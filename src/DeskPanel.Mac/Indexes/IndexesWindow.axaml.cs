using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Mac.Services;
using DeskPanel.Search.Indexing;

namespace DeskPanel.Mac.Indexes;

/// <summary>Creates, edits and refreshes the folder indexes (Lucene) that live in this Mac's local storage.</summary>
public partial class IndexesWindow : Window
{
    private const string DefaultNamePrefix = "Índice";
    private static readonly Regex DefaultNamePattern = new($@"^{DefaultNamePrefix} \d+$");

    private readonly MacServices _services = null!;
    private readonly ObservableCollection<IndexDefinition> _definitions = new();
    private HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;

    public IndexesWindow() => InitializeComponent();

    public IndexesWindow(MacServices services) : this()
    {
        _services = services;
        foreach (var definition in services.Config.App.Search.Indexes)
        {
            _definitions.Add(definition);
        }

        DefinitionsList.ItemsSource = _definitions;
        DefinitionsList.SelectionChanged += (_, _) => OnSelectionChanged();
        NewButton.Click += (_, _) => NewIndex();
        DeleteButton.Click += async (_, _) => await DeleteIndexAsync();
        BrowseButton.Click += async (_, _) => await BrowseBaseRootAsync();
        SaveButton.Click += (_, _) => Save();
        UpdateButton.Click += async (_, _) => await RunIndexerAsync(rebuild: false);
        RebuildButton.Click += async (_, _) => await RebuildAsync();
        CancelButton.Click += (_, _) => _cancellation?.Cancel();
        UpdateAllButton.Click += async (_, _) => await UpdateAllAsync();
        LogButton.Click += (_, _) => _services.OpenLog();
        OpenStorageButton.Click += (_, _) => OpenStorage();

        DefinitionsList.SelectedItem = _definitions.FirstOrDefault();
        OnSelectionChanged();
    }

    private IndexDefinition? Selected => DefinitionsList.SelectedItem as IndexDefinition;

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateAllButton.IsEnabled = !busy;
        NewButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy && Selected is not null;
        UpdateButton.IsEnabled = !busy && Selected is not null;
        RebuildButton.IsEnabled = !busy && Selected is not null;
        SaveButton.IsEnabled = !busy && Selected is not null;
        CancelButton.IsEnabled = busy;
    }

    private void OnSelectionChanged()
    {
        FolderTree.ItemsSource = null;
        var value = Selected;
        var hasSelection = value is not null;
        HintText.IsVisible = !hasSelection;
        FormTop.IsEnabled = hasSelection;
        FolderTree.IsEnabled = hasSelection;
        TreeLabel.IsVisible = hasSelection;
        SetBusy(_isBusy);

        if (value is null)
        {
            NameBox.Text = BaseRootBox.Text = ExtensionsBox.Text = InfoText.Text = "";
            return;
        }

        NameBox.Text = value.Name;
        BaseRootBox.Text = value.BaseRoot;
        ExtensionsBox.Text = string.Join(" ", value.Extensions);
        RecursiveBox.IsChecked = value.Recursive;
        _selection = new HashSet<string>(FolderIndexer.NormalizeIncludedFolders(value.IncludedFolders), StringComparer.OrdinalIgnoreCase);
        RefreshInfo(value);
        if (!string.IsNullOrWhiteSpace(value.BaseRoot))
        {
            LoadTree();
        }
    }

    private void NewIndex()
    {
        var definition = new IndexDefinition
        {
            Name = $"{DefaultNamePrefix} {_definitions.Count + 1}",
            Extensions = _services.Indexer.SupportedExtensions.ToList(),
        };
        _services.Config.App.Search.Indexes.Add(definition);
        _definitions.Add(definition);
        DefinitionsList.SelectedItem = definition;
    }

    private async Task DeleteIndexAsync()
    {
        if (Selected is not { } definition)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(this, $"¿Quitar '{definition.Name}' de la configuración?\nLos ficheros del índice no se borran.", "Quitar"))
        {
            return;
        }

        _services.Config.App.Search.Indexes.Remove(definition);
        _definitions.Remove(definition);
        _services.Config.Save();
        DefinitionsList.SelectedItem = _definitions.FirstOrDefault();
    }

    private async Task BrowseBaseRootAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Carpeta base del índice",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        // Absolute path: the Mac profile has no aliases, so MakePortable leaves it as it is.
        BaseRootBox.Text = _services.Config.Paths.MakePortable(path);
        UseFolderNameIfDefault(path);
        _selection.Clear();
        LoadTree();
    }

    /// <summary>Renames an index still carrying its generated "Índice N" name after the base folder it now points at.</summary>
    private void UseFolderNameIfDefault(string folder)
    {
        if (!DefaultNamePattern.IsMatch((NameBox.Text ?? "").Trim()))
        {
            return;
        }

        var candidate = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        candidate = string.Join("_", candidate.Split(Path.GetInvalidFileNameChars()));
        if (candidate.Length == 0)
        {
            return;
        }

        // The name doubles as the on-disk folder, so two indexes cannot share it.
        var unique = candidate;
        for (var n = 2; _definitions.Any(d => d != Selected && string.Equals(d.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
        {
            unique = $"{candidate} ({n})";
        }

        NameBox.Text = unique;
    }

    private void LoadTree()
    {
        if (!_services.Config.Paths.TryResolve(BaseRootBox.Text, out var resolved) || !Directory.Exists(resolved))
        {
            FolderTree.ItemsSource = null;
            StatusText.Text = "La carpeta base no existe.";
            return;
        }

        var root = new FolderNode(resolved, "", resolved, null, _selection);
        FolderTree.ItemsSource = new[] { root };
        root.IsExpanded = true;
        StatusText.Text = "";
    }

    private void Save()
    {
        if (Selected is not { } definition)
        {
            return;
        }

        ApplyEdits(definition);
        _services.Config.Save();
        StatusText.Text = "Configuración guardada.";
    }

    private async Task RebuildAsync()
    {
        if (await Dialogs.ConfirmAsync(this, "Se borrará y regenerará el índice completo. ¿Continuar?"))
        {
            await RunIndexerAsync(rebuild: true);
        }
    }

    private void OpenStorage()
    {
        if (_services.ResolveIndexStorage() is { } storage)
        {
            Directory.CreateDirectory(storage);
            _services.Opener.OpenFolder(storage);
        }
    }

    private async Task RunIndexerAsync(bool rebuild)
    {
        if (Selected is not { } definition || _isBusy)
        {
            StatusText.Text = "Selecciona un índice de la lista o crea uno con «Nuevo».";
            return;
        }

        ApplyEdits(definition);
        _services.Config.Save();

        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        Progress.Value = 0;

        var progress = new Progress<IndexProgress>(p =>
        {
            Progress.Maximum = Math.Max(1, p.Total);
            Progress.Value = p.Processed;
            StatusText.Text = p.Total > 0 ? $"{p.Phase}: {p.Processed}/{p.Total}  {p.CurrentFile}" : p.Phase;
        });

        try
        {
            var result = await UpdateOneAsync(definition, rebuild, progress, _cancellation.Token);
            StatusText.Text = result.Failures.Count == 0
                ? result.ToString()
                : $"{result} — primer error: {result.Failures[0]} (más detalle en el registro)";
            AppLog.Info($"Index '{definition.Name}' updated: {result}");
            foreach (var failure in result.Failures)
            {
                AppLog.Warn($"Index '{definition.Name}': {failure}");
            }
        }
        catch (OperationCanceledException)
        {
            // Work done so far is committed when the writer closes, so the next update resumes instead of restarting.
            StatusText.Text = "Cancelado. Lo ya procesado se conserva; la próxima actualización continúa desde ahí.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Indexing '{definition.Name}' failed.", ex);
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
            RefreshInfo(definition);
        }
    }

    private Task<IndexUpdateResult> UpdateOneAsync(IndexDefinition definition, bool rebuild, IProgress<IndexProgress> progress, CancellationToken ct)
    {
        if (!_services.Config.Paths.TryResolve(definition.BaseRoot, out var baseRoot))
        {
            throw new InvalidOperationException($"La carpeta base «{definition.BaseRoot}» no se puede resolver.");
        }

        if (!Directory.Exists(baseRoot))
        {
            throw new DirectoryNotFoundException($"La carpeta base no existe: {baseRoot}");
        }

        var storage = _services.ResolveIndexStorage() ?? throw new InvalidOperationException("No se pudo resolver el almacén de índices.");
        return _services.Indexer.UpdateAsync(definition, baseRoot, IndexLocator.GetIndexDirectory(storage, definition), rebuild, progress, ct);
    }

    /// <summary>Updates every index one after another; one that fails does not stop the others.</summary>
    private async Task UpdateAllAsync()
    {
        if (_definitions.Count == 0)
        {
            StatusText.Text = "No hay índices configurados.";
            return;
        }

        // What is on screen for the selected index (folders ticked, extensions…) counts, so save it before reading the list.
        if (Selected is { } selected)
        {
            ApplyEdits(selected);
        }

        _services.Config.Save();

        var definitions = _definitions.ToList();
        var summary = new List<string>();
        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            for (var i = 0; i < definitions.Count; i++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var definition = definitions[i];
                var prefix = $"Índice {i + 1} de {definitions.Count}: {definition.Name}";
                Progress.Value = 0;
                var progress = new Progress<IndexProgress>(p =>
                {
                    Progress.Maximum = Math.Max(1, p.Total);
                    Progress.Value = p.Processed;
                    StatusText.Text = p.Total > 0 ? $"{prefix} — {p.Phase}: {p.Processed}/{p.Total}" : $"{prefix} — {p.Phase}";
                });

                try
                {
                    var result = await UpdateOneAsync(definition, rebuild: false, progress, _cancellation.Token);
                    AppLog.Info($"Index '{definition.Name}' updated: {result}");
                    foreach (var failure in result.Failures)
                    {
                        AppLog.Warn($"Index '{definition.Name}': {failure}");
                    }

                    summary.Add($"{definition.Name}: {result}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Indexing '{definition.Name}' failed.", ex);
                    summary.Add($"{definition.Name}: ERROR {ex.Message}");
                }
            }

            StatusText.Text = "Terminado. " + string.Join(" | ", summary);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelado. " + string.Join(" | ", summary);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
            if (Selected is { } current)
            {
                RefreshInfo(current);
            }
        }
    }

    private void ApplyEdits(IndexDefinition definition)
    {
        var oldName = definition.Name;
        var newName = string.IsNullOrWhiteSpace(NameBox.Text) ? oldName : NameBox.Text.Trim();
        definition.Name = newName;
        definition.BaseRoot = (BaseRootBox.Text ?? "").Trim();
        definition.Extensions = (ExtensionsBox.Text ?? "")
            .Split(new[] { ' ', ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct()
            .ToList();
        definition.Recursive = RecursiveBox.IsChecked == true;
        definition.IncludedFolders = FolderIndexer.NormalizeIncludedFolders(_selection);

        if (!string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            // The name is also the folder name on disk; a rename otherwise orphans the existing index.
            TryRenameIndexFolder(oldName, definition);

            // IndexDefinition has no change notification, so re-assign the list to refresh the label.
            var keep = definition;
            DefinitionsList.ItemsSource = null;
            DefinitionsList.ItemsSource = _definitions;
            DefinitionsList.SelectedItem = keep;
        }
    }

    private void TryRenameIndexFolder(string oldName, IndexDefinition definition)
    {
        if (_services.ResolveIndexStorage() is not { } storage)
        {
            return;
        }

        var oldDirectory = IndexLocator.GetIndexDirectory(storage, new IndexDefinition { Name = oldName });
        var newDirectory = IndexLocator.GetIndexDirectory(storage, definition);
        if (Directory.Exists(oldDirectory) && !Directory.Exists(newDirectory))
        {
            Directory.Move(oldDirectory, newDirectory);
        }
    }

    private void RefreshInfo(IndexDefinition definition)
    {
        if (_services.ResolveIndexStorage() is not { } storage)
        {
            InfoText.Text = "Almacén de índices no resoluble.";
            return;
        }

        var directory = IndexLocator.GetIndexDirectory(storage, definition);
        var meta = IndexMetadata.TryLoad(directory);
        InfoText.Text = meta is null
            ? $"Sin crear. Se guardará en {directory}"
            : $"{meta.DocumentCount} documentos, actualizado {meta.LastUpdatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} en {meta.LastUpdatedOnMachine}. Carpeta: {directory}"
              + (meta.SchemaVersion != IndexSchema.Version ? "  (versión de esquema antigua: se reconstruirá)" : "");
    }
}
