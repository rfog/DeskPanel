using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DeskPanel.App.About;
using DeskPanel.App.Audio;
using DeskPanel.App.Feeds;
using DeskPanel.App.Indexes;
using DeskPanel.App.Infrastructure;
using DeskPanel.App.Panel;
using DeskPanel.App.Pdf;
using DeskPanel.App.Search;
using DeskPanel.App.Settings;
using DeskPanel.App.Web;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Feeds;
using DeskPanel.Search.Extraction;
using DeskPanel.Search.Indexing;
using DeskPanel.Search.Searching;

namespace DeskPanel.App;

/// <summary>
/// Composition root and window coordinator. Kept deliberately small and DI-free: the app has a handful of
/// long-lived services, and explicit wiring is easier to follow than a container at this size.
/// </summary>
internal sealed class Shell : IDisposable
{
    private readonly HotKeyService _hotKey = new();
    private readonly SystemKeys _systemKeys = new();
    private readonly TrayIcon _tray;

    private PanelWindow? _panel;
    private FeedsWindow? _feeds;
    private SearchWindow? _search;
    private IndexManagerWindow? _indexes;
    private SettingsWindow? _settings;
    private AudioWindow? _audio;
    private AudioViewModel? _audioVm;
    private AboutWindow? _about;
    private PdfListWindow? _pdfList;
    private Process? _verifier;

    public Shell()
    {
        _tray = new TrayIcon(this);
        Http = CreateHttpClient();
        WebView = new WebViewEnvironmentProvider(Path.Combine(ConfigService.LocalAppDirectory, "WebView2"));
        PdfRenderer = new PdfRenderer(WebView, TempDirectory);
        Web = new WebServerManager(this);
        Extractors = ExtractorRegistry.CreateDefault();
        Indexer = new FolderIndexer(Extractors);
        SearchHistory = new SearchHistoryStore(Path.Combine(ConfigService.LocalAppDirectory, "search-history.json"));
        SearchHistory.Load();
    }

    public ConfigService Config { get; } = new();
    public HttpClient Http { get; }
    public WebViewEnvironmentProvider WebView { get; }
    public PdfRenderer PdfRenderer { get; }
    public WebServerManager Web { get; }
    public ExtractorRegistry Extractors { get; }
    public FolderIndexer Indexer { get; }
    public SearchHistoryStore SearchHistory { get; }
    public SearchService Search { get; } = new();
    public ReadStateStore ReadState { get; private set; } = null!;

    public static string AssetsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "assets");
    public static string TempDirectory { get; } = Path.Combine(ConfigService.LocalAppDirectory, "temp");

    public void Start()
    {
        CleanTempDirectory();
        LoadConfiguration();
        _tray.Show();
        _hotKey.Pressed += (_, _) => TogglePanel();
        RegisterHotKey();
        RegisterSystemKeys();
        _ = Web.ApplyAsync();

        if (Config.CreatedDefaults)
        {
            _tray.Notify("DeskPanel", "Se ha creado una configuración por defecto. Revisa machine.json y deskpanel.json.");
        }
    }

    /// <summary>The "Caché" folder of this machine, or null when none is configured (or its alias is undefined here).</summary>
    public string? ResolveCacheFolder() =>
        !string.IsNullOrWhiteSpace(Config.App.CacheFolder) && Config.Paths.TryResolve(Config.App.CacheFolder, out var cache) ? cache : null;

    /// <summary>A tray balloon, for components that live outside the tray's own code.</summary>
    public void Notify(string title, string text, bool isWarning = false) => _tray.Notify(title, text, isWarning);

    public void ReloadConfiguration()
    {
        try
        {
            LoadConfiguration();
            RegisterHotKey();
            RegisterSystemKeys();

            // A reload may have changed the port, the password or the published indexes.
            _ = Web.ApplyAsync();
            _panel?.ViewModel.Refresh();
            _tray.Notify("DeskPanel", "Configuración recargada.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Reload failed.", ex);
            MessageBox.Show($"La configuración no es válida:\n\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
        }
        else
        {
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        _panel ??= new PanelWindow(new PanelViewModel(this));
        _panel.ShowNearCursor();
    }

    public void HidePanel() => _panel?.Hide();

    public void OpenFeeds() => ShowSingleton(ref _feeds, () => new FeedsWindow(new FeedsViewModel(this)));

    public void OpenSearch() => ShowSingleton(ref _search, () => new SearchWindow(this));

    public void OpenIndexManager() => ShowSingleton(ref _indexes, () => new IndexManagerWindow(new IndexManagerViewModel(this)));

    /// <summary>One audio view model for the whole app, so the window and the web interface see the same run. UI thread only.</summary>
    public AudioViewModel AudioVm => _audioVm ??= new AudioViewModel(this);

    public void OpenAudio() => ShowSingleton(ref _audio, () => new AudioWindow(AudioVm));

    /// <summary>Opens the audio window and copies the dropped files into the input folder (ticking only those).</summary>
    public Task ImportAudioAsync(IEnumerable<string> files)
    {
        OpenAudio();
        return _audio!.ViewModel.ImportAsync(files);
    }

    /// <summary>Opens the web interface of the remote DeskPanel configured in Settings › Web, in the default browser.</summary>
    public void OpenRemoteWeb()
    {
        var web = Config.Machine.Web;
        if (WebServerConfig.BuildRemoteUrl(web.RemoteAddress, web.Port) is not { } url)
        {
            Notify("DeskPanel", "Indica la IP del servidor remoto en Configurar › Web › Servidor remoto.", isWarning: true);
            return;
        }

        OpenWithShell(url.AbsoluteUri);
    }

    public void OpenAbout() => ShowSingleton(ref _about, () => new AboutWindow());

    public void OpenSettings() => ShowSingleton(ref _settings, () => new SettingsWindow(new SettingsViewModel(this)));

    /// <summary>
    /// Launches VerificaFicheros (its own exe, built next to this one). Only one instance runs: two of them would
    /// edit the same MonitoredFolders.txt and per-folder databases at once.
    /// </summary>
    public void OpenFileVerifier()
    {
        if (_verifier is { HasExited: false })
        {
            SetForegroundWindow(_verifier.MainWindowHandle);
            return;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "VerificaFicheros.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show($"No se encuentra VerificaFicheros.exe junto a DeskPanel:\n{exe}", "DeskPanel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _verifier = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppLog.Error("Could not start VerificaFicheros.", ex);
            MessageBox.Show($"No se pudo iniciar VerificaFicheros:\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void OpenPdfList() => ShowSingleton(ref _pdfList, () => new PdfListWindow(new PdfListViewModel(this)));

    public void OpenPdfPreview(string pdfPath, string? searchText = null)
    {
        // Multiple preview windows are allowed on purpose: comparing two generated PDFs is a common case.
        var window = new PdfPreviewWindow(this, pdfPath, searchText);
        window.Show();
        window.Activate();
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"La carpeta no existe:\n{path}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void RevealFile(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public void OpenWithShell(string pathOrUrl) =>
        Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });

    public void EditConfiguration() => OpenInEditor(Config.AppConfigPath);

    public void OpenInEditor(string path)
    {
        try
        {
            OpenWithShell(path);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // .json and .css frequently have no editor association on a fresh Windows install.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    public void OpenLog()
    {
        if (AppLog.CurrentLogFile is { } log && File.Exists(log))
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{log}\"") { UseShellExecute = true });
        }
    }

    public void Dispose()
    {
        Web.Dispose();
        _hotKey.Dispose();
        _systemKeys.Dispose();
        _tray.Dispose();
        PdfRenderer.Dispose();
        Http.Dispose();
    }

    private void LoadConfiguration()
    {
        Config.Load();
        SeedCssTemplates();

        ReadState = new ReadStateStore(Config.ResolveConfigRelative(Config.App.Feeds.ReadStateFile));
        ReadState.Load();

        var conflicts = Config.FindConflictCopies();
        if (conflicts.Count > 0)
        {
            AppLog.Warn("Sync conflict copies found: " + string.Join(", ", conflicts));
            _tray.Notify("DeskPanel",
                $"Hay {conflicts.Count} copia(s) en conflicto de la configuración en {Config.ConfigDirectory}. Revísalas y bórralas.",
                isWarning: true);
        }
    }

    private void RegisterSystemKeys()
    {
        var problems = _systemKeys.Apply(Config.App.SystemKeys);
        if (problems.Count > 0)
        {
            _tray.Notify("DeskPanel", "No se pudieron registrar atajos del sistema:\n" + string.Join("\n", problems), isWarning: true);
        }
    }

    private void RegisterHotKey()
    {
        var hotKey = Config.App.HotKey;
        if (!Enum.TryParse<ModifierKeys>(hotKey.Modifiers, ignoreCase: true, out var modifiers)
            || !Enum.TryParse<Key>(hotKey.Key, ignoreCase: true, out var key))
        {
            _tray.Notify("DeskPanel", $"Atajo no válido: {hotKey.Modifiers} + {hotKey.Key}", isWarning: true);
            return;
        }

        if (!_hotKey.TryRegister(modifiers, key, out var error))
        {
            AppLog.Warn($"Hotkey {modifiers}+{key} not registered: {error}");
            _tray.Notify("DeskPanel", $"No se pudo registrar {modifiers} + {key}: {error}", isWarning: true);
        }
    }

    /// <summary>Copies bundled CSS profiles into the synced config folder once, so edits there follow the user.</summary>
    private void SeedCssTemplates()
    {
        var source = Path.Combine(AssetsDirectory, "css");
        if (!Directory.Exists(source))
        {
            return;
        }

        var target = Path.Combine(Config.ConfigDirectory, "css");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*.css"))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Copy(file, destination);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void CleanTempDirectory()
    {
        try
        {
            Directory.CreateDirectory(TempDirectory);
            foreach (var file in Directory.EnumerateFiles(TempDirectory).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)))
            {
                File.Delete(file);
            }
        }
        catch (IOException ex)
        {
            AppLog.Warn("Temp cleanup skipped.", ex);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Some feed endpoints reject requests without a browser-like User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeskPanel/0.1");
        return client;
    }

    private static void ShowSingleton<T>(ref T? field, Func<T> factory) where T : Window
    {
        if (field is null || !field.IsLoaded)
        {
            var window = factory();
            field = window;
            window.Show();
        }
        else if (field.WindowState == WindowState.Minimized)
        {
            field.WindowState = WindowState.Normal;
        }

        field.Activate();
    }
}
