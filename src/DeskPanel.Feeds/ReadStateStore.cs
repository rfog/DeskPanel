using System.Text.Json;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.IO;

namespace DeskPanel.Feeds;

/// <summary>
/// Local "read" state (entries already handled here), independent of Inoreader's own read flag.
/// The file lives in the synced config directory, so saves merge with what is on disk to avoid
/// one machine wiping out marks made on another between two syncs.
/// </summary>
public sealed class ReadStateStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset> _marks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unmarkedSinceLoad = new(StringComparer.Ordinal);

    public ReadStateStore(string path) => _path = path;

    public void Load()
    {
        lock (_gate)
        {
            _marks = ReadFromDisk();
            _unmarkedSinceLoad.Clear();
        }
    }

    public bool IsRead(string id)
    {
        lock (_gate)
        {
            return _marks.ContainsKey(id);
        }
    }

    public void MarkRead(string id)
    {
        lock (_gate)
        {
            _marks[id] = DateTimeOffset.UtcNow;
            _unmarkedSinceLoad.Remove(id);
        }
    }

    public void MarkUnread(string id)
    {
        lock (_gate)
        {
            _marks.Remove(id);
            _unmarkedSinceLoad.Add(id);
        }
    }

    public void Save(int retentionDays)
    {
        lock (_gate)
        {
            var onDisk = ReadFromDisk();
            foreach (var (id, when) in onDisk)
            {
                // Explicit unmarks made here must win over the stale copy on disk.
                if (!_unmarkedSinceLoad.Contains(id) && !_marks.ContainsKey(id))
                {
                    _marks[id] = when;
                }
            }

            // Old entries have long left the feed; keeping them only grows a file that syncs on every change.
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(retentionDays, 1));
            foreach (var stale in _marks.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            {
                _marks.Remove(stale);
            }

            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_marks, ConfigService.JsonOptions));
        }
    }

    private Dictionary<string, DateTimeOffset> ReadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }

        var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_path), ConfigService.JsonOptions);
        return loaded is null
            ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(loaded, StringComparer.Ordinal);
    }
}
