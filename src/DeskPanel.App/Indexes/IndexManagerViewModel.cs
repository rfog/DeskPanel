using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Search.Indexing;
using Microsoft.Win32;

namespace DeskPanel.App.Indexes;

internal sealed partial class IndexManagerViewModel : ObservableObject
{
    private const string DefaultNamePrefix = "Índice";
    private static readonly System.Text.RegularExpressions.Regex DefaultNamePattern = new($@"^{DefaultNamePrefix} \d+$");

    private readonly Shell _shell;
    private HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasNoSelection))]
    private IndexDefinition? _selectedDefinition;

    public bool HasSelection => SelectedDefinition is not null;
    public bool HasNoSelection => SelectedDefinition is null;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _baseRoot = "";

    [ObservableProperty]
    private string _extensionsText = "";

    [ObservableProperty]
    private bool _recursive = true;

    [ObservableProperty]
    private bool _skipCloudPlaceholders = true;

    [ObservableProperty]
    private string _indexInfo = "";

    [ObservableProperty]
    private string _status = "";

    // Whenever IsBusy changes, WPF re-asks the command whether it may run (CanUpdateAll), which greys out its button.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllIndexCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    public IndexManagerViewModel(Shell shell)
    {
        _shell = shell;
        foreach (var definition in shell.Config.App.Search.Indexes)
        {
            Definitions.Add(definition);
        }

        SelectedDefinition = Definitions.FirstOrDefault();
    }

    public ObservableCollection<IndexDefinition> Definitions { get; } = new();
    public ObservableCollection<FolderNode> Tree { get; } = new();

    [RelayCommand]
    private void NewIndex()
    {
        var definition = new IndexDefinition
        {
            Name = $"{DefaultNamePrefix} {Definitions.Count + 1}",
            Extensions = _shell.Indexer.SupportedExtensions.ToList(),
        };
        _shell.Config.App.Search.Indexes.Add(definition);
        Definitions.Add(definition);
        SelectedDefinition = definition;
    }

    [RelayCommand]
    private void DeleteIndex()
    {
        if (SelectedDefinition is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"¿Quitar '{SelectedDefinition.Name}' de la configuración?\nLos ficheros del índice no se borran.",
            "DeskPanel", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _shell.Config.App.Search.Indexes.Remove(SelectedDefinition);
        Definitions.Remove(SelectedDefinition);
        _shell.Config.Save();
        SelectedDefinition = Definitions.FirstOrDefault();
    }

    [RelayCommand]
    private void BrowseBaseRoot()
    {
        var dialog = new OpenFolderDialog { Title = "Carpeta base del índice" };
        if (_shell.Config.Paths.TryResolve(BaseRoot, out var current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog() == true)
        {
            // Store the aliased form so the definition stays valid on machines where the root lives elsewhere.
            BaseRoot = _shell.Config.Paths.MakePortable(dialog.FolderName);
            UseFolderNameIfDefault(dialog.FolderName);
            _selection.Clear();
            LoadTree();
        }
    }

    /// <summary>Renames an index still carrying its generated "Índice N" name after the base folder it now points at.</summary>
    private void UseFolderNameIfDefault(string folder)
    {
        if (!DefaultNamePattern.IsMatch(Name.Trim()))
        {
            return;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var candidate = Path.GetFileName(trimmed);
        if (candidate.Length == 0)
        {
            candidate = trimmed.TrimEnd(':', '\\', '/'); // drive root such as D:\
        }

        candidate = string.Join("_", candidate.Split(Path.GetInvalidFileNameChars()));
        if (candidate.Length == 0)
        {
            return;
        }

        // The name doubles as the on-disk folder, so two indexes cannot share it.
        var unique = candidate;
        for (var n = 2; Definitions.Any(d => d != SelectedDefinition && string.Equals(d.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
        {
            unique = $"{candidate} ({n})";
        }

        Name = unique;
    }

    [RelayCommand]
    private void LoadTree()
    {
        Tree.Clear();
        if (!_shell.Config.Paths.TryResolve(BaseRoot, out var resolved) || !Directory.Exists(resolved))
        {
            Status = "La carpeta base no existe o su alias no está definido en machine.json.";
            return;
        }

        var root = new FolderNode(resolved, "", resolved, null, _selection);
        Tree.Add(root);
        root.IsExpanded = true;
        Status = "";
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedDefinition is null)
        {
            return;
        }

        ApplyEdits(SelectedDefinition);
        _shell.Config.Save();
        Status = "Configuración guardada.";
    }

    [RelayCommand]
    private Task UpdateIndexAsync() => RunIndexerAsync(rebuild: false);

    [RelayCommand]
    private Task RebuildIndexAsync()
    {
        var answer = MessageBox.Show("Se borrará y regenerará el índice completo. ¿Continuar?", "DeskPanel",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes ? RunIndexerAsync(rebuild: true) : Task.CompletedTask;
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void OpenIndexStorage()
    {
        if (_shell.Config.Paths.TryResolve(_shell.Config.App.Search.IndexStorageFolder, out var storage))
        {
            Directory.CreateDirectory(storage);
            _shell.OpenFolder(storage);
        }
    }

    partial void OnSelectedDefinitionChanged(IndexDefinition? value)
    {
        Tree.Clear();
        if (value is null)
        {
            Name = BaseRoot = ExtensionsText = IndexInfo = "";
            return;
        }

        Name = value.Name;
        BaseRoot = value.BaseRoot;
        ExtensionsText = string.Join(" ", value.Extensions);
        Recursive = value.Recursive;
        SkipCloudPlaceholders = value.SkipCloudPlaceholders;
        _selection = new HashSet<string>(FolderIndexer.NormalizeIncludedFolders(value.IncludedFolders), StringComparer.OrdinalIgnoreCase);
        RefreshIndexInfo(value);
        if (!string.IsNullOrWhiteSpace(BaseRoot))
        {
            LoadTree();
        }
    }

    private async Task RunIndexerAsync(bool rebuild)
    {
        var definition = SelectedDefinition;
        if (definition is null)
        {
            Status = "Selecciona un índice de la lista o crea uno con «Nuevo».";
            return;
        }

        ApplyEdits(definition);
        _shell.Config.Save();

        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;

        var progress = new Progress<IndexProgress>(p =>
        {
            ProgressMaximum = Math.Max(1, p.Total);
            ProgressValue = p.Processed;
            Status = p.Total > 0 ? $"{p.Phase}: {p.Processed}/{p.Total}  {p.CurrentFile}" : p.Phase;
        });

        try
        {
            var result = await UpdateOneAsync(definition, rebuild, progress, _cancellation.Token);
            Status = result.ToString();
            AppLog.Info($"Index '{definition.Name}' updated: {result}");
            foreach (var failure in result.Failures)
            {
                AppLog.Warn($"Index '{definition.Name}': {failure}");
            }
        }
        catch (OperationCanceledException)
        {
            // Work done so far is committed when the writer closes, so the next update resumes instead of restarting.
            Status = "Cancelado. Lo ya procesado se conserva; la próxima actualización continúa desde ahí.";
        }
        catch (Exception ex)
        {
            AppLog.Error($"Indexing '{definition.Name}' failed.", ex);
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
            RefreshIndexInfo(definition);
        }
    }

    /// <summary>The indexing of one definition, shared by "Actualizar índice", "Reconstruir" and "Actualizar todos".</summary>
    private Task<IndexUpdateResult> UpdateOneAsync(IndexDefinition definition, bool rebuild, IProgress<IndexProgress> progress, CancellationToken ct)
    {
        if (!_shell.Config.Paths.TryResolve(definition.BaseRoot, out var baseRoot))
        {
            throw new InvalidOperationException($"La carpeta base «{definition.BaseRoot}» no se puede resolver en esta máquina.");
        }

        if (!Directory.Exists(baseRoot))
        {
            throw new DirectoryNotFoundException($"La carpeta base no existe: {baseRoot}");
        }

        if (!_shell.Config.Paths.TryResolve(_shell.Config.App.Search.IndexStorageFolder, out var storage))
        {
            throw new InvalidOperationException("No se pudo resolver el almacén de índices en esta máquina.");
        }

        return _shell.Indexer.UpdateAsync(definition, baseRoot, IndexLocator.GetIndexDirectory(storage, definition), rebuild, progress, ct);
    }

    private bool CanUpdateAll() => !IsBusy;

    /// <summary>
    /// Bound from XAML as <c>{Binding UpdateAllIndexCommand}</c>. The toolkit's source generator turns this method into a
    /// public <c>UpdateAllIndexCommand</c> property (method name minus "Async", plus "Command").
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllIndexAsync()
    {
        if (Definitions.Count == 0)
        {
            Status = "No hay índices configurados.";
            return;
        }

        // What is on screen for the selected index (folders ticked, extensions…) counts, so save it before reading the list.
        if (SelectedDefinition is { } selected)
        {
            ApplyEdits(selected);
        }

        _shell.Config.Save();

        var definitions = Definitions.ToList();
        var progress = new UpdateAllViewModel(definitions.Count);

        // The dialog sits over this window. A view model normally avoids touching windows; here the owner is found by
        // its DataContext so the command needs no reference to the view.
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w.DataContext, this));
        var dialog = new UpdateAllWindow(progress) { Owner = owner };

        IsBusy = true;
        try
        {
            // Order matters. First start the work: it runs on the UI thread until its first await and then continues
            // as each await completes. ShowDialog() then blocks this method but keeps pumping messages, which is what
            // lets those continuations (and the Progress<T> updates) run while the dialog is open.
            var work = RunAllAsync(definitions, progress);
            dialog.ShowDialog();
            await work;
        }
        finally
        {
            IsBusy = false;
            Status = progress.Summary;
            if (SelectedDefinition is { } current)
            {
                RefreshIndexInfo(current);
            }
        }
    }

    private async Task RunAllAsync(List<IndexDefinition> definitions, UpdateAllViewModel vm)
    {
        try
        {
            for (var i = 0; i < definitions.Count; i++)
            {
                vm.Token.ThrowIfCancellationRequested();
                var definition = definitions[i];
                vm.BeginIndex(i + 1, definition.Name);

                try
                {
                    // Progress<T> was created here, on the UI thread, so Report() always runs on it.
                    var result = await UpdateOneAsync(definition, rebuild: false, new Progress<IndexProgress>(vm.Report), vm.Token);
                    AppLog.Info($"Index '{definition.Name}' updated: {result}");
                    foreach (var failure in result.Failures)
                    {
                        AppLog.Warn($"Index '{definition.Name}': {failure}");
                    }

                    vm.CompleteIndex(definition.Name, result.ToString(), failed: false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One broken index (missing folder, locked files) must not stop the others.
                    AppLog.Error($"Indexing '{definition.Name}' failed.", ex);
                    vm.CompleteIndex(definition.Name, ex.Message, failed: true);
                }
            }

            vm.Finish();
        }
        catch (OperationCanceledException)
        {
            vm.FinishCancelled();
        }
    }

    private void ApplyEdits(IndexDefinition definition)
    {
        var oldName = definition.Name;
        definition.Name = string.IsNullOrWhiteSpace(Name) ? oldName : Name.Trim();
        definition.BaseRoot = BaseRoot.Trim();
        definition.Extensions = ExtensionsText
            .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct()
            .ToList();
        definition.Recursive = Recursive;
        definition.SkipCloudPlaceholders = SkipCloudPlaceholders;
        definition.IncludedFolders = FolderIndexer.NormalizeIncludedFolders(_selection);

        if (!string.Equals(oldName, definition.Name, StringComparison.Ordinal))
        {
            // The name is also the folder name on disk; a rename otherwise orphans the existing index.
            TryRenameIndexFolder(oldName, definition);

            // IndexDefinition is a plain config POCO without change notification; refreshing the view updates the list label.
            System.Windows.Data.CollectionViewSource.GetDefaultView(Definitions).Refresh();
        }
    }

    private void TryRenameIndexFolder(string oldName, IndexDefinition definition)
    {
        if (!_shell.Config.Paths.TryResolve(_shell.Config.App.Search.IndexStorageFolder, out var storage))
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

    private void RefreshIndexInfo(IndexDefinition definition)
    {
        if (!_shell.Config.Paths.TryResolve(_shell.Config.App.Search.IndexStorageFolder, out var storage))
        {
            IndexInfo = "Almacén de índices no resoluble.";
            return;
        }

        var directory = IndexLocator.GetIndexDirectory(storage, definition);
        var meta = IndexMetadata.TryLoad(directory);
        IndexInfo = meta is null
            ? $"Sin crear. Se guardará en {directory}"
            : $"{meta.DocumentCount} documentos, actualizado {meta.LastUpdatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} en {meta.LastUpdatedOnMachine}. Carpeta: {directory}"
              + (meta.SchemaVersion != IndexSchema.Version ? "  (versión de esquema antigua: se reconstruirá)" : "");
    }
}
