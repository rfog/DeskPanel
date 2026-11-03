using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Mac.Devonthink;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Settings;

/// <summary>
/// Edits the local profile (<c>~/.DeskPanel/deskpanel.json</c>) and the Keychain secrets. Only the options the Mac
/// build uses are here; the rest of the file is kept as it is.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] Providers = { LlmClientFactory.LmStudio, LlmClientFactory.Gemini, LlmClientFactory.Claude };

    private readonly MacServices _services;

    public SettingsWindow() : this(null!)
    {
    }

    public SettingsWindow(MacServices services)
    {
        InitializeComponent();
        _services = services;
        if (services is null)
        {
            return;
        }

        var config = services.Config;
        var app = config.App;
        ProfileText.Text = $"Perfil local: {config.ConfigDirectory}";

        CssFolderBox.Text = app.Feeds.CssFolder;
        CacheFolderBox.Text = app.CacheFolder;
        ChromePathBox.Text = app.Feeds.ChromePath;
        QuickFoldersBox.Text = string.Join("\n", app.QuickFolders.Select(q => $"{q.Name} | {q.Path}"));
        SourcesBox.Text = string.Join("\n", app.Feeds.Sources.Select(s => $"{s.Name} | {s.Url}"));
        OutputFolderBox.Text = app.Feeds.OutputFolder;
        SummaryFolderBox.Text = app.Feeds.SummaryFolder;
        LoadCssFiles(app.Feeds.CustomCssFile);
        DefaultProfileBox.ItemsSource = app.Feeds.Profiles.Select(p => p.Name).ToList();
        DefaultProfileBox.SelectedItem = app.Feeds.DefaultProfile;

        ProviderBox.ItemsSource = Providers;
        ProviderBox.SelectedItem = Providers.Contains(app.Ai.Provider) ? app.Ai.Provider : LlmClientFactory.LmStudio;
        LmUrlBox.Text = app.Ai.LmStudioUrl;
        LmTokenBox.Text = services.Secrets.Get(SecretNames.LmStudioToken);
        GeminiKeyBox.Text = services.Secrets.Get(SecretNames.GeminiKey);
        ClaudeKeyBox.Text = services.Secrets.Get(SecretNames.ClaudeKey);
        TemperatureBox.Value = (decimal)app.Ai.Temperature;
        ShowModels(Array.Empty<string>(), CurrentModelFor(app.Ai, (string)ProviderBox.SelectedItem!));

        var audio = app.Audio;
        AudioInputBox.Text = audio.InputFolder;
        AudioCleanBox.Text = audio.CleanAudioFolder;
        AudioTranscriptBox.Text = audio.TranscriptFolder;
        AudioSummaryBox.Text = audio.SummaryFolder;
        AudioCleanNoiseBox.IsChecked = audio.CleanNoise;
        AudioTranscribeBox.IsChecked = audio.Transcribe;
        AudioSummarizeBox.IsChecked = audio.Summarize;
        AudioCacheCleanBox.IsChecked = audio.CacheCleanAudio;
        AudioModelBox.Text = audio.WhisperModelFile;
        AudioToolsBox.Text = audio.ToolsFolder;
        AudioInputButton.Click += async (_, _) => await PickFolderAsync(AudioInputBox);
        AudioCleanButton.Click += async (_, _) => await PickFolderAsync(AudioCleanBox);
        AudioTranscriptButton.Click += async (_, _) => await PickFolderAsync(AudioTranscriptBox);
        AudioSummaryButton.Click += async (_, _) => await PickFolderAsync(AudioSummaryBox);
        AudioToolsButton.Click += async (_, _) => await PickFolderAsync(AudioToolsBox);
        ShowAudioToolsStatus();

        var dt = app.Devonthink;
        DtTransportBox.ItemsSource = TransportLabels.Keys.ToList();
        DtTransportBox.SelectedItem = TransportLabels.First(t => t.Value == dt.Transport).Key;
        DtPortBox.Value = dt.Port;
        DtTokenBox.Text = services.Secrets.Get(SecretNames.DevonthinkMcpToken);
        DtCommandBox.Text = dt.StdioCommand;
        DtMaxResultsBox.Value = dt.MaxResults;
        ShowDatabases(dt.Databases);
        DtReadButton.Click += async (_, _) => await ReadDatabasesAsync();

        ProviderBox.SelectionChanged += (_, _) =>
            ShowModels(Array.Empty<string>(), CurrentModelFor(app.Ai, (string)ProviderBox.SelectedItem!));
        ListModelsButton.Click += async (_, _) => await ListModelsAsync();
        CssFolderButton.Click += async (_, _) => await PickFolderAsync(CssFolderBox);
        CacheFolderButton.Click += async (_, _) => await PickFolderAsync(CacheFolderBox);
        OutputFolderButton.Click += async (_, _) => await PickFolderAsync(OutputFolderBox);
        SummaryFolderButton.Click += async (_, _) => await PickFolderAsync(SummaryFolderBox);
        EditCssButton.Click += (_, _) => EditCss();
        OpenCssFolderButton.Click += (_, _) => OpenCssFolder();
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
    }

    private const string NoCss = "(ninguno)";

    private string CssDirectory => Path.GetDirectoryName(_services.ResolveCss("css/x.css"))!;

    // Lists the stylesheets as "css/name.css", the form the configuration stores, so the choice resolves through the
    // same shared-folder rule as the profiles' own CSS.
    private void LoadCssFiles(string? selected)
    {
        var files = new List<string> { NoCss };
        try
        {
            if (Directory.Exists(CssDirectory))
            {
                files.AddRange(Directory.EnumerateFiles(CssDirectory, "*.css")
                    .Select(f => "css/" + Path.GetFileName(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not list the css folder.", ex);
        }

        if (!string.IsNullOrWhiteSpace(selected) && !files.Contains(selected))
        {
            files.Add(selected);
        }

        CustomCssBox.ItemsSource = files;
        CustomCssBox.SelectedItem = string.IsNullOrWhiteSpace(selected) ? NoCss : selected;
    }

    private void EditCss()
    {
        if (CustomCssBox.SelectedItem is string css && css != NoCss && File.Exists(_services.ResolveCss(css)))
        {
            _services.Opener.OpenFile(_services.ResolveCss(css));
        }
        else
        {
            StatusText.Text = "Elige antes un CSS que exista.";
        }
    }

    private void OpenCssFolder()
    {
        if (Directory.Exists(CssDirectory))
        {
            _services.Opener.OpenFolder(CssDirectory);
        }
        else
        {
            StatusText.Text = $"La carpeta de CSS no existe: {CssDirectory}";
        }
    }

    private void ShowAudioToolsStatus()
    {
        var folder = Blank(AudioToolsBox.Text);
        var tools = DeskPanel.Audio.AudioTools.Locate(folder, Blank(AudioModelBox.Text) ?? "ggml-large-v3-turbo.bin");
        string Mark(bool ok, string name) => (ok ? "✔ " : "✘ ") + name;
        AudioToolsStatus.Text = string.Join("   ", Mark(tools.Ffmpeg is not null, "ffmpeg"), Mark(tools.DeepFilter is not null, "deep-filter"), Mark(tools.ModelExists, "modelo de Whisper"));
    }

    private static readonly Dictionary<string, string> TransportLabels = new()
    {
        ["HTTP (servidor en marcha: puerto y token)"] = DevonthinkConfig.TransportHttp,
        ["Proceso local (ayudante MCP, sin token)"] = DevonthinkConfig.TransportStdio,
    };

    private string SelectedTransport => TransportLabels.TryGetValue(DtTransportBox.SelectedItem as string ?? "", out var t) ? t : DevonthinkConfig.TransportHttp;

    private void ShowDatabases(IReadOnlyCollection<DevonthinkDatabase> databases) =>
        DtDatabasesText.Text = databases.Count == 0 ? "Ninguna todavía: pulsa «Leer bases de datos»." : string.Join("\n", databases.Select(d => d.Name));

    /// <summary>Reads the databases with the values typed in this tab (saved or not) and stores the result straight away.</summary>
    private async Task ReadDatabasesAsync()
    {
        if (!await DevonthinkApp.EnsureRunningAsync(this))
        {
            DtStatusText.Text = "DEVONthink no está abierto.";
            return;
        }

        DtReadButton.IsEnabled = false;
        DtStatusText.Text = "Leyendo las bases de datos de DEVONthink…";
        try
        {
            await using var client = await DevonthinkService.ConnectAsync(
                SelectedTransport, (int)(DtPortBox.Value ?? 8420), DtTokenBox.Text, Blank(DtCommandBox.Text), CancellationToken.None);
            var databases = await DevonthinkService.ReadDatabasesAsync(client, CancellationToken.None);
            _services.Config.App.Devonthink.Databases = databases;
            _services.Config.Save();
            ShowDatabases(databases);
            DtStatusText.Text = $"{databases.Count} bases de datos leídas y guardadas.";
        }
        catch (Exception ex) when (ex is McpException or System.Text.Json.JsonException or IOException)
        {
            DtStatusText.Text = ex.Message;
        }
        finally
        {
            DtReadButton.IsEnabled = true;
        }
    }

    private static string CurrentModelFor(AiConfig ai, string provider) => provider switch
    {
        LlmClientFactory.Gemini => ai.GeminiModel,
        LlmClientFactory.Claude => ai.ClaudeModel,
        _ => ai.LmStudioModel,
    };

    private void ShowModels(IReadOnlyCollection<string> listed, string current)
    {
        // The saved model stays selectable even before (or without) listing, so opening the window never loses it.
        var models = listed.ToList();
        if (!string.IsNullOrWhiteSpace(current) && !models.Contains(current))
        {
            models.Insert(0, current);
        }

        ModelBox.ItemsSource = models;
        ModelBox.SelectedItem = string.IsNullOrWhiteSpace(current) ? models.FirstOrDefault() : current;
    }

    private async Task ListModelsAsync()
    {
        var provider = (string)ProviderBox.SelectedItem!;
        var secret = provider switch
        {
            LlmClientFactory.Gemini => GeminiKeyBox.Text,
            LlmClientFactory.Claude => ClaudeKeyBox.Text,
            _ => LmTokenBox.Text,
        };

        ListModelsButton.IsEnabled = false;
        AiStatusText.Text = "Consultando modelos…";
        try
        {
            var client = LlmClientFactory.Create(provider, LmUrlBox.Text, secret);
            var models = await client.ListModelsAsync(CancellationToken.None);
            ShowModels(models.ToList(), ModelBox.SelectedItem as string ?? "");
            AiStatusText.Text = $"{models.Count} modelos disponibles.";
        }
        catch (LlmException ex)
        {
            AiStatusText.Text = ex.Message;
        }
        finally
        {
            ListModelsButton.IsEnabled = true;
        }
    }

    private async Task PickFolderAsync(TextBox target)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            target.Text = path;
        }
    }

    private void Save()
    {
        try
        {
            var app = _services.Config.App;

            var sources = new List<FeedSource>();
            foreach (var line in (SourcesBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || !Uri.TryCreate(parts[1], UriKind.Absolute, out _))
                {
                    StatusText.Text = $"Fuente no válida (usa «Nombre | URL»): {line}";
                    return;
                }

                sources.Add(new FeedSource { Name = parts[0], Url = parts[1] });
            }

            app.Feeds.Sources = sources;
            app.Feeds.CssFolder = Blank(CssFolderBox.Text);
            app.CacheFolder = Blank(CacheFolderBox.Text);
            app.Feeds.ChromePath = Blank(ChromePathBox.Text);

            var quickFolders = new List<QuickFolder>();
            foreach (var line in (QuickFoldersBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                {
                    StatusText.Text = $"Acceso rápido no válido (usa «Nombre | ruta»): {line}";
                    return;
                }

                quickFolders.Add(new QuickFolder { Name = parts[0], Path = parts[1] });
            }

            app.QuickFolders = quickFolders;
            app.Feeds.OutputFolder = (OutputFolderBox.Text ?? "").Trim();
            app.Feeds.SummaryFolder = Blank(SummaryFolderBox.Text);
            app.Feeds.CustomCssFile = CustomCssBox.SelectedItem is string css && css != NoCss ? css : null;
            if (DefaultProfileBox.SelectedItem is string profile)
            {
                app.Feeds.DefaultProfile = profile;
            }

            var provider = (string)ProviderBox.SelectedItem!;
            app.Ai.Provider = provider;
            app.Ai.LmStudioUrl = (LmUrlBox.Text ?? "").Trim();
            app.Ai.Temperature = (double)(TemperatureBox.Value ?? 0.4m);
            var model = ModelBox.SelectedItem as string ?? "";
            switch (provider)
            {
                case LlmClientFactory.Gemini: app.Ai.GeminiModel = model; break;
                case LlmClientFactory.Claude: app.Ai.ClaudeModel = model; break;
                default: app.Ai.LmStudioModel = model; break;
            }

            var audio = app.Audio;
            audio.InputFolder = Blank(AudioInputBox.Text);
            audio.CleanAudioFolder = Blank(AudioCleanBox.Text);
            audio.TranscriptFolder = Blank(AudioTranscriptBox.Text);
            audio.SummaryFolder = Blank(AudioSummaryBox.Text);
            audio.CleanNoise = AudioCleanNoiseBox.IsChecked == true;
            audio.Transcribe = AudioTranscribeBox.IsChecked == true;
            audio.Summarize = AudioSummarizeBox.IsChecked == true;
            audio.CacheCleanAudio = AudioCacheCleanBox.IsChecked == true;
            audio.WhisperModelFile = Blank(AudioModelBox.Text) ?? audio.WhisperModelFile;
            audio.ToolsFolder = Blank(AudioToolsBox.Text);

            var devonthink = app.Devonthink;
            devonthink.Transport = SelectedTransport;
            devonthink.Port = (int)(DtPortBox.Value ?? 8420);
            devonthink.StdioCommand = Blank(DtCommandBox.Text);
            devonthink.MaxResults = (int)(DtMaxResultsBox.Value ?? 200);

            _services.Config.Save();
            _services.Secrets.Set(SecretNames.LmStudioToken, LmTokenBox.Text);
            _services.Secrets.Set(SecretNames.GeminiKey, GeminiKeyBox.Text);
            _services.Secrets.Set(SecretNames.ClaudeKey, ClaudeKeyBox.Text);
            _services.Secrets.Set(SecretNames.DevonthinkMcpToken, DtTokenBox.Text);
            StatusText.Text = "Guardado.";
            ShowAudioToolsStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not save the settings.", ex);
            StatusText.Text = $"No se pudo guardar: {ex.Message}";
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
