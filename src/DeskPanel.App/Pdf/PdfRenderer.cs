using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeskPanel.App.Web;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskPanel.App.Pdf;

/// <summary>
/// HTML → PDF through an off-screen WebView2. The control needs a real HWND to initialise, so it lives in a
/// window parked outside the virtual desktop; renders are serialised because one WebView navigates at a time.
/// </summary>
internal sealed class PdfRenderer : DeskPanel.Core.Platform.IPdfRenderer, IDisposable
{
    private const double MillimetresPerInch = 25.4;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(60);

    private readonly WebViewEnvironmentProvider _environment;
    private readonly string _tempDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Window? _host;
    private WebView2? _view;

    public PdfRenderer(WebViewEnvironmentProvider environment, string tempDirectory)
    {
        _environment = environment;
        _tempDirectory = tempDirectory;
    }

    /// <param name="headerText">Shown in the page header (the article URL); the footer carries creation date and "n de total".</param>
    public async Task RenderAsync(string html, string outputPath, PdfProfile profile, string? headerText = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        Directory.CreateDirectory(_tempDirectory);
        var htmlPath = Path.Combine(_tempDirectory, $"render-{Guid.NewGuid():N}.html");
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var stagingPath = Path.Combine(outputDirectory, $"~{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var core = await EnsureViewAsync();

            // A temp file instead of NavigateToString: that API caps content at 2 MB, which inline images exceed.
            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), ct);
            await NavigateAsync(core, new Uri(htmlPath).AbsoluteUri, ct);

            if (profile.ShowHeaderFooter && await TryPrintWithHeaderFooterAsync(core, stagingPath, profile, headerText))
            {
                File.Move(stagingPath, outputPath, overwrite: false);
                return;
            }

            var environment = await _environment.GetAsync();
            var settings = environment.CreatePrintSettings();
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.PageWidth = profile.PageWidthMm / MillimetresPerInch;
            settings.PageHeight = profile.PageHeightMm / MillimetresPerInch;
            settings.MarginTop = profile.MarginTopMm / MillimetresPerInch;
            settings.MarginBottom = profile.MarginBottomMm / MillimetresPerInch;
            settings.MarginLeft = profile.MarginLeftMm / MillimetresPerInch;
            settings.MarginRight = profile.MarginRightMm / MillimetresPerInch;
            settings.ScaleFactor = profile.Scale;
            settings.ShouldPrintBackgrounds = profile.PrintBackgrounds;
            settings.ShouldPrintHeaderAndFooter = false;

            // Print to a staging name first; the output folder is synced and must never expose a partial PDF.
            if (!await core.PrintToPdfAsync(stagingPath, settings))
            {
                throw new InvalidOperationException("WebView2 no pudo generar el PDF.");
            }

            File.Move(stagingPath, outputPath, overwrite: false);
        }
        finally
        {
            TryDelete(htmlPath);
            TryDelete(stagingPath);
            _gate.Release();
        }
    }

    /// <summary>
    /// CoreWebView2.PrintToPdfAsync can only print Chromium's fixed header/footer (date, title, URI, "1/8"), so the
    /// DevTools Page.printToPDF method is used instead: it accepts HTML templates and the pageNumber/totalPages
    /// placeholders. Returns false (after cleaning up) if that path fails, so the caller can still produce a PDF.
    /// </summary>
    private static async Task<bool> TryPrintWithHeaderFooterAsync(CoreWebView2 core, string stagingPath, PdfProfile profile, string? headerText)
    {
        try
        {
            var parameters = new Dictionary<string, object>
            {
                ["displayHeaderFooter"] = true,
                ["headerTemplate"] = BuildHeaderTemplate(profile, headerText),
                ["footerTemplate"] = BuildFooterTemplate(profile, DateTimeOffset.Now),
                ["printBackground"] = profile.PrintBackgrounds,
                ["scale"] = profile.Scale,
                ["paperWidth"] = profile.PageWidthMm / MillimetresPerInch,
                ["paperHeight"] = profile.PageHeightMm / MillimetresPerInch,
                ["marginTop"] = profile.MarginTopMm / MillimetresPerInch,
                ["marginBottom"] = profile.MarginBottomMm / MillimetresPerInch,
                ["marginLeft"] = profile.MarginLeftMm / MillimetresPerInch,
                ["marginRight"] = profile.MarginRightMm / MillimetresPerInch,

                // Streaming avoids one huge base64 string in a single DevTools message for image-heavy articles.
                ["transferMode"] = "ReturnAsStream",
            };

            using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.printToPDF", JsonSerializer.Serialize(parameters)));
            var handle = result.RootElement.GetProperty("stream").GetString()!;
            try
            {
                await using var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                while (true)
                {
                    var request = JsonSerializer.Serialize(new { handle, size = 1 << 20 });
                    using var chunk = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("IO.read", request));
                    var root = chunk.RootElement;
                    var data = root.GetProperty("data").GetString() ?? "";
                    var bytes = root.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean()
                        ? Convert.FromBase64String(data)
                        : Encoding.Latin1.GetBytes(data);
                    await output.WriteAsync(bytes);
                    if (root.GetProperty("eof").GetBoolean())
                    {
                        break;
                    }
                }
            }
            finally
            {
                await core.CallDevToolsProtocolMethodAsync("IO.close", JsonSerializer.Serialize(new { handle }));
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("Header/footer print failed; falling back to a plain PDF.", ex);
            TryDelete(stagingPath);
            return false;
        }
    }

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
        "width:100%;box-sizing:border-box;font-family:'Segoe UI',Arial,sans-serif;font-size:8px;color:#5e6b7a;" +
        "white-space:nowrap;overflow:hidden;text-overflow:ellipsis;-webkit-print-color-adjust:exact;" +
        string.Create(CultureInfo.InvariantCulture, $"padding:0 {profile.MarginRightMm}mm 0 {profile.MarginLeftMm}mm;");

    public void Dispose()
    {
        _view?.Dispose();
        _host?.Close();
        _gate.Dispose();
    }

    private async Task<CoreWebView2> EnsureViewAsync()
    {
        if (_view?.CoreWebView2 is { } existing)
        {
            return existing;
        }

        _view = new WebView2();
        _host = new Window
        {
            Title = "DeskPanel renderer",
            Width = 1024,
            Height = 768,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Content = _view,
        };
        _host.Show();

        await _view.EnsureCoreWebView2Async(await _environment.GetAsync());
        var settings = _view.CoreWebView2.Settings;

        // Feed HTML is third-party content; nothing in a print render needs script.
        settings.IsScriptEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        return _view.CoreWebView2;
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(new InvalidOperationException($"Error al cargar el HTML: {e.WebErrorStatus}"));
            }
        }

        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);

            // NavigationCompleted fires after the load event, so images referenced by the article have been fetched.
            await completion.Task.WaitAsync(NavigationTimeout, ct);
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }
    }

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
            // Left for the startup temp sweep.
        }
    }
}
