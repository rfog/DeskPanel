using System.Security.Cryptography;
using System.Text.Json;
using DeskPanel.Core.Configuration;
using DeskPanel.Feeds;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Feeds;

/// <summary>
/// The HTML the preview pane shows: the article exactly as the PDF is built (same <see cref="ArticleHtmlBuilder"/> and CSS),
/// made safe for on-screen use. Feed content is third-party, so no script of the article may run.
/// </summary>
public static class FeedPreview
{
    /// <summary>The message the page posts when the user right-clicks it.</summary>
    public const string ContextMenuMessage = "dp-contextmenu";

    // A printed page gets its margins from the PDF engine; a browser pane does not.
    private const string ScreenCss = "\nbody { box-sizing: border-box; max-width: 860px; margin: 0 auto; padding: 20px 28px; }\n";

    public static async Task<string> BuildAsync(MacServices services, FeedEntry entry, PdfProfile? profile, string? customCss)
    {
        var css = profile is null ? "" : await FeedPdfService.LoadCssAsync(services, profile, customCss);
        return Harden(ArticleHtmlBuilder.Build(entry, css + ScreenCss));
    }

    /// <summary>
    /// Adds a Content-Security-Policy that blocks every script except ours (identified by a one-time nonce), and the
    /// script that reports right-clicks to the host: the native web view would otherwise show its own menu.
    /// </summary>
    public static string Harden(string html)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var policy = $"<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'nonce-{nonce}'; object-src 'none'\">";
        var script = $"<script nonce=\"{nonce}\">" + BridgeScript + "</script>";

        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            html = html.Insert(head + "<head>".Length, policy);
        }

        var body = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return body >= 0 ? html.Insert(body, script) : html + script;
    }

    private static string BridgeScript =>
        """
        document.addEventListener('contextmenu', function (e) {
          e.preventDefault();
          var m = JSON.stringify({ type: 'dp-contextmenu', x: e.clientX, y: e.clientY });
          try {
            if (window.invokeCSharpAction) { window.invokeCSharpAction(m); }
            else if (window.webkit && window.webkit.messageHandlers) {
              for (var k in window.webkit.messageHandlers) { window.webkit.messageHandlers[k].postMessage(m); }
            } else if (window.chrome && window.chrome.webview) { window.chrome.webview.postMessage(m); }
          } catch (err) { }
        }, true);
        """;

    /// <summary>The position of a right-click message in page coordinates, or null for any other message.</summary>
    public static (double X, double Y)? ParseContextMenu(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        try
        {
            // Some hosts deliver the message as a JSON string that itself contains JSON.
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return ParseContextMenu(root.GetString());
            }

            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("type", out var type) && type.GetString() == ContextMenuMessage
                ? (root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
