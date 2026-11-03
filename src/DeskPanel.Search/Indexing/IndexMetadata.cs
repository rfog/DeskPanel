using System.Text.Json;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.IO;

namespace DeskPanel.Search.Indexing;

/// <summary>
/// Sidecar stored inside each index folder. It records the configured (aliased) base root rather than the
/// resolved path, which is what makes a copied index valid on a machine where that root lives elsewhere.
/// </summary>
public sealed class IndexMetadata
{
    public int SchemaVersion { get; set; }
    public string Name { get; set; } = "";
    public string BaseRoot { get; set; } = "";
    public DateTimeOffset LastUpdatedUtc { get; set; }
    public int DocumentCount { get; set; }
    public string? LastUpdatedOnMachine { get; set; }

    public static IndexMetadata? TryLoad(string indexDirectory)
    {
        var path = System.IO.Path.Combine(indexDirectory, IndexSchema.MetaFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<IndexMetadata>(File.ReadAllText(path), ConfigService.JsonOptions);
    }

    public void Save(string indexDirectory) =>
        AtomicFile.WriteAllText(System.IO.Path.Combine(indexDirectory, IndexSchema.MetaFileName),
            JsonSerializer.Serialize(this, ConfigService.JsonOptions));
}
