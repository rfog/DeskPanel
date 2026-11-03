using System.Net;
using System.Text.RegularExpressions;

namespace DeskPanel.Search.Extraction;

public sealed partial class HtmlTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".html", ".htm", ".xhtml" };

    public string Extract(Stream stream, string fileName) => StripTags(TextDecoding.ReadStreamSmart(stream));

    public static string StripTags(string html)
    {
        // Script and style bodies are code, not content; left in they would pollute every search with identifiers.
        var text = NonContent().Replace(html, " ");
        text = BlockBreak().Replace(text, "\n");
        text = Tag().Replace(text, " ");
        return WebUtility.HtmlDecode(text);
    }

    [GeneratedRegex(@"<(script|style)\b.*?</\1\s*>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NonContent();

    [GeneratedRegex(@"</?(p|div|br|li|tr|h[1-6]|section|article|table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();
}
