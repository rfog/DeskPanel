using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Search.Indexing;
using DeskPanel.Search.Searching;

namespace DeskPanel.App.Search;

internal sealed partial class SelectableIndex : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public SelectableIndex(IndexDefinition definition, string info)
    {
        Definition = definition;
        Info = info;
    }

    public IndexDefinition Definition { get; }
    public string Info { get; }
}

internal sealed partial class SearchViewModel : ObservableObject
{
    private readonly Shell _shell;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private SearchHit? _selectedHit;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private SearchHistoryEntry? _selectedHistory;

    public SearchViewModel(Shell shell)
    {
        _shell = shell;
        ReloadIndexes();
        RefreshHistory();
    }

    public ObservableCollection<SearchHistoryEntry> History { get; } = new();

    [RelayCommand]
    private void RemoveHistory(SearchHistoryEntry? entry)
    {
        if (entry is not null)
        {
            _shell.SearchHistory.Remove(entry.Query);
            RefreshHistory();
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (History.Count == 0)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show("¿Borrar todo el histórico de búsquedas?", "DeskPanel",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer == System.Windows.MessageBoxResult.Yes)
        {
            _shell.SearchHistory.Clear();
            RefreshHistory();
        }
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var entry in _shell.SearchHistory.Entries)
        {
            History.Add(entry);
        }
    }

    partial void OnSelectedHistoryChanged(SearchHistoryEntry? value)
    {
        if (value is null)
        {
            return;
        }

        Query = value.Query;

        // Reset so the same entry can be picked again; the list is a launcher, not a persistent selection.
        SelectedHistory = null;
        _ = SearchCommand.ExecuteAsync(null);
    }

    /// <summary>Raised when the selected hit changes; the view decides how to render text vs. PDF.</summary>
    public event Action<SearchHit, IReadOnlyList<string>>? PreviewRequested;

    public ObservableCollection<SelectableIndex> Indexes { get; } = new();
    public ObservableCollection<SearchHit> Results { get; } = new();
    public IReadOnlyList<string> HighlightTerms { get; private set; } = Array.Empty<string>();

    public void ReloadIndexes()
    {
        // Called whenever the window regains focus (the index manager may have changed things); keep the user's choices.
        var deselected = Indexes.Where(i => !i.IsSelected).Select(i => i.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Indexes.Clear();
        var storage = ResolveStorage();
        foreach (var definition in _shell.Config.App.Search.Indexes)
        {
            var info = "sin crear";
            if (storage is not null)
            {
                var meta = IndexMetadata.TryLoad(IndexLocator.GetIndexDirectory(storage, definition));
                if (meta is not null)
                {
                    info = $"{meta.DocumentCount} docs · {meta.LastUpdatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}";
                }
            }

            Indexes.Add(new SelectableIndex(definition, info) { IsSelected = !deselected.Contains(definition.Name) });
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var storage = ResolveStorage();
        if (storage is null || string.IsNullOrWhiteSpace(Query))
        {
            return;
        }

        var targets = new List<SearchTarget>();
        foreach (var index in Indexes.Where(i => i.IsSelected))
        {
            if (_shell.Config.Paths.TryResolve(index.Definition.BaseRoot, out var baseRoot))
            {
                targets.Add(new SearchTarget(index.Definition.Name, IndexLocator.GetIndexDirectory(storage, index.Definition), baseRoot));
            }
        }

        if (targets.Count == 0)
        {
            Status = "Selecciona al menos un índice con su carpeta base resuelta en esta máquina.";
            return;
        }

        IsBusy = true;
        Status = "Buscando…";
        try
        {
            var result = await _shell.Search.SearchAsync(targets, Query, _shell.Config.App.Search.MaxResults);
            HighlightTerms = result.HighlightTerms;
            Results.Clear();
            foreach (var hit in result.Hits)
            {
                Results.Add(hit);
            }

            Status = result.TotalHits > result.Hits.Count
                ? $"{result.Hits.Count} de {result.TotalHits} resultados"
                : $"{result.TotalHits} resultados";
            SelectedHit = Results.FirstOrDefault();

            _shell.SearchHistory.Add(Query);
            RefreshHistory();
        }
        catch (Exception ex)
        {
            AppLog.Error("Search failed.", ex);
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenExternal(SearchHit? hit)
    {
        if (hit is not null)
        {
            _shell.OpenWithShell(hit.FullPath);
        }
    }

    [RelayCommand]
    private void Reveal(SearchHit? hit)
    {
        if (hit is not null)
        {
            _shell.RevealFile(hit.FullPath);
        }
    }

    [RelayCommand]
    private void OpenInPreviewWindow(SearchHit? hit)
    {
        if (hit is not null && hit.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _shell.OpenPdfPreview(hit.FullPath, string.Join(' ', HighlightTerms));
        }
    }

    partial void OnSelectedHitChanged(SearchHit? value)
    {
        if (value is not null)
        {
            PreviewRequested?.Invoke(value, HighlightTerms);
        }
    }

    private string? ResolveStorage() =>
        _shell.Config.Paths.TryResolve(_shell.Config.App.Search.IndexStorageFolder, out var storage) ? storage : null;
}
