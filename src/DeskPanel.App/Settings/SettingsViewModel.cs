using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Feeds;
using Microsoft.Win32;

namespace DeskPanel.App.Settings;

/// <summary>Plain row type: the DataGrid commits edits straight into the properties.</summary>
internal sealed class FeedSourceRow
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

internal sealed partial class SettingsViewModel : ObservableObject
{
    public const string NoCss = "(ninguno)";

    private readonly Shell _shell;
    private ModifierKeys _hotKeyModifiers;
    private Key _hotKeyKey;

    [ObservableProperty]
    private string _hotKeyText = "";

    [ObservableProperty]
    private bool _keepOpenOnFocusLoss;

    [ObservableProperty]
    private string _configDirectory = "";

    [ObservableProperty]
    private string _feedsOutputFolder = "";

    [ObservableProperty]
    private string _cacheFolder = "";

    [ObservableProperty]
    private string _feedsArchiveFolder = "";

    [ObservableProperty]
    private string _feedsSummaryFolder = "";

    [ObservableProperty]
    private string _feedsSummaryPrompt = "";

    [ObservableProperty]
    private string _feedsTranslationPrompt = "";

    [ObservableProperty]
    private FeedSourceRow? _selectedFeed;

    [ObservableProperty]
    private string _selectedCss = NoCss;

    [ObservableProperty]
    private string _audioInputFolder = "";

    [ObservableProperty]
    private string _audioCleanFolder = "";

    [ObservableProperty]
    private string _audioTranscriptFolder = "";

    [ObservableProperty]
    private string _audioSummaryFolder = "";

    [ObservableProperty]
    private bool _cleanNoise;

    [ObservableProperty]
    private bool _transcribe;

    [ObservableProperty]
    private bool _summarize;

    [ObservableProperty]
    private string _indexStorageFolder = "";

    public SettingsViewModel(Shell shell)
    {
        _shell = shell;
        var config = shell.Config;
        var app = config.App;

        _hotKeyModifiers = Enum.TryParse<ModifierKeys>(app.HotKey.Modifiers, ignoreCase: true, out var modifiers)
            ? modifiers : ModifierKeys.Control | ModifierKeys.Alt;
        _hotKeyKey = Enum.TryParse<Key>(app.HotKey.Key, ignoreCase: true, out var key) ? key : Key.Space;
        _hotKeyText = FormatHotKey(_hotKeyModifiers, _hotKeyKey);
        LoadSystemKeys(app);
        _keepOpenOnFocusLoss = app.Panel.KeepOpenOnFocusLoss;
        _configDirectory = config.Machine.ConfigDirectory;
        LoadRoots(config.Machine);

        _feedsOutputFolder = app.Feeds.OutputFolder;
        _cacheFolder = app.CacheFolder ?? "";
        _feedsArchiveFolder = app.Feeds.ArchiveFolder ?? "";
        _feedsSummaryFolder = app.Feeds.SummaryFolder ?? "";

        // Like the audio prompt: the box shows the effective text, and the default is stored as null.
        _feedsSummaryPrompt = app.Feeds.SummaryPrompt is { Length: > 0 } customFeedPrompt ? customFeedPrompt : FeedSummaryPrompt.Default;
        _feedsTranslationPrompt = app.Feeds.TranslationPrompt is { Length: > 0 } customTranslation ? customTranslation : FeedTranslationPrompt.Default;
        foreach (var source in app.Feeds.Sources)
        {
            Feeds.Add(new FeedSourceRow { Name = source.Name, Url = source.Url });
        }

        LoadCssFiles(app.Feeds.CustomCssFile);

        _audioInputFolder = app.Audio.InputFolder ?? "";
        _audioCleanFolder = app.Audio.CleanAudioFolder ?? "";
        _audioTranscriptFolder = app.Audio.TranscriptFolder ?? "";
        _audioSummaryFolder = app.Audio.SummaryFolder ?? "";
        _cleanNoise = app.Audio.CleanNoise;
        _transcribe = app.Audio.Transcribe;
        _summarize = app.Audio.Summarize;

        _indexStorageFolder = app.Search.IndexStorageFolder;

        LoadAi(app, config.Paths);
        LoadWeb(config.Machine, app);
    }

    public event Action? CloseRequested;

    public ObservableCollection<FeedSourceRow> Feeds { get; } = new();
    public ObservableCollection<string> CssFiles { get; } = new();

    private string CssDirectory => Path.Combine(_shell.Config.ConfigDirectory, "css");

    private void LoadCssFiles(string? configured)
    {
        CssFiles.Clear();
        CssFiles.Add(NoCss);
        if (Directory.Exists(CssDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(CssDirectory, "*.css").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                CssFiles.Add("css/" + Path.GetFileName(file));
            }
        }

        // A configured file that no longer exists stays selectable, so opening and saving the dialog does not silently drop it.
        var match = string.IsNullOrWhiteSpace(configured)
            ? NoCss
            : CssFiles.FirstOrDefault(c => string.Equals(c, configured.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            match = configured!;
            CssFiles.Add(match);
        }

        SelectedCss = match;
    }

    [RelayCommand]
    private void OpenCssFolder()
    {
        Directory.CreateDirectory(CssDirectory);
        _shell.OpenFolder(CssDirectory);
    }

    [RelayCommand]
    private void EditCss()
    {
        if (SelectedCss != NoCss && _shell.Config.Paths.TryResolve(Path.Combine(_shell.Config.ConfigDirectory, SelectedCss), out var path)
            && File.Exists(path))
        {
            _shell.OpenInEditor(path);
        }
    }

    public string ConfigDirectoryPlaceholder => ConfigService.LocalAppDirectory;

    public void SetHotKey(ModifierKeys modifiers, Key key)
    {
        _hotKeyModifiers = modifiers;
        _hotKeyKey = key;
        HotKeyText = FormatHotKey(modifiers, key);
    }

    [RelayCommand]
    private void AddFeed()
    {
        var row = new FeedSourceRow { Name = $"Feed {Feeds.Count + 1}" };
        Feeds.Add(row);
        SelectedFeed = row;
    }

    [RelayCommand]
    private void RemoveFeed()
    {
        if (SelectedFeed is { } row)
        {
            Feeds.Remove(row);
        }
    }

    [RelayCommand]
    private void BrowseConfigDirectory() => Browse("Carpeta de configuración", ConfigDirectory, v => ConfigDirectory = v);

    [RelayCommand]
    private void BrowseFeedsOutput() => Browse("Carpeta de PDF generados", FeedsOutputFolder, v => FeedsOutputFolder = v);

    [RelayCommand]
    private void BrowseCache() => Browse("Carpeta de caché", CacheFolder, v => CacheFolder = v);

    [RelayCommand]
    private void BrowseFeedsArchive() => Browse("Carpeta de archivo de PDF", FeedsArchiveFolder, v => FeedsArchiveFolder = v);

    [RelayCommand]
    private void BrowseFeedsSummary() => Browse("Carpeta de resúmenes de feeds", FeedsSummaryFolder, v => FeedsSummaryFolder = v);

    [RelayCommand]
    private void RestoreFeedsSummaryPrompt() => FeedsSummaryPrompt = FeedSummaryPrompt.Default;

    [RelayCommand]
    private void RestoreFeedsTranslationPrompt() => FeedsTranslationPrompt = FeedTranslationPrompt.Default;

    [RelayCommand]
    private void BrowseIndexStorage() => Browse("Carpeta de índices de búsqueda", IndexStorageFolder, v => IndexStorageFolder = v);

    [RelayCommand]
    private void BrowseAudioFolder(string? which)
    {
        switch (which)
        {
            case "input": Browse("Audios sin procesar", AudioInputFolder, v => AudioInputFolder = v); break;
            case "clean": Browse("Audio procesado", AudioCleanFolder, v => AudioCleanFolder = v); break;
            case "transcript": Browse("Transcripciones", AudioTranscriptFolder, v => AudioTranscriptFolder = v); break;
            case "summary": Browse("Resúmenes", AudioSummaryFolder, v => AudioSummaryFolder = v); break;
        }
    }

    [RelayCommand]
    private void EditJson()
    {
        _shell.EditConfiguration();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Save()
    {
        if (!Validate(out var feeds) || !ConfirmRootChanges())
        {
            return;
        }

        // The aliases go in first, in memory only: everything below (the config directory may itself be "{Alias}/…")
        // must resolve against the new map. If the save is abandoned or fails, the previous map is put back.
        var config = _shell.Config;
        var previousRoots = new Dictionary<string, string>(config.Machine.Roots, StringComparer.OrdinalIgnoreCase);
        var rootsChanged = !SameRoots(previousRoots, RootsAsDictionary());
        if (rootsChanged)
        {
            config.ApplyRoots(RootsAsDictionary());
        }

        var saved = false;
        try
        {
            saved = SaveCore(feeds, rootsChanged);
        }
        finally
        {
            if (!saved && rootsChanged)
            {
                config.ApplyRoots(previousRoots);
            }
        }
    }

    /// <returns>True when everything was written and the dialog was closed; false if the user backed out or it failed.</returns>
    private bool SaveCore(List<FeedSource> feeds, bool rootsChanged)
    {
        var config = _shell.Config;
        var app = config.App;
        var newConfigDirectory = ConfigDirectory.Trim();
        var overwrite = false;

        if (!string.Equals(newConfigDirectory, config.Machine.ConfigDirectory.Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(newConfigDirectory) && config.HasAppConfigIn(newConfigDirectory))
        {
            var answer = MessageBox.Show(
                "La carpeta elegida ya contiene un deskpanel.json.\n\n" +
                "Sí: usar ese fichero (se descartan los cambios hechos en este diálogo).\n" +
                "No: sustituirlo por la configuración actual.",
                "DeskPanel", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                return false;
            }

            overwrite = answer == MessageBoxResult.No;
            if (!overwrite)
            {
                return ApplyConfigDirectoryOnly(newConfigDirectory);
            }
        }

        app.HotKey.Modifiers = _hotKeyModifiers.ToString();
        app.HotKey.Key = _hotKeyKey.ToString();
        ApplySystemKeys(app);
        app.Panel.KeepOpenOnFocusLoss = KeepOpenOnFocusLoss;

        app.CacheFolder = NullIfBlank(CacheFolder);
        app.Feeds.OutputFolder = FeedsOutputFolder.Trim();
        app.Feeds.ArchiveFolder = NullIfBlank(FeedsArchiveFolder);
        app.Feeds.SummaryFolder = NullIfBlank(FeedsSummaryFolder);
        app.Feeds.SummaryPrompt = PromptToStore(FeedsSummaryPrompt, FeedSummaryPrompt.Default);
        app.Feeds.TranslationPrompt = PromptToStore(FeedsTranslationPrompt, FeedTranslationPrompt.Default);
        app.Feeds.Sources = feeds;
        app.Feeds.CustomCssFile = SelectedCss == NoCss ? null : SelectedCss;

        app.Audio.InputFolder = NullIfBlank(AudioInputFolder);
        app.Audio.CleanAudioFolder = NullIfBlank(AudioCleanFolder);
        app.Audio.TranscriptFolder = NullIfBlank(AudioTranscriptFolder);
        app.Audio.SummaryFolder = NullIfBlank(AudioSummaryFolder);
        app.Audio.CleanNoise = CleanNoise;
        app.Audio.Transcribe = Transcribe;
        app.Audio.Summarize = Summarize;

        app.Search.IndexStorageFolder = IndexStorageFolder.Trim();
        ApplyAi(app);
        ApplyWeb(config.Machine);

        try
        {
            // Save in the current location first, then move: a failure while relocating loses nothing.
            config.Save();

            // machine.json holds the web server settings (and the aliases); written here even when nothing moves.
            config.SaveMachine();

            // Relocating also writes machine.json (with the new aliases). It is needed when the directory text
            // changed, and also when only an alias moved: "{Cloud}/DeskPanel" may now point somewhere else, and the
            // settings have to follow it rather than be recreated empty there.
            if (rootsChanged || !string.Equals(newConfigDirectory, config.Machine.ConfigDirectory.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                config.RelocateConfigDirectory(newConfigDirectory, overwrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("Saving settings failed.", ex);
            MessageBox.Show($"No se pudo guardar la configuración:\n\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        CloseRequested?.Invoke();
        _shell.ReloadConfiguration();
        return true;
    }

    private bool ApplyConfigDirectoryOnly(string newConfigDirectory)
    {
        try
        {
            _shell.Config.RelocateConfigDirectory(newConfigDirectory, overwriteExisting: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Changing config directory failed.", ex);
            MessageBox.Show($"No se pudo cambiar la carpeta:\n\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        CloseRequested?.Invoke();
        _shell.ReloadConfiguration();
        return true;
    }

    private bool Validate(out System.Collections.Generic.List<FeedSource> feeds)
    {
        feeds = new();
        string? error = null;

        if (_hotKeyKey == Key.None || _hotKeyModifiers == ModifierKeys.None)
        {
            error = "El atajo necesita al menos un modificador (Ctrl, Alt, Mayús o Win) y una tecla.";
        }
        else if (ValidateSystemKeys() is { } systemKeysError)
        {
            error = systemKeysError;
        }
        else if (ValidateRoots() is { } rootsError)
        {
            error = rootsError;
        }
        else if (!string.IsNullOrWhiteSpace(ConfigDirectory) && !CurrentPaths.TryResolve(ConfigDirectory, out _))
        {
            error = "La carpeta de configuración no es válida o usa un alias que no está definido en las carpetas base.";
        }
        else if (ValidateWeb() is { } webError)
        {
            error = webError;
        }
        else if (string.IsNullOrWhiteSpace(FeedsOutputFolder))
        {
            error = "Indica la carpeta de PDF generados.";
        }
        else if (string.IsNullOrWhiteSpace(IndexStorageFolder))
        {
            error = "Indica la carpeta de los índices de búsqueda.";
        }

        foreach (var row in Feeds.Where(r => !string.IsNullOrWhiteSpace(r.Url) || !string.IsNullOrWhiteSpace(r.Name)))
        {
            var url = row.Url.Trim();
            if (error is null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                                  || uri.Scheme is not ("http" or "https")))
            {
                error = $"La URL del feed \"{row.Name}\" no es válida (debe empezar por http:// o https://).";
            }

            feeds.Add(new FeedSource { Name = string.IsNullOrWhiteSpace(row.Name) ? url : row.Name.Trim(), Url = url });
        }

        if (error is not null)
        {
            MessageBox.Show(error, "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return error is null;
    }

    private void Browse(string title, string current, Action<string> assign)
    {
        var dialog = new OpenFolderDialog { Title = title };
        var paths = CurrentPaths;
        if (paths.TryResolve(current, out var resolved) && Directory.Exists(resolved))
        {
            dialog.InitialDirectory = resolved;
        }

        if (dialog.ShowDialog() == true)
        {
            // Aliased form keeps deskpanel.json valid on every machine that defines the same alias.
            assign(paths.MakePortable(dialog.FolderName));
        }
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatHotKey(ModifierKeys modifiers, Key key) => FormatChord(modifiers, key);

    internal static string FormatChord(ModifierKeys modifiers, Key key)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Mayús");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (key != Key.None) parts.Add(key.ToString());
        return string.Join(" + ", parts);
    }
}
