namespace DeskPanel.Search.Extraction;

public sealed class PlainTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> Extensions { get; } = new[]
    {
        ".txt", ".md", ".markdown", ".rst", ".org", ".tex", ".csv", ".tsv", ".log", ".srt", ".vtt", ".eml",
        ".json", ".xml", ".ini", ".cfg", ".conf", ".toml", ".properties", ".yaml", ".yml",
        ".cs", ".cpp", ".h", ".hpp", ".c", ".java", ".go", ".rs", ".js", ".ts", ".css", ".py", ".vb",
        ".ps1", ".bat", ".cmd", ".sh", ".sql", ".xaml", ".csproj", ".sln",
    };

    public string Extract(Stream stream, string fileName) => TextDecoding.ReadStreamSmart(stream);
}
