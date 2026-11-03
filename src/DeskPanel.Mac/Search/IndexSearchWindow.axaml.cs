using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Mac.Services;
using DeskPanel.Search.Indexing;
using DeskPanel.Search.Searching;

namespace DeskPanel.Mac.Search;

/// <summary>An index with the tick that says whether a search includes it.</summary>
public sealed class SelectableIndex : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public SelectableIndex(IndexDefinition definition, string info)
    {
        Definition = definition;
        Label = $"{definition.Name} ({info})";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IndexDefinition Definition { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>Full-text search over the indexes made in this Mac's own storage. PDFs are read and annotated in DEVONthink, so there is no viewer here.</summary>
public partial class IndexSearchWindow : Window
{
    private readonly MacServices _services = null!;
    private readonly ObservableCollection<SearchHit> _results = new();
    private bool _refreshingHistory;

    public IndexSearchWindow() => InitializeComponent();

    public IndexSearchWindow(MacServices services) : this()
    {
        _services = services;
        ResultsList.ItemsSource = _results;
        ReloadIndexes();
        RefreshHistory();

        SearchButton.Click += async (_, _) => await SearchAsync();
        QueryBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                await SearchAsync();
            }
        };
        HistoryBox.SelectionChanged += async (_, _) =>
        {
            if (!_refreshingHistory && HistoryBox.SelectedItem is SearchHistoryEntry entry)
            {
                QueryBox.Text = entry.Query;
                await SearchAsync();
            }
        };
        ClearHistoryButton.Click += async (_, _) => await ClearHistoryAsync();
        OpenButton.Click += (_, _) => OpenSelected(reveal: false);
        RevealButton.Click += (_, _) => OpenSelected(reveal: true);
        ResultsList.DoubleTapped += (_, _) => OpenSelected(reveal: false);

        // The index manager may have changed things while this window was in the background.
        Activated += (_, _) => ReloadIndexes();
    }

    private void ReloadIndexes()
    {
        var current = IndexesList.ItemsSource as IEnumerable<SelectableIndex>;
        var deselected = current?.Where(i => !i.IsSelected).Select(i => i.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                         ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storage = _services.ResolveIndexStorage();
        var list = new List<SelectableIndex>();
        foreach (var definition in _services.Config.App.Search.Indexes)
        {
            var info = "sin crear";
            if (storage is not null && IndexMetadata.TryLoad(IndexLocator.GetIndexDirectory(storage, definition)) is { } meta)
            {
                info = $"{meta.DocumentCount} docs · {meta.LastUpdatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}";
            }

            list.Add(new SelectableIndex(definition, info) { IsSelected = !deselected.Contains(definition.Name) });
        }

        IndexesList.ItemsSource = list;
    }

    private void RefreshHistory()
    {
        _refreshingHistory = true;
        HistoryBox.ItemsSource = _services.SearchHistory.Entries.ToList();
        HistoryBox.SelectedItem = null;
        _refreshingHistory = false;
    }

    private async Task ClearHistoryAsync()
    {
        if (_services.SearchHistory.Entries.Count > 0 && await Dialogs.ConfirmAsync(this, "¿Borrar todo el histórico de búsquedas?", "Borrar"))
        {
            _services.SearchHistory.Clear();
            RefreshHistory();
        }
    }

    private async Task SearchAsync()
    {
        var query = (QueryBox.Text ?? "").Trim();
        var storage = _services.ResolveIndexStorage();
        if (storage is null || query.Length == 0)
        {
            return;
        }

        var targets = new List<SearchTarget>();
        foreach (var index in (IndexesList.ItemsSource as IEnumerable<SelectableIndex> ?? []).Where(i => i.IsSelected))
        {
            if (_services.Config.Paths.TryResolve(index.Definition.BaseRoot, out var baseRoot))
            {
                targets.Add(new SearchTarget(index.Definition.Name, IndexLocator.GetIndexDirectory(storage, index.Definition), baseRoot));
            }
        }

        if (targets.Count == 0)
        {
            StatusText.Text = "Selecciona al menos un índice con su carpeta base válida.";
            return;
        }

        SearchButton.IsEnabled = false;
        StatusText.Text = "Buscando…";
        try
        {
            var result = await _services.Search.SearchAsync(targets, query, _services.Config.App.Search.MaxResults);
            _results.Clear();
            foreach (var hit in result.Hits)
            {
                _results.Add(hit);
            }

            StatusText.Text = result.TotalHits > result.Hits.Count
                ? $"{result.Hits.Count} de {result.TotalHits} resultados"
                : $"{result.TotalHits} resultados";
            ResultsList.SelectedItem = _results.FirstOrDefault();

            _services.SearchHistory.Add(query);
            RefreshHistory();
        }
        catch (Exception ex)
        {
            AppLog.Error("Search failed.", ex);
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void OpenSelected(bool reveal)
    {
        if (ResultsList.SelectedItem is not SearchHit hit)
        {
            return;
        }

        if (!File.Exists(hit.FullPath))
        {
            StatusText.Text = $"El fichero ya no existe: {hit.FullPath}";
            return;
        }

        if (reveal)
        {
            _services.Opener.RevealFile(hit.FullPath);
        }
        else
        {
            _services.Opener.OpenFile(hit.FullPath);
        }
    }
}
