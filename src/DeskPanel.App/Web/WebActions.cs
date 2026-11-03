using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DeskPanel.App.Audio;
using DeskPanel.App.Feeds;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Feeds;
using DeskPanel.Remote;

namespace DeskPanel.App.Web;

/// <summary>
/// What the web interface may do on this PC: process the audios waiting in the input folder and turn feed entries into
/// PDFs (with full article and summary). It drives the same code as the desktop windows; the web only starts things and
/// reads how they went. Nothing is uploaded or downloaded: every file stays in its own (synced) folder.
/// </summary>
internal sealed class WebActions : IWebActions
{
    private const int MaxEntries = 300;

    private readonly Shell _shell;
    private readonly object _gate = new();
    private readonly Dictionary<string, FeedEntry> _entries = new();
    private readonly Dictionary<string, string> _fullContent = new();
    private readonly Dictionary<string, (FeedEntry Entry, string Markdown)> _summaries = new();
    private List<string> _order = new();
    private List<string> _lastErrors = new();

    public WebActions(Shell shell) => _shell = shell;

    // WPF objects (the audio view model, WebView2) belong to the UI thread; requests arrive on others.
    private static Dispatcher Ui => System.Windows.Application.Current.Dispatcher;

    // Read at the moment of use, so a reload (Settings, tray) is picked up without restarting the server.
    private DeskPanel.Core.Configuration.FeedsConfig FeedsCfg => _shell.Config.App.Feeds;

    // ---------------------------------------------------------------- audio

    public async Task<WebAudioState> GetAudioAsync() => await Ui.InvokeAsync(() =>
    {
        var vm = _shell.AudioVm;

        // Rescan the folder when idle so the list shows what has arrived since; a running job keeps its own list.
        if (!vm.IsBusy)
        {
            vm.RefreshCommand.Execute(null);
        }

        var files = vm.Files.Select(f => new WebAudioFile(f.Name, f.Size, f.ModifiedUtc)).ToList();
        var results = vm.Results.Select(r => new WebAudioResult(r.Name, r.Ok, r.Message)).ToList();
        var log = vm.SnapshotLog();

        // After a run the view model's own status is overwritten by the folder rescan ("No hay audios…"); the outcome is
        // what the phone needs to see.
        var status = vm.Status;
        if (!vm.IsBusy && results.Count > 0)
        {
            var failed = results.Count(r => !r.Ok);
            status = failed == 0
                ? $"Terminado: {results.Count} audio(s) procesados sin errores."
                : $"Terminado: {results.Count - failed} correcto(s), {failed} con error (se conservan en la carpeta de entrada).";
        }

        return new WebAudioState(
            files, vm.IsBusy, vm.StepText, Math.Round(vm.ProgressValue, 1), log.Skip(Math.Max(0, log.Length - 40)).ToList(),
            results, status, string.IsNullOrEmpty(vm.ToolsWarning) ? null : vm.ToolsWarning);
    });

    public async Task<string?> StartAudioAsync(IReadOnlyList<string> names) => await Ui.InvokeAsync(() =>
    {
        var vm = _shell.AudioVm;
        if (vm.IsBusy)
        {
            return "Ya hay un proceso de audio en marcha.";
        }

        vm.RefreshCommand.Execute(null);

        // The names come from the client: only files that really are in the input folder can be chosen.
        var selected = vm.Files.Where(f => names.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            return "Esos audios ya no están en la carpeta de entrada.";
        }

        foreach (var file in vm.Files)
        {
            file.IsSelected = selected.Contains(file);
        }

        var problem = vm.Begin(selected, out var run);
        if (problem is null)
        {
            // The run reports through the view model; a crash here must still end up in the log.
            _ = run.ContinueWith(t => AppLog.Error("Audio run (web) failed.", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }

        return problem;
    });

    public async Task CancelAudioAsync() => await Ui.InvokeAsync(() => _shell.AudioVm.CancelIfRunning());

    // ---------------------------------------------------------------- feeds

    public async Task<WebFeedList> GetFeedsAsync(bool refresh, bool includeRead, CancellationToken ct)
    {
        bool needLoad;
        lock (_gate)
        {
            needLoad = refresh || _order.Count == 0;
        }

        if (needLoad)
        {
            await LoadEntriesAsync(ct);
        }

        var config = FeedsCfg;
        lock (_gate)
        {
            var list = new List<WebFeedEntry>();
            foreach (var id in _order)
            {
                var entry = _entries[id];
                var isRead = _shell.ReadState.IsRead(entry.Id);
                if (isRead && !includeRead)
                {
                    continue;
                }

                list.Add(new WebFeedEntry(id, entry.Title, entry.SourceName, entry.Published?.UtcDateTime, isRead,
                    _fullContent.ContainsKey(id), !string.IsNullOrWhiteSpace(entry.Link)));
                if (list.Count >= MaxEntries)
                {
                    break;
                }
            }

            return new WebFeedList(list, config.Profiles.Select(p => p.Name).ToList(), config.DefaultProfile, _lastErrors.ToList());
        }
    }

    private async Task LoadEntriesAsync(CancellationToken ct)
    {
        // Another machine may have generated PDFs since the last read.
        _shell.ReadState.Load();

        var errors = new ConcurrentBag<string>();
        var reader = new FeedReader(_shell.Http);
        var results = await Task.WhenAll(FeedsCfg.Sources.ToArray().Select(async source =>
        {
            try
            {
                return await reader.ReadAsync(source.Url, source.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn($"Feed '{source.Name}' failed (web).", ex);
                errors.Add($"{source.Name}: {ex.Message}");
                return (IReadOnlyList<FeedEntry>)Array.Empty<FeedEntry>();
            }
        }));
        ct.ThrowIfCancellationRequested();

        var ordered = results.SelectMany(r => r).GroupBy(e => e.Id).Select(g => g.First())
            .OrderByDescending(e => e.Published ?? DateTimeOffset.MinValue).ToList();

        lock (_gate)
        {
            _entries.Clear();
            _order = new List<string>();
            foreach (var entry in ordered)
            {
                var id = ShortId(entry.Id);
                if (_entries.TryAdd(id, entry))
                {
                    _order.Add(id);
                }
            }

            // A refresh brings new content for the entries that remain, so what was downloaded or summarised for entries
            // that left the feed is dropped.
            foreach (var stale in _fullContent.Keys.Where(k => !_entries.ContainsKey(k)).ToList()) _fullContent.Remove(stale);
            foreach (var stale in _summaries.Keys.Where(k => !_entries.ContainsKey(k)).ToList()) _summaries.Remove(stale);
            _lastErrors = errors.ToList();
        }
    }

    // A short opaque id: the feed's own id may be a long URL, and the client never needs to see it.
    private static string ShortId(string feedId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(feedId)))[..16].ToLowerInvariant();

    private bool TryGet(string id, out FeedEntry effective)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                effective = _fullContent.TryGetValue(id, out var full) ? entry with { ContentHtml = full } : entry;
                return true;
            }
        }

        effective = null!;
        return false;
    }

    public async Task<string?> GetFeedPreviewAsync(string id, string? profile, CancellationToken ct)
    {
        if (!TryGet(id, out var entry))
        {
            return null;
        }

        var html = await FeedRendering.BuildPreviewHtmlAsync(_shell, entry, FeedRendering.FindProfile(FeedsCfg, profile), FeedsCfg.CustomCssFile);

        // Links inside the article open in a new tab instead of replacing the preview frame. On a phone the print layout
        // (large justified type for a sheet of paper) is far too big, so a narrow frame gets reading-friendly rules.
        const string phone = "<style>@media (max-width: 700px) { body { max-width: none !important; padding: 12px 14px !important; " +
                             "font-size: 16px !important; line-height: 1.55 !important; text-align: left !important; } " +
                             ".dp-title { font-size: 1.45rem !important; line-height: 1.25 !important; } " +
                             "img, video, table { max-width: 100% !important; height: auto !important; } " +
                             "pre { white-space: pre-wrap !important; } }</style></head>";
        return html.Replace("<base href=\"", "<base target=\"_blank\" href=\"", StringComparison.Ordinal)
            .Replace("</head>", phone, StringComparison.Ordinal);
    }

    public async Task<WebActionResult> GeneratePdfAsync(string id, string? profile, CancellationToken ct)
    {
        if (!TryGet(id, out var entry))
        {
            return new WebActionResult(false, "La entrada ya no está disponible. Actualiza la lista.");
        }

        var pdfProfile = FeedRendering.FindProfile(FeedsCfg, profile ?? FeedsCfg.DefaultProfile);
        if (pdfProfile is null)
        {
            return new WebActionResult(false, "No hay ningún perfil PDF configurado.");
        }

        try
        {
            // WebView2 renders on the UI thread; the renderer queues concurrent jobs itself.
            var target = await await Ui.InvokeAsync(() => FeedRendering.WritePdfAsync(_shell, entry, pdfProfile, FeedsCfg.CustomCssFile));

            _shell.ReadState.MarkRead(entry.Id);
            _shell.ReadState.Save(FeedsCfg.ReadStateRetentionDays);
            AppLog.Info($"PDF generated from the web: {target}");
            return new WebActionResult(true, $"PDF generado: {System.IO.Path.GetFileName(target)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"PDF generation (web) failed for '{entry.Title}'.", ex);
            return new WebActionResult(false, $"No se pudo generar el PDF: {ex.Message}");
        }
    }

    public async Task<WebActionResult> ToggleFullContentAsync(string id, CancellationToken ct)
    {
        FeedEntry original;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out original!))
            {
                return new WebActionResult(false, "La entrada ya no está disponible. Actualiza la lista.");
            }

            if (_fullContent.Remove(id))
            {
                return new WebActionResult(true, "Se usa de nuevo el contenido del feed.");
            }
        }

        if (string.IsNullOrWhiteSpace(original.Link))
        {
            return new WebActionResult(false, "La entrada no tiene enlace al artículo original.");
        }

        try
        {
            var html = await new FullArticleFetcher(_shell.Http).FetchAsync(original.Link, ct);
            lock (_gate)
            {
                _fullContent[id] = html;
            }

            return new WebActionResult(true, "Artículo completo descargado.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn($"Full article download (web) failed for '{original.Link}'.", ex);
            return new WebActionResult(false, $"No se pudo descargar el artículo completo: {ex.Message}");
        }
    }

    public async Task<WebSummary> SummarizeAsync(string id, CancellationToken ct)
    {
        if (!TryGet(id, out var entry))
        {
            return new WebSummary(false, "La entrada ya no está disponible. Actualiza la lista.", null);
        }

        var outcome = await global::DeskPanel.App.Feeds.FeedSummarizer.GenerateAsync(_shell, entry, null, ct);
        if (outcome.Markdown is null)
        {
            return new WebSummary(false, outcome.Status, null);
        }

        lock (_gate)
        {
            _summaries[id] = (outcome.Entry, outcome.Markdown);
        }

        return new WebSummary(true, outcome.Status, outcome.Markdown);
    }

    public async Task<WebActionResult> SaveSummaryAsync(string id, CancellationToken ct)
    {
        (FeedEntry Entry, string Markdown) summary;
        lock (_gate)
        {
            if (!_summaries.TryGetValue(id, out summary))
            {
                return new WebActionResult(false, "Primero genera el resumen.");
            }
        }

        try
        {
            var path = await Task.Run(() => global::DeskPanel.App.Feeds.FeedSummarizer.Save(_shell, summary.Entry, summary.Markdown), ct);
            return new WebActionResult(true, $"Resumen guardado: {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            return new WebActionResult(false, ex.Message);
        }
    }
}
