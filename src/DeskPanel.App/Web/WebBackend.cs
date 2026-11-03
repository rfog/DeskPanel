using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskPanel.Core.Configuration;
using DeskPanel.Remote;
using DeskPanel.Search.Extraction;
using DeskPanel.Search.Indexing;
using DeskPanel.Search.Searching;

namespace DeskPanel.App.Web;

/// <summary>
/// The application side of the web interface. It is the single place that decides what the web may touch: only the
/// indexes ticked in Settings › Web, and only files that really live under such an index's base folder.
/// </summary>
internal sealed class WebBackend : IWebBackend
{
    private readonly Shell _shell;

    public WebBackend(Shell shell) => _shell = shell;

    private WebServerConfig Settings => _shell.Config.Machine.Web;

    // Copies, because the configuration lists are edited on the UI thread while requests arrive on others.
    private IndexDefinition[] PublishedDefinitions() =>
        _shell.Config.App.Search.Indexes.ToArray()
            .Where(d => Settings.PublishedIndexes.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    private string? StorageFolder() =>
        _shell.Config.Paths.TryResolve(_shell.Config.App.Search.IndexStorageFolder, out var storage) ? storage : null;

    public IReadOnlyList<WebIndexInfo> GetIndexes()
    {
        var storage = StorageFolder();
        var list = new List<WebIndexInfo>();
        if (storage is null)
        {
            return list;
        }

        foreach (var definition in PublishedDefinitions())
        {
            // An index that was never built has nothing to search; leave it out rather than offering an empty choice.
            var meta = SafeLoadMeta(IndexLocator.GetIndexDirectory(storage, definition));
            if (meta is not null)
            {
                list.Add(new WebIndexInfo(definition.Name, meta.DocumentCount, meta.LastUpdatedUtc.UtcDateTime));
            }
        }

        return list;
    }

    private static IndexMetadata? SafeLoadMeta(string directory)
    {
        try
        {
            return IndexMetadata.TryLoad(directory);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<SearchResult> SearchAsync(string query, IReadOnlyCollection<string> indexNames, int maxHits, CancellationToken ct)
    {
        var storage = StorageFolder();
        var targets = new List<SearchTarget>();
        if (storage is not null)
        {
            foreach (var definition in PublishedDefinitions().Where(d => indexNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)))
            {
                if (_shell.Config.Paths.TryResolve(definition.BaseRoot, out var baseRoot))
                {
                    targets.Add(new SearchTarget(definition.Name, IndexLocator.GetIndexDirectory(storage, definition), baseRoot));
                }
            }
        }

        var result = await _shell.Search.SearchAsync(targets, query, maxHits, ct);

        // The same history the desktop search window uses, so a search made on the phone shows up there too.
        _shell.SearchHistory.Add(query);
        return result;
    }

    public WebFileRef? ResolveFile(string indexName, string relativePath)
    {
        var definition = PublishedDefinitions().FirstOrDefault(d => string.Equals(d.Name, indexName, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !_shell.Config.Paths.TryResolve(definition.BaseRoot, out var baseRoot))
        {
            return null;
        }

        // Path.Combine would silently discard the base if the "relative" part were rooted; refuse that outright.
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseRoot)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            // The check that matters: whatever the id said, the file must stay inside the published index's folder.
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return null;
            }

            return new WebFileRef(full, Path.GetFileName(full));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // The same extractors the indexer and the desktop preview use, so what the phone shows is what was searched.
    public Task<string> ReadTextAsync(string fullPath, CancellationToken ct) => Task.Run(() =>
        _shell.Extractors.TryGet(Path.GetExtension(fullPath), out var extractor)
            ? extractor.Extract(fullPath)
            : TextDecoding.ReadAllTextSmart(fullPath), ct);

    public IReadOnlyList<SearchHistoryEntry> GetHistory() => _shell.SearchHistory.Entries;

    public void RemoveHistory(string query) => _shell.SearchHistory.Remove(query);

    public void ClearHistory() => _shell.SearchHistory.Clear();
}
