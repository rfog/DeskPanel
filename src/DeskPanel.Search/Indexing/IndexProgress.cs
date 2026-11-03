namespace DeskPanel.Search.Indexing;

public sealed record IndexProgress(string Phase, int Processed, int Total, string? CurrentFile);

public sealed class IndexUpdateResult
{
    // Public fields rather than properties: the indexer updates them with Interlocked from parallel workers.
    public int Added;
    public int Updated;
    public int MetadataOnly;
    public int Unchanged;
    public int Removed;
    public List<string> Failures { get; } = new();
    public TimeSpan Elapsed { get; set; }

    public override string ToString() =>
        $"Nuevos {Added}, actualizados {Updated}, solo fecha {MetadataOnly}, sin cambios {Unchanged}, " +
        $"eliminados {Removed}, errores {Failures.Count} ({Elapsed:mm\\:ss})";
}
