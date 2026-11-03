using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.App.Infrastructure;
using DeskPanel.Audio;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.App.Settings;

internal sealed record AiProviderOption(string Key, string Label);

/// <summary>The "IA" tab and the tools section of the "Audio" tab.</summary>
internal sealed partial class SettingsViewModel
{
    internal const string SecretLmStudioToken = SecretNames.LmStudioToken;
    internal const string SecretGeminiKey = SecretNames.GeminiKey;
    internal const string SecretClaudeKey = SecretNames.ClaudeKey;

    private readonly Dictionary<string, string> _aiModelByProvider = new();

    public static IReadOnlyList<AiProviderOption> AiProviders { get; } = new[]
    {
        new AiProviderOption(LlmClientFactory.LmStudio, "LM Studio (local)"),
        new AiProviderOption(LlmClientFactory.Gemini, "Gemini (online)"),
        new AiProviderOption(LlmClientFactory.Claude, "Claude (online)"),
    };

    public static IReadOnlyList<string> WhisperModelFiles { get; } = new[]
    {
        "ggml-large-v3-turbo.bin", "ggml-large-v3-turbo-q5_0.bin", "ggml-large-v3.bin",
        "ggml-medium.bin", "ggml-small.bin", "ggml-base.bin", "ggml-tiny.bin",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLmStudio), nameof(IsGemini), nameof(IsClaude))]
    private AiProviderOption _selectedAiProvider = AiProviders[0];

    [ObservableProperty]
    private string _lmStudioUrl = "";

    [ObservableProperty]
    private string _lmStudioToken = "";

    [ObservableProperty]
    private string _geminiKey = "";

    [ObservableProperty]
    private string _claudeKey = "";

    [ObservableProperty]
    private string _aiModel = "";

    [ObservableProperty]
    private string _aiTemperature = "0.4";

    [ObservableProperty]
    private string _aiStatus = "";

    [ObservableProperty]
    private string _audioLanguage = "es";

    [ObservableProperty]
    private string _whisperModelFile = "";

    [ObservableProperty]
    private string _toolsStatus = "";

    [ObservableProperty]
    private string _summaryPrompt = "";

    public ObservableCollection<string> AiModels { get; } = new();

    public bool IsLmStudio => SelectedAiProvider.Key == LlmClientFactory.LmStudio;
    public bool IsGemini => SelectedAiProvider.Key == LlmClientFactory.Gemini;
    public bool IsClaude => SelectedAiProvider.Key == LlmClientFactory.Claude;

    /// <summary>Where to paste the manual command on another machine: shown in the Audio tab.</summary>
    public string InstallCommandText =>
        "powershell -ExecutionPolicy Bypass -File \"" + InstallScriptPath + "\"";

    private string InstallScriptPath
    {
        get
        {
            var bundled = Path.Combine(Shell.AssetsDirectory, "tools", "Install-AudioTools.ps1");
            return File.Exists(bundled) ? bundled : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Install-AudioTools.ps1"));
        }
    }

    // Called from the constructor: the backing fields are assigned directly on purpose so the generated
    // On...Changed callbacks (which clear lists and rewrite the per-provider model map) do not fire during load.
#pragma warning disable MVVMTK0034
    private void LoadAi(AppConfig app, PathResolver paths)
    {
        var ai = app.Ai;
        _aiModelByProvider[LlmClientFactory.LmStudio] = ai.LmStudioModel;
        _aiModelByProvider[LlmClientFactory.Gemini] = ai.GeminiModel;
        _aiModelByProvider[LlmClientFactory.Claude] = ai.ClaudeModel;

        _selectedAiProvider = AiProviders.FirstOrDefault(p => p.Key == ai.Provider) ?? AiProviders[0];
        _lmStudioUrl = ai.LmStudioUrl;
        _lmStudioToken = SecretStore.Get(SecretLmStudioToken) ?? "";
        _geminiKey = SecretStore.Get(SecretGeminiKey) ?? "";
        _claudeKey = SecretStore.Get(SecretClaudeKey) ?? "";
        _aiModel = _aiModelByProvider[_selectedAiProvider.Key];
        _aiTemperature = ai.Temperature.ToString("0.##", CultureInfo.InvariantCulture);

        _audioLanguage = app.Audio.Language;
        _whisperModelFile = app.Audio.WhisperModelFile;
        _toolsStatus = BuildToolsStatus(app, paths);

        // The box always shows the effective prompt, so "default" is something the user can read and start from.
        _summaryPrompt = app.Audio.SummaryPrompt is { Length: > 0 } custom ? custom : SummaryService.DefaultPrompt;
    }
#pragma warning restore MVVMTK0034

    [RelayCommand]
    private void RestoreSummaryPrompt() => SummaryPrompt = SummaryService.DefaultPrompt;

    private void ApplyAi(AppConfig app)
    {
        var ai = app.Ai;
        ai.Provider = SelectedAiProvider.Key;
        ai.LmStudioUrl = LmStudioUrl.Trim();
        ai.LmStudioModel = _aiModelByProvider[LlmClientFactory.LmStudio].Trim();
        ai.GeminiModel = _aiModelByProvider[LlmClientFactory.Gemini].Trim();
        ai.ClaudeModel = _aiModelByProvider[LlmClientFactory.Claude].Trim();
        ai.Temperature = ParseTemperature();

        // Keys live outside deskpanel.json on purpose; see SecretStore.
        SecretStore.Set(SecretLmStudioToken, LmStudioToken);
        SecretStore.Set(SecretGeminiKey, GeminiKey);
        SecretStore.Set(SecretClaudeKey, ClaudeKey);

        // Stored as null while it equals the default, so a later improvement to the default is not masked by a stale copy.
        app.Audio.SummaryPrompt = PromptToStore(SummaryPrompt);

        app.Audio.Language = string.IsNullOrWhiteSpace(AudioLanguage) ? "es" : AudioLanguage.Trim();
        app.Audio.WhisperModelFile = string.IsNullOrWhiteSpace(WhisperModelFile) ? "ggml-large-v3-turbo.bin" : WhisperModelFile.Trim();
    }

    internal static string? PromptToStore(string text) => PromptToStore(text, SummaryService.DefaultPrompt);

    internal static string? PromptToStore(string text, string defaultPrompt) =>
        string.IsNullOrWhiteSpace(text) || Normalize(text) == Normalize(defaultPrompt) ? null : text.Trim();

    // The TextBox returns CRLF; the built-in default is LF. Compare ignoring that and trailing blanks.
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    partial void OnSelectedAiProviderChanged(AiProviderOption value)
    {
        // Each provider remembers its own model; the list of choices belongs to the previous one, so it is cleared.
        AiModels.Clear();
        AiModel = _aiModelByProvider.GetValueOrDefault(value.Key, "");
        AiStatus = "";
    }

    partial void OnAiModelChanged(string value) => _aiModelByProvider[SelectedAiProvider.Key] = value;

    partial void OnWhisperModelFileChanged(string value) => RefreshAudioTools();

    [RelayCommand]
    private async Task ListAiModelsAsync()
    {
        AiStatus = "Consultando los modelos disponibles…";
        try
        {
            var client = LlmClientFactory.Create(SelectedAiProvider.Key, LmStudioUrl, CurrentSecret());
            var models = await client.ListModelsAsync(default);

            AiModels.Clear();
            foreach (var model in models)
            {
                AiModels.Add(model);
            }

            if (models.Count == 0)
            {
                AiStatus = "El proveedor no devolvió ningún modelo.";
                return;
            }

            if (string.IsNullOrWhiteSpace(AiModel) || !models.Contains(AiModel))
            {
                AiModel = models[0];
            }

            AiStatus = $"{models.Count} modelos disponibles.";
        }
        catch (LlmException ex)
        {
            AiStatus = "Error: " + ex.Message;
        }
    }

    private string? CurrentSecret() => SelectedAiProvider.Key switch
    {
        LlmClientFactory.LmStudio => LmStudioToken,
        LlmClientFactory.Gemini => GeminiKey,
        _ => ClaudeKey,
    };

    private double ParseTemperature() =>
        double.TryParse(AiTemperature.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            ? Math.Clamp(t, 0, 1)
            : 0.4;

    [RelayCommand]
    private void RefreshAudioTools() => ToolsStatus = BuildToolsStatus(_shell.Config.App, _shell.Config.Paths);

    [RelayCommand]
    private void OpenToolsFolder()
    {
        var folder = LocateTools(_shell.Config.App, _shell.Config.Paths).ToolsFolder;
        Directory.CreateDirectory(folder);
        _shell.OpenFolder(folder);
    }

    /// <summary>Runs the same script other machines use, in a visible console so the download can be followed.</summary>
    [RelayCommand]
    private void InstallAudioTools()
    {
        var script = InstallScriptPath;
        if (!File.Exists(script))
        {
            MessageBox.Show($"No se encuentra el script de instalación:\n{script}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var model = WhisperModelFile.Trim();
        var name = model.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) && model.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            ? model[5..^4]
            : "large-v3-turbo";
        var tools = LocateTools(_shell.Config.App, _shell.Config.Paths).ToolsFolder;

        var answer = MessageBox.Show(
            "Se descargarán las herramientas que falten en:\n" + tools + "\n\n" +
            "• ffmpeg (~109 MB, gyan.dev)\n• deep-filter (~26 MB, GitHub)\n• modelo de Whisper " + name + " (hasta 3 GB, HuggingFace)\n\n¿Continuar?",
            "DeskPanel", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var info = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ToolsFolder \"{tools}\" -WhisperModel {name}")
            {
                UseShellExecute = true,
            };
            var process = Process.Start(info);
            if (process is not null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Application.Current.Dispatcher.Invoke(RefreshAudioTools);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppLog.Error("Could not start the tools installer.", ex);
            MessageBox.Show($"No se pudo iniciar PowerShell:\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private AudioTools LocateTools(AppConfig app, PathResolver paths)
    {
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(app.Audio.ToolsFolder) && paths.TryResolve(app.Audio.ToolsFolder, out var resolved))
        {
            folder = resolved;
        }

        return AudioTools.Locate(folder, string.IsNullOrWhiteSpace(WhisperModelFile) ? app.Audio.WhisperModelFile : WhisperModelFile.Trim());
    }

    private string BuildToolsStatus(AppConfig app, PathResolver paths)
    {
        var tools = LocateTools(app, paths);
        var builder = new StringBuilder();
        builder.AppendLine($"ffmpeg:        {(tools.Ffmpeg is null ? "✘ no encontrado" : "✔ " + tools.Ffmpeg)}");
        builder.AppendLine($"deep-filter:   {(tools.DeepFilter is null ? "✘ no encontrado" : "✔ " + tools.DeepFilter)}");
        builder.AppendLine($"Modelo Whisper: {(tools.ModelExists ? "✔ " : "✘ falta ")}{tools.ModelPath}");
        builder.Append($"Carpeta de herramientas: {tools.ToolsFolder}");
        return builder.ToString();
    }
}
