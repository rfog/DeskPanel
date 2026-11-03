using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskPanel.Feeds;

public static class ArticleHtmlBuilder
{
    // Scripts are disabled in the renderer, so native lazy-loading would leave below-the-fold images blank in the PDF.
    private static readonly Regex LazyLoading = new(@"\sloading\s*=\s*[""']?lazy[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Build(FeedEntry entry, string css)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"es\"><head><meta charset=\"utf-8\">");
        if (!string.IsNullOrEmpty(entry.Link))
        {
            // Feed content often uses relative image URLs; <base> makes them resolve against the original article.
            html.Append("<base href=\"").Append(WebUtility.HtmlEncode(entry.Link)).AppendLine("\">");
        }

        html.Append("<title>").Append(WebUtility.HtmlEncode(entry.Title)).AppendLine("</title>");
        html.Append("<style>").Append(css).AppendLine("</style>");
        html.AppendLine("</head><body><article>");
        html.AppendLine("<header class=\"dp-header\">");
        html.Append("<h1 class=\"dp-title\">").Append(WebUtility.HtmlEncode(entry.Title)).AppendLine("</h1>");
        html.Append("<p class=\"dp-meta\">").Append(WebUtility.HtmlEncode(BuildMeta(entry))).AppendLine("</p>");
        if (!string.IsNullOrEmpty(entry.Link))
        {
            var link = WebUtility.HtmlEncode(entry.Link);
            html.Append("<p class=\"dp-link\"><a href=\"").Append(link).Append("\">").Append(link).AppendLine("</a></p>");
        }

        html.AppendLine("</header>");
        html.AppendLine("<div class=\"dp-content\">");
        html.AppendLine(LazyLoading.Replace(entry.ContentHtml, ""));
        html.AppendLine("</div></article></body></html>");
        return html.ToString();
    }

    public static string SuggestFileName(FeedEntry entry)
    {
        var date = (entry.Published ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"{date} - {entry.Title}";
    }

    private static string BuildMeta(FeedEntry entry)
    {
        var parts = new List<string> { entry.SourceName };
        if (!string.IsNullOrWhiteSpace(entry.Author))
        {
            parts.Add(entry.Author);
        }

        if (entry.Published is { } published)
        {
            parts.Add(published.ToLocalTime().ToString("d MMMM yyyy, HH:mm", new CultureInfo("es-ES")));
        }

        return string.Join(" | ", parts);
    }
}
