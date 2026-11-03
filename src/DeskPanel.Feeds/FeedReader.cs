using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DeskPanel.Feeds;

/// <summary>
/// Hand-rolled RSS 2.0 / Atom parser. System.ServiceModel.Syndication rejects many real-world feeds
/// (non-RFC822 dates, missing elements), and Inoreader output only needs a small subset anyway.
/// </summary>
public sealed class FeedReader
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace ContentNs = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";

    private static readonly string[] Rfc822Formats =
    {
        "ddd, d MMM yyyy HH:mm:ss zzz",
        "ddd, d MMM yyyy HH:mm zzz",
        "d MMM yyyy HH:mm:ss zzz",
        "d MMM yyyy HH:mm zzz",
    };

    private static readonly Regex NumericOffset = new(@"([+-]\d{2})(\d{2})$", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public FeedReader(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<FeedEntry>> ReadAsync(string url, string sourceName, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Parse(stream, sourceName);
    }

    public static IReadOnlyList<FeedEntry> Parse(Stream stream, string sourceName)
    {
        // DTD processing stays off: feeds are untrusted input and entity expansion is a known DoS vector.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new InvalidDataException("Empty feed document.");

        return root.Name == Atom + "feed"
            ? ParseAtom(root, sourceName)
            : ParseRss(root, sourceName);
    }

    private static List<FeedEntry> ParseRss(XElement root, string sourceName)
    {
        var entries = new List<FeedEntry>();
        foreach (var item in root.Descendants("item"))
        {
            var title = Clean(item.Element("title")?.Value) ?? "(sin título)";
            var link = Clean(item.Element("link")?.Value);
            var published = ParseDate(item.Element("pubDate")?.Value ?? item.Element(DublinCore + "date")?.Value);
            var author = Clean(item.Element(DublinCore + "creator")?.Value ?? item.Element("author")?.Value);

            // content:encoded carries the full article; description is often a truncated teaser.
            var content = item.Element(ContentNs + "encoded")?.Value ?? item.Element("description")?.Value ?? "";
            var id = Clean(item.Element("guid")?.Value) ?? link ?? SyntheticId(title, published);

            entries.Add(new FeedEntry(id, title, link, published, author, content, sourceName));
        }

        return entries;
    }

    private static List<FeedEntry> ParseAtom(XElement root, string sourceName)
    {
        var entries = new List<FeedEntry>();
        foreach (var entry in root.Elements(Atom + "entry"))
        {
            var title = Clean(entry.Element(Atom + "title")?.Value) ?? "(sin título)";
            var link = entry.Elements(Atom + "link")
                .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")
                ?.Attribute("href")?.Value;
            var published = ParseDate(entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value);
            var author = Clean(entry.Element(Atom + "author")?.Element(Atom + "name")?.Value);
            var content = entry.Element(Atom + "content")?.Value ?? entry.Element(Atom + "summary")?.Value ?? "";
            var id = Clean(entry.Element(Atom + "id")?.Value) ?? link ?? SyntheticId(title, published);

            entries.Add(new FeedEntry(id, title, link, published, author, content, sourceName));
        }

        return entries;
    }

    internal static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso)
            && !value.Contains(',', StringComparison.Ordinal))
        {
            return iso;
        }

        // RFC 822 zones come as "+0000", "GMT" or "UT"; the zzz specifier only understands "+00:00".
        value = value.Replace(" GMT", " +00:00", StringComparison.OrdinalIgnoreCase)
                     .Replace(" UTC", " +00:00", StringComparison.OrdinalIgnoreCase)
                     .Replace(" UT", " +00:00", StringComparison.OrdinalIgnoreCase)
                     .Replace(" Z", " +00:00", StringComparison.Ordinal);
        value = NumericOffset.Replace(value, "$1:$2");

        if (DateTimeOffset.TryParseExact(value, Rfc822Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var rfc))
        {
            return rfc;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fallback)
            ? fallback
            : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Items without guid or link still need a stable key, otherwise they reappear as unread on every refresh.
    private static string SyntheticId(string title, DateTimeOffset? published)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{title}|{published:O}"));
        return "sha256:" + Convert.ToHexString(bytes, 0, 16);
    }
}
