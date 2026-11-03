using Ganss.Xss;
using SmartReader;

namespace DeskPanel.Feeds;

/// <summary>
/// Downloads the original page of a feed entry whose RSS only carries a summary, extracts the article body
/// (Readability algorithm, via SmartReader) and sanitizes it before it goes into the PDF or the preview.
/// </summary>
public sealed class FullArticleFetcher
{
    private const long MaxPageBytes = 10 * 1024 * 1024;

    private readonly HttpClient _http;

    public FullArticleFetcher(HttpClient http) => _http = http;

    /// <summary>Returns the sanitized article HTML. Throws <see cref="InvalidOperationException"/> when no article could be found.</summary>
    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("La entrada no tiene una dirección web válida.");
        }

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPageBytes)
        {
            throw new InvalidOperationException("La página es demasiado grande.");
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        var article = new Reader(url, html).GetArticle();
        if (!article.IsReadable || string.IsNullOrWhiteSpace(article.Content))
        {
            throw new InvalidOperationException("No se pudo extraer el artículo de la página (puede requerir inicio de sesión o JavaScript).");
        }

        return Sanitize(article.Content, url);
    }

    /// <summary>
    /// Keeps the markup an article needs (text, lists, tables, images, links) and drops everything active:
    /// scripts, frames, forms, event handlers and non-http(s) URLs.
    /// </summary>
    public static string Sanitize(string html, string baseUrl)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");

        // Images that arrive as data: URIs are dropped with the scheme list above; articles rarely need them.
        sanitizer.AllowedTags.Remove("form");
        sanitizer.AllowedTags.Remove("input");
        sanitizer.AllowedTags.Remove("button");
        return sanitizer.Sanitize(html, baseUrl);
    }
}
