using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;
using DeskPanel.Feeds;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Feeds;

/// <summary>How a feed entry becomes a PDF in the output folder (and in the cache folder, when one is configured).</summary>
public static class FeedPdfService
{
    /// <summary>Suffix that tells a translated PDF from the original-language one.</summary>
    public const string TranslatedSuffix = "_ES";

    public sealed record Result(string OutputPath, string? CachePath);

    /// <summary>The profile's CSS followed by the custom CSS, so the custom rules win.</summary>
    public static async Task<string> LoadCssAsync(MacServices services, PdfProfile profile, string? customCss)
    {
        var cssPath = services.ResolveCss(profile.CssFile);
        var css = File.Exists(cssPath) ? await File.ReadAllTextAsync(cssPath) : "";
        if (css.Length == 0)
        {
            AppLog.Warn($"CSS '{cssPath}' not found; rendering unstyled.");
        }

        if (!string.IsNullOrWhiteSpace(customCss))
        {
            var customPath = services.ResolveCss(customCss);
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

    public static string BuildFileName(FeedEntry entry, bool translated) =>
        FileNames.Sanitize(ArticleHtmlBuilder.SuggestFileName(entry)) + (translated ? TranslatedSuffix : "") + ".pdf";

    /// <summary>
    /// Renders the entry to a temporary file and renames it into each destination. Both destinations are watched
    /// (by the sync client and by DEVONthink's import rule), so nothing is ever written in them except a finished file.
    /// </summary>
    public static async Task<Result> WritePdfAsync(
        MacServices services, FeedEntry entry, bool translated, PdfProfile profile, string? customCss, CancellationToken ct = default)
    {
        var css = await LoadCssAsync(services, profile, customCss);
        var outputDirectory = services.Config.Paths.Resolve(services.Config.App.Feeds.OutputFolder);
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(MacServices.TempDirectory);

        var fileName = BuildFileName(entry, translated);
        var staging = Path.Combine(MacServices.TempDirectory, $"{Guid.NewGuid():N}.pdf");
        string? cacheStaging = null;
        try
        {
            await services.PdfRenderer.RenderAsync(ArticleHtmlBuilder.Build(entry, css), staging, profile, entry.Link, ct);

            var cacheTarget = PrepareCacheCopy(services, outputDirectory, fileName, staging, out cacheStaging);

            var target = FileNames.MakeUnique(outputDirectory, fileName);
            File.Move(staging, target, overwrite: false);

            string? cachePath = null;
            if (cacheStaging is not null && cacheTarget is not null)
            {
                // The real PDF is already in place: a cache failure must not turn that into an error.
                try
                {
                    File.Move(cacheStaging, cacheTarget, overwrite: false);
                    cachePath = cacheTarget;
                    cacheStaging = null;
                }
                catch (IOException ex)
                {
                    AppLog.Warn($"Could not place the cache copy '{cacheTarget}'.", ex);
                }
            }

            return new Result(target, cachePath);
        }
        finally
        {
            TryDelete(staging);
            TryDelete(cacheStaging);
        }
    }

    /// <summary>Copies the staged PDF to a second temp file when a cache folder applies; returns where it will land.</summary>
    private static string? PrepareCacheCopy(MacServices services, string outputDirectory, string fileName, string staging, out string? cacheStaging)
    {
        cacheStaging = null;
        var cacheFolder = services.ResolveCacheFolder();
        if (string.IsNullOrWhiteSpace(cacheFolder))
        {
            return null;
        }

        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheFolder));
            if (string.Equals(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)), StringComparison.Ordinal))
            {
                return null; // the cache is the output folder itself
            }

            Directory.CreateDirectory(folder);
            var target = FileNames.MakeUnique(folder, fileName);
            cacheStaging = Path.Combine(MacServices.TempDirectory, $"{Guid.NewGuid():N}.cache.pdf");
            File.Copy(staging, cacheStaging);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("Could not prepare the cache copy.", ex);
            return null;
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Left for the startup temp sweep.
        }
    }
}
