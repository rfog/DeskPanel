using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;
using DeskPanel.Feeds;

namespace DeskPanel.App.Feeds;

/// <summary>How a feed entry becomes the HTML that is previewed and the PDF that is printed. Shared by the desktop window and the web.</summary>
internal static class FeedRendering
{
    /// <summary>The profile's CSS followed by the custom CSS, so the custom rules win.</summary>
    public static async Task<string> LoadCssAsync(Shell shell, PdfProfile profile, string? customCss)
    {
        var cssPath = shell.Config.ResolveConfigRelative(profile.CssFile);
        var css = File.Exists(cssPath) ? await File.ReadAllTextAsync(cssPath) : "";
        if (css.Length == 0)
        {
            AppLog.Warn($"CSS '{cssPath}' not found; rendering unstyled.");
        }

        if (!string.IsNullOrWhiteSpace(customCss))
        {
            var customPath = shell.Config.ResolveConfigRelative(customCss);
            if (File.Exists(customPath))
            {
                css += "\n/* custom: " + Path.GetFileName(customPath) + " */\n" + await File.ReadAllTextAsync(customPath);
            }
            else
            {
                AppLog.Warn($"Custom CSS '{customPath}' not found; ignored.");
            }
        }

        return css;
    }

    public static PdfProfile? FindProfile(FeedsConfig config, string? name) =>
        config.Profiles.FirstOrDefault(p => p.Name == name) ?? config.Profiles.FirstOrDefault();

    /// <summary>
    /// The article exactly as it will be printed (same HTML builder and CSS), plus a few screen-only rules:
    /// a printed page has margins supplied by the PDF engine, a browser pane does not.
    /// </summary>
    public static async Task<string> BuildPreviewHtmlAsync(Shell shell, FeedEntry entry, PdfProfile? profile, string? customCss)
    {
        var css = profile is null ? "" : await LoadCssAsync(shell, profile, customCss);
        css += "\nbody { box-sizing: border-box; max-width: 860px; margin: 0 auto; padding: 20px 28px; }\n";
        return ArticleHtmlBuilder.Build(entry, css);
    }

    /// <summary>Prints the entry to a new PDF in the output folder and returns its path. Must run on the UI thread (WebView2).</summary>
    public static async Task<string> WritePdfAsync(Shell shell, FeedEntry entry, PdfProfile profile, string? customCss)
    {
        var css = await LoadCssAsync(shell, profile, customCss);
        var outputDirectory = shell.Config.Paths.Resolve(shell.Config.App.Feeds.OutputFolder);
        Directory.CreateDirectory(outputDirectory);
        var fileName = FileNames.Sanitize(ArticleHtmlBuilder.SuggestFileName(entry)) + ".pdf";
        var target = FileNames.MakeUnique(outputDirectory, fileName);

        await shell.PdfRenderer.RenderAsync(ArticleHtmlBuilder.Build(entry, css), target, profile, entry.Link);

        var cache = shell.ResolveCacheFolder();
        await Task.Run(() => CacheMirror.Copy(cache, target, overwrite: false, line => AppLog.Info("[cache] " + line.Trim())));
        return target;
    }
}
