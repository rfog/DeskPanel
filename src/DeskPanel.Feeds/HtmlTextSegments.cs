using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace DeskPanel.Feeds;

/// <summary>
/// The visible text of an HTML fragment as a list of segments that can be translated one by one and put back. Only text
/// nodes are touched, so every tag, attribute, image and link of the original survives exactly as it was.
/// </summary>
public sealed partial class HtmlTextSegments
{
    // Text that is not prose: code, scripts, styles. Translating it would break it.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "pre", "code", "kbd", "samp", "textarea", "svg", "math",
    };

    private readonly IHtmlDocument _document;
    private readonly List<(IText Node, string Lead, string Trail)> _nodes = new();

    private HtmlTextSegments(IHtmlDocument document, List<string> segments)
    {
        _document = document;
        Segments = segments;
    }

    /// <summary>The translatable texts, trimmed and with runs of whitespace collapsed, in document order.</summary>
    public IReadOnlyList<string> Segments { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static HtmlTextSegments Parse(string htmlFragment)
    {
        var document = new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + htmlFragment + "</body></html>");
        var segments = new List<string>();
        var result = new HtmlTextSegments(document, segments);
        Walk(document.Body!, result, segments);
        return result;
    }

    private static void Walk(INode parent, HtmlTextSegments result, List<string> segments)
    {
        foreach (var child in parent.ChildNodes.ToList())
        {
            if (child is IText text)
            {
                var data = text.Data;
                var trimmed = data.Trim();

                // Whitespace between tags, and things like "—" or "2026", have nothing to translate.
                if (trimmed.Length == 0 || !trimmed.Any(char.IsLetter))
                {
                    continue;
                }

                var lead = data[..(data.Length - data.TrimStart().Length)];
                var trail = data[data.TrimEnd().Length..];
                result._nodes.Add((text, lead, trail));
                segments.Add(Whitespace().Replace(trimmed, " "));
            }
            else if (child is IElement element && !Skipped.Contains(element.LocalName))
            {
                Walk(element, result, segments);
            }
        }
    }

    /// <summary>Puts the translation of segment <paramref name="index"/> in place of the original text.</summary>
    public void Replace(int index, string translated)
    {
        var (node, lead, trail) = _nodes[index];
        node.Data = lead + translated.Trim() + trail;
    }

    public string ToHtml() => _document.Body!.InnerHtml;
}
