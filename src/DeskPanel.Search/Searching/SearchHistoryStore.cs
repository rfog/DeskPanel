using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.IO;

namespace DeskPanel.Search.Searching;

public sealed class SearchHistoryEntry
{
    public string Query { get; set; } = "";
    public DateTime LastUsedUtc { get; set; }
    public int Uses { get; set; } = 1;

    [JsonIgnore]
    public DateTime LastUsedLocal => LastUsedUtc.ToLocalTime();
}

/// <summary>
/// Most-recent-first list of queries. It lives in the per-machine app folder rather than the synced config
/// directory: it is rewritten on every search, which is exactly the write pattern that makes sync clients
/// produce conflict copies.
/// </summary>
public sealed class SearchHistoryStore
{
    private readonly string _path;
    private readonly int _maxEntries;
    // The desktop window and the web server (request threads) share one store, so every access is under this lock.
    private readonly object _gate = new();
    private List<SearchHistoryEntry> _entries = new();

    public SearchHistoryStore(string path, int maxEntries = 200)
    {
        _path = path;
        _maxEntries = maxEntries;
    }

    /// <summary>A snapshot copy: callers may enumerate it while other threads add or remove entries.</summary>
    public IReadOnlyList<SearchHistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                _entries = File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<SearchHistoryEntry>>(File.ReadAllText(_path), ConfigService.JsonOptions) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A damaged history is not worth failing startup over.
                _entries = new();
            }
        }
    }

    public void Add(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var previous = _entries.FirstOrDefault(e => string.Equals(e.Query, query, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                _entries.Remove(previous);
            }

            _entries.Insert(0, new SearchHistoryEntry
            {
                Query = query,
                LastUsedUtc = DateTime.UtcNow,
                Uses = (previous?.Uses ?? 0) + 1,
            });

            if (_entries.Count > _maxEntries)
            {
                _entries.RemoveRange(_maxEntries, _entries.Count - _maxEntries);
            }

            Save();
        }
    }

    public void Remove(string query)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => string.Equals(e.Query, query, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }
    }

    private void Save()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, ConfigService.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a failed write must never break searching.
        }
    }
}
