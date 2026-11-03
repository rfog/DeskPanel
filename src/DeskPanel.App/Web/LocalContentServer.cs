using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace DeskPanel.App.Web;

/// <summary>
/// Serves the bundled pdf.js viewer and the current document from a fake https origin via WebResourceRequested.
/// pdf.js refuses to open a file from a different origin than the viewer, and file:// breaks its ES modules
/// and worker, so both must come from the same synthetic host.
/// </summary>
internal sealed class LocalContentServer
{
    public const string Origin = "https://deskpanel.local";
    public const string DocumentPath = "/doc/current.pdf";

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".mjs"] = "text/javascript",
        [".js"] = "text/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".ftl"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".wasm"] = "application/wasm",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
    };

    private readonly CoreWebView2Environment _environment;
    private readonly string _pdfJsRoot;
    private byte[]? _document;

    public LocalContentServer(CoreWebView2Environment environment, string pdfJsRoot)
    {
        _environment = environment;
        _pdfJsRoot = Path.GetFullPath(pdfJsRoot) + Path.DirectorySeparatorChar;
    }

    /// <summary>The document is held in memory so the original file stays unlocked for sync clients and for saving.</summary>
    public void SetDocument(byte[] content) => _document = content;

    public void Attach(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    public static string BuildViewerUrl(string? searchText)
    {
        // The cache-busting token forces pdf.js to refetch after an in-place save.
        var file = Uri.EscapeDataString($"{DocumentPath}?v={Guid.NewGuid():N}");
        var url = $"{Origin}/pdfjs/web/viewer.html?file={file}";
        return string.IsNullOrWhiteSpace(searchText) ? url : $"{url}#search={Uri.EscapeDataString(searchText)}";
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        if (path.Equals(DocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            e.Response = _document is null
                ? NotFound()
                : Ok(_document, "application/pdf");
            return;
        }

        const string prefix = "/pdfjs/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.GetFullPath(Path.Combine(_pdfJsRoot, path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));

            // Reject traversal outside the viewer folder; the page is local but still renders untrusted PDF content.
            if (candidate.StartsWith(_pdfJsRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                var mime = MimeTypes.TryGetValue(Path.GetExtension(candidate), out var known) ? known : "application/octet-stream";
                e.Response = Ok(File.ReadAllBytes(candidate), mime);
                return;
            }
        }

        e.Response = NotFound();
    }

    private CoreWebView2WebResourceResponse Ok(byte[] content, string mime) =>
        _environment.CreateWebResourceResponse(new MemoryStream(content, writable: false), 200, "OK",
            $"Content-Type: {mime}\r\nCache-Control: no-store");

    private CoreWebView2WebResourceResponse NotFound() =>
        _environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
}
