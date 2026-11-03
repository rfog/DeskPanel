using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;
using DeskPanel.Core.Platform;
using DeskPanel.Feeds;
using DeskPanel.Pdf.Chromium;
using DeskPanel.Search.Indexing;
using DeskPanel.Search.Extraction;
using DeskPanel.Search.Searching;

namespace DeskPanel.Mac.Services;

/// <summary>What every window shares: configuration, HTTP, secrets, the PDF engine and the system opener.</summary>
public sealed class MacServices : IAsyncDisposable, IDisposable
{
    // Same volume as the home folder (where the synced folders live), so a rename out of here is a real rename.
    public static string TempDirectory { get; } = Path.Combine(ConfigService.LocalAppDirectory, "temp");

    public MacServices(ConfigService config)
    {
        Config = config;

        // Everything written into the synced/watched folders is staged here first (see AtomicFile.StagingDirectory).
        Directory.CreateDirectory(TempDirectory);
        AtomicFile.StagingDirectory = TempDirectory;
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Some feed endpoints reject requests without a browser-like User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) DeskPanel/0.1");
        ReadState = new ReadStateStore(config.ResolveConfigRelative(config.App.Feeds.ReadStateFile));
        ReadState.Load();
        Extractors = ExtractorRegistry.CreateDefault();
        Indexer = new FolderIndexer(Extractors);

        // Per machine and outside any synced folder: it is rewritten on every search.
        SearchHistory = new SearchHistoryStore(Path.Combine(ConfigService.LocalAppDirectory, "search-history.json"));
        SearchHistory.Load();
        PdfRenderer = new ChromiumPdfRenderer(() => config.App.Feeds.ChromePath, TempDirectory);
        CleanTempDirectory();
    }

    public ConfigService Config { get; }

    public HttpClient Http { get; }

    public ISecretStore Secrets { get; } = new KeychainSecretStore();

    public IShellOpener Opener { get; } = new MacShellOpener();

    public ReadStateStore ReadState { get; }

    public IPdfRenderer PdfRenderer { get; }

    public ExtractorRegistry Extractors { get; }

    public FolderIndexer Indexer { get; }

    public SearchService Search { get; } = new();

    public SearchHistoryStore SearchHistory { get; }

    /// <summary>The resolved index storage folder, or null when its path cannot be resolved.</summary>
    public string? ResolveIndexStorage() =>
        Config.Paths.TryResolve(Config.App.Search.IndexStorageFolder, out var storage) ? storage : null;

    /// <summary>Opens today's log in the default app (Console), or the logs folder when nothing has been logged yet.</summary>
    public void OpenLog()
    {
        if (AppLog.CurrentLogFile is { } file && File.Exists(file))
        {
            Opener.OpenFile(file);
        }
        else if (Path.GetDirectoryName(AppLog.CurrentLogFile) is { } folder)
        {
            Directory.CreateDirectory(folder);
            Opener.OpenFolder(folder);
        }
    }

    public string? ResolveCacheFolder() =>
        !string.IsNullOrWhiteSpace(Config.App.CacheFolder) && Config.Paths.TryResolve(Config.App.CacheFolder, out var cache) ? cache : null;

    /// <summary>
    /// Resolves a stylesheet named in the configuration. <c>css/name.css</c> is looked up in the shared CSS folder when
    /// one is set; anything else (absolute, aliased, other relative paths) goes through the usual config-relative rule.
    /// </summary>
    public string ResolveCss(string path)
    {
        var folder = Config.App.Feeds.CssFolder;
        var normalized = path.Replace('\\', '/').Trim();
        if (!string.IsNullOrWhiteSpace(folder) && normalized.StartsWith("css/", StringComparison.OrdinalIgnoreCase)
            && Config.Paths.TryResolve(folder, out var resolved))
        {
            return Path.Combine(resolved, normalized["css/".Length..]);
        }

        return Config.ResolveConfigRelative(path);
    }

    // Called on the UI thread at exit. Running the asynchronous disposal on the thread pool keeps it from needing the
    // (blocked) UI thread to continue, which used to freeze the app on close once Chrome had been started.
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (PdfRenderer is IAsyncDisposable renderer)
        {
            await renderer.DisposeAsync();
        }

        Http.Dispose();
    }

    private static void CleanTempDirectory()
    {
        try
        {
            if (!Directory.Exists(TempDirectory))
            {
                return;
            }

            // Leftovers of an interrupted render; a day old is certainly not in use.
            foreach (var file in Directory.EnumerateFiles(TempDirectory).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not clean the temp directory.", ex);
        }
    }
}
