namespace DeskPanel.Search.Searching;

/// <summary>
/// Snippet text uses U+0001 / U+0002 as highlight start/end markers so the UI can render emphasis without parsing HTML.
/// </summary>
public sealed record SearchHit(
    string IndexName,
    string RelativePath,
    string FullPath,
    string Extension,
    float Score,
    string Snippet)
{
    public const char HighlightStart = '\u0001';
    public const char HighlightEnd = '\u0002';

    public string FileName => Path.GetFileName(RelativePath);
}

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalHits, IReadOnlyList<string> HighlightTerms);

public sealed record SearchTarget(string IndexName, string IndexDirectory, string ResolvedBaseRoot);
