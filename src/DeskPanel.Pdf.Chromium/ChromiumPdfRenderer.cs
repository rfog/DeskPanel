using System.Globalization;
using System.Net;
using System.Text;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Platform;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace DeskPanel.Pdf.Chromium;

/// <summary>
/// HTML → PDF through the Chrome that is already installed (no browser is downloaded). One browser process is started on
/// the first render and reused until <see cref="DisposeAsync"/>; renders are serialised so pages never compete.
/// </summary>
public sealed class ChromiumPdfRenderer : IPdfRenderer, IAsyncDisposable
{
    public const string DefaultMacChromePath = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string?> _chromePath;
    private readonly string _tempDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowser? _browser;

    /// <param name="chromePath">Read on every browser start, so a path changed in Settings applies without restarting.</param>
    /// <param name="tempDirectory">Where the intermediate HTML lives; any folder that is not synced.</param>
    public ChromiumPdfRenderer(Func<string?> chromePath, string tempDirectory)
    {
        _chromePath = chromePath;
        _tempDirectory = tempDirectory;
    }

    public async Task RenderAsync(string html, string outputPath, PdfProfile profile, string? headerText = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        Directory.CreateDirectory(_tempDirectory);
        var htmlPath = Path.Combine(_tempDirectory, $"render-{Guid.NewGuid():N}.html");
        try
        {
            // A file instead of SetContentAsync: inline images make the HTML large, and a file URL lets the <base> tag work.
            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), ct);

            var browser = await EnsureBrowserAsync();
            await using var page = await browser.NewPageAsync();

            // Feed HTML is third-party content; nothing in a print render needs script.
            await page.SetJavaScriptEnabledAsync(false);
            await page.GoToAsync(new Uri(htmlPath).AbsoluteUri, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Load },
                Timeout = (int)NavigationTimeout.TotalMilliseconds,
            });

            await page.PdfAsync(outputPath, BuildOptions(profile, headerText));
        }
        finally
        {
            TryDelete(htmlPath);
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is { } browser)
        {
            _browser = null;
            // ConfigureAwait(false) matters: the app disposes this while the UI thread is blocked waiting for it, so no
            // continuation here may need to come back to that thread. The timeout covers a Chrome that does not answer.
            try
            {
                await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PuppeteerException or TimeoutException)
            {
                try
                {
                    browser.Process?.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }

            await browser.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private static PdfOptions BuildOptions(PdfProfile profile, string? headerText)
    {
        var options = new PdfOptions
        {
            Width = Mm(profile.PageWidthMm),
            Height = Mm(profile.PageHeightMm),
            Scale = (decimal)profile.Scale,
            PrintBackground = profile.PrintBackgrounds,

            // @page rules win when a stylesheet sets them; otherwise Width/Height above apply.
            PreferCSSPageSize = true,
            MarginOptions = new MarginOptions
            {
                Top = Mm(profile.MarginTopMm),
                Bottom = Mm(profile.MarginBottomMm),
                Left = Mm(profile.MarginLeftMm),
                Right = Mm(profile.MarginRightMm),
            },
        };

        if (profile.ShowHeaderFooter)
        {
            options.DisplayHeaderFooter = true;
            options.HeaderTemplate = BuildHeaderTemplate(profile, headerText);
            options.FooterTemplate = BuildFooterTemplate(profile, DateTimeOffset.Now);
        }

        return options;
    }

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is { IsClosed: false } existing)
        {
            return existing;
        }

        var path = string.IsNullOrWhiteSpace(_chromePath()) ? DefaultMacChromePath : _chromePath()!;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No se encuentra Chrome en '{path}'. Indica la ruta en Ajustes › General.");
        }

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = path,
            Headless = true,
            Args = new[] { "--disable-gpu", "--no-first-run", "--no-default-browser-check" },
        });
        return _browser;
    }

    private static string Mm(double value) => string.Create(CultureInfo.InvariantCulture, $"{value}mm");

    private static string BuildHeaderTemplate(PdfProfile profile, string? headerText) =>
        $"<div style=\"{TemplateStyle(profile)}\">{WebUtility.HtmlEncode(headerText ?? "")}</div>";

    private static string BuildFooterTemplate(PdfProfile profile, DateTimeOffset created)
    {
        var date = created.ToString("d MMMM yyyy, HH:mm", new CultureInfo("es-ES"));
        return $"<div style=\"{TemplateStyle(profile)}display:flex;justify-content:space-between;\">" +
               $"<span>Creado el {WebUtility.HtmlEncode(date)}</span>" +
               "<span><span class=\"pageNumber\"></span> de <span class=\"totalPages\"></span></span></div>";
    }

    // Header/footer templates render outside the page body: no inherited CSS, no margins, and an unset font size
    // would make the text invisible. Side padding re-creates the page margins so the text lines up with the content.
    private static string TemplateStyle(PdfProfile profile) =>
        "width:100%;box-sizing:border-box;font-family:'Segoe UI',Helvetica,Arial,sans-serif;font-size:8px;color:#5e6b7a;" +
        "white-space:nowrap;overflow:hidden;text-overflow:ellipsis;-webkit-print-color-adjust:exact;" +
        string.Create(CultureInfo.InvariantCulture, $"padding:0 {profile.MarginRightMm}mm 0 {profile.MarginLeftMm}mm;");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Left for the next temp sweep.
        }
    }
}
