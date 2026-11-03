using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskPanel.Audio;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Feeds;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Audio;

/// <summary>An audio waiting in the input folder, with the tick that says whether the next run includes it.</summary>
public sealed class AudioFileItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public AudioFileItem(string path) => Path = path;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>
/// Runs the audios of the input folder through the enabled stages (noise removal, transcription, summary), one after
/// another. The original is moved to the Trash only when every enabled stage of that audio succeeded.
/// </summary>
public partial class AudioWindow : Window
{
    private readonly MacServices _services = null!;
    private readonly ObservableCollection<AudioFileItem> _files = new();
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;

    public AudioWindow() => InitializeComponent();

    public AudioWindow(MacServices services) : this()
    {
        _services = services;
        FilesList.ItemsSource = _files;
        RefreshButton.Click += (_, _) => Refresh();
        OpenInputButton.Click += (_, _) => OpenInput();
        ProcessButton.Click += async (_, _) => await ProcessAsync();
        CancelButton.Click += (_, _) =>
        {
            Log("Cancelando…");
            _cancellation?.Cancel();
        };
        Closing += (_, _) => _cancellation?.Cancel();

        // Settings may have changed the folders or tools while this window was in the background.
        Activated += (_, _) =>
        {
            if (!_isBusy)
            {
                Refresh();
            }
        };
        SetBusy(false);
        Refresh();
    }

    private AudioConfig Audio => _services.Config.App.Audio;

    /// <summary>True while audios are being processed; the main window asks before closing over a running job.</summary>
    public bool IsBusy => _isBusy;

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ProcessButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void Refresh()
    {
        var deselected = _files.Where(f => !f.IsSelected).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _files.Clear();

        if (!TryResolve(Audio.InputFolder, out var input))
        {
            StatusText.Text = "Configura la carpeta de audios sin procesar (Ajustes › Audio).";
        }
        else if (!Directory.Exists(input))
        {
            StatusText.Text = $"La carpeta de entrada no existe: {input}";
        }
        else
        {
            foreach (var path in Directory.EnumerateFiles(input).Where(AudioFileTypes.IsAudio)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                _files.Add(new AudioFileItem(path) { IsSelected = !deselected.Contains(path) });
            }

            StatusText.Text = _files.Count == 0 ? "No hay audios en la carpeta de entrada." : $"{_files.Count} audio(s) pendientes.";
        }

        var warning = BuildToolsWarning();
        WarningText.Text = warning;
        WarningText.IsVisible = warning.Length > 0;
    }

    private void OpenInput()
    {
        if (TryResolve(Audio.InputFolder, out var input) && Directory.Exists(input))
        {
            _services.Opener.OpenFolder(input);
        }
    }

    private async Task ProcessAsync()
    {
        var selected = _files.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Marca al menos un audio.";
            return;
        }

        var problems = new List<string>();
        var plan = BuildPlan(problems);
        if (plan is null)
        {
            StatusText.Text = "No se puede empezar: " + string.Join(" · ", problems);
            return;
        }

        _cancellation = new CancellationTokenSource();
        var ct = _cancellation.Token;
        SetBusy(true);
        LogBox.Text = "";
        Progress.Value = 0;
        int done = 0, failed = 0, index = 0;
        try
        {
            foreach (var file in selected)
            {
                index++;
                ct.ThrowIfCancellationRequested();
                Log("");
                Log($"━━ [{index}/{selected.Count}] {file.Name}");

                var current = index;
                var progress = new Progress<AudioProgress>(p =>
                {
                    StepText.Text = $"{p.Step} — audio {current}/{selected.Count}";
                    var perFile = p.Fraction is { } f ? ((p.StepNumber - 1) + f) / p.StepCount : (p.StepNumber - 1.0) / p.StepCount;
                    Progress.Value = ((current - 1) + Math.Clamp(perFile, 0, 1)) / selected.Count * 100;
                });

                var path = file.Path;
                try
                {
                    // Looked up per file, right before it starts: the previous one of this run is already in the folder.
                    path = await Task.Run(() => AudioNumbering.EnsureNumbered(file.Path, plan.CleanAudioFolder, Log), ct);
                    var result = await AudioPipeline.RunAsync(plan with { InputPath = path }, progress, Log, ct);
                    done++;
                    Log($"✔ Terminado ({result.Outputs.Count} fichero(s) generados).");
                    TrashOriginal(path);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Error($"Audio processing failed for {path}.", ex);
                    Log("✘ ERROR: " + ex.Message);
                    Log("  El audio original se conserva en la carpeta de entrada.");
                }
            }

            StatusText.Text = failed == 0 ? $"Procesados {done} audio(s) sin errores." : $"{done} correcto(s), {failed} con error (se han conservado).";
            Progress.Value = 100;
            StepText.Text = "Terminado";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelado. El audio en curso se conserva en la carpeta de entrada.";
            StepText.Text = "Cancelado";
            Log("Cancelado por el usuario.");
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
            Refresh();
        }
    }

    // Only after every enabled stage succeeded, and to the Trash so a bad result (or a wrong click on a synced folder) is recoverable.
    private void TrashOriginal(string path)
    {
        try
        {
            MacTrash.MoveToTrash(path);
            Log("  Audio original enviado a la Papelera.");
        }
        catch (IOException ex)
        {
            Log($"  ⚠ No se pudo enviar el audio original a la Papelera (se conserva): {ex.Message}");
        }
    }

    /// <summary>Validates the configuration and builds the per-file job template; null (with reasons) if something is missing.</summary>
    private AudioJobOptions? BuildPlan(List<string> problems)
    {
        var app = _services.Config.App;
        var audio = app.Audio;
        var needTranscript = audio.Transcribe || audio.Summarize;

        if (!audio.CleanNoise && !audio.Transcribe && !audio.Summarize)
        {
            problems.Add("No hay ningún paso activado (Ajustes › Audio).");
        }

        string? clean = null, transcripts = null, summaries = null;
        if (audio.CleanNoise && !TryResolve(audio.CleanAudioFolder, out clean)) problems.Add("Falta la carpeta de «Audio procesado».");
        if (audio.Transcribe && !TryResolve(audio.TranscriptFolder, out transcripts)) problems.Add("Falta la carpeta de «Transcripciones».");
        if (audio.Summarize && !TryResolve(audio.SummaryFolder, out summaries)) problems.Add("Falta la carpeta de «Resúmenes».");

        var tools = LocateTools();
        if (tools.Ffmpeg is null) problems.Add("Falta ffmpeg (brew install ffmpeg).");
        if (audio.CleanNoise && tools.DeepFilter is null) problems.Add("Falta deep-filter (tools/install-mac-prerequisites.sh).");
        if (needTranscript && !tools.ModelExists) problems.Add($"Falta el modelo de Whisper: {tools.ModelPath}");

        ILlmClient? llm = null;
        string? model = null;
        if (audio.Summarize)
        {
            (llm, model) = LlmSetup.Create(app.Ai, _services.Secrets, problems);
        }

        if (problems.Count > 0)
        {
            return null;
        }

        return new AudioJobOptions(
            "", MacServices.TempDirectory, tools, audio.CleanNoise, audio.Transcribe, audio.Summarize,
            clean, transcripts, summaries, audio.Language, llm, model, app.Ai.Temperature, audio.SummaryPrompt,
            _services.ResolveCacheFolder(), audio.CacheCleanAudio);
    }

    private AudioTools LocateTools()
    {
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(Audio.ToolsFolder) && _services.Config.Paths.TryResolve(Audio.ToolsFolder, out var resolved))
        {
            folder = resolved;
        }

        return AudioTools.Locate(folder, Audio.WhisperModelFile);
    }

    private string BuildToolsWarning()
    {
        var tools = LocateTools();
        var missing = new List<string>();
        if (tools.Ffmpeg is null) missing.Add("ffmpeg");
        if (Audio.CleanNoise && tools.DeepFilter is null) missing.Add("deep-filter");
        if ((Audio.Transcribe || Audio.Summarize) && !tools.ModelExists) missing.Add("modelo de Whisper");
        return missing.Count == 0 ? "" : "Faltan herramientas: " + string.Join(", ", missing) + ". Instálalas con tools/install-mac-prerequisites.sh y brew install ffmpeg.";
    }

    private bool TryResolve(string? configured, out string path) => _services.Config.Paths.TryResolve(configured, out path);

    // The pipeline reports from worker threads; the log box belongs to the UI thread.
    private void Log(string line)
    {
        AppLog.Info("[audio] " + line);
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line + "\n";
            LogBox.CaretIndex = LogBox.Text.Length;
        });
    }
}
