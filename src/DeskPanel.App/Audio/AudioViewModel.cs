using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.App.Infrastructure;
using DeskPanel.App.Settings;
using DeskPanel.Audio;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using Microsoft.VisualBasic.FileIO;

namespace DeskPanel.App.Audio;

internal sealed partial class AudioFileItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public AudioFileItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        var info = new FileInfo(path);
        Size = info.Length;
        ModifiedUtc = info.LastWriteTimeUtc;
        SizeText = info.Length >= 1 << 20 ? $"{info.Length / 1048576.0:0.#} MB" : $"{info.Length / 1024.0:0} KB";
        ModifiedText = info.LastWriteTime.ToString("dd/MM/yyyy HH:mm");
    }

    public string Path { get; }
    public string Name { get; }
    public long Size { get; }
    public DateTime ModifiedUtc { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }
}

internal sealed record AudioRunResult(string Name, bool Ok, string Message);

internal sealed partial class AudioViewModel : ObservableObject
{
    private readonly Shell _shell;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProcessCommand), nameof(CancelCommand), nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _stepText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _toolsWarning = "";

    public AudioViewModel(Shell shell)
    {
        _shell = shell;
        Refresh();
    }

    public string BuildWarning() => BuildToolsWarning();

    public event Action<string>? LogLine;

    public ObservableCollection<AudioFileItem> Files { get; } = new();

    private AudioConfig Audio => _shell.Config.App.Audio;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        var selected = Files.Where(f => !f.IsSelected).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Files.Clear();

        if (!TryResolve(Audio.InputFolder, out var input))
        {
            Status = "Configura la carpeta de audios sin procesar (Configurar › Audio).";
        }
        else if (!Directory.Exists(input))
        {
            Status = $"La carpeta de entrada no existe: {input}";
        }
        else
        {
            foreach (var path in Directory.EnumerateFiles(input).Where(AudioFileTypes.IsAudio)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Files.Add(new AudioFileItem(path) { IsSelected = !selected.Contains(path) });
            }

            Status = Files.Count == 0 ? "No hay audios en la carpeta de entrada." : $"{Files.Count} audio(s) pendientes.";
        }

        ToolsWarning = BuildToolsWarning();
    }

    private bool CanRefresh() => !IsBusy;

    /// <summary>Copies dropped audio files into the input folder and leaves just those ticked.</summary>
    public async Task ImportAsync(IEnumerable<string> files)
    {
        if (IsBusy)
        {
            Status = "Espera a que termine el proceso en curso para añadir más audios.";
            return;
        }

        Status = "Copiando a la carpeta de entrada…";
        var imported = await AudioImporter.ImportAsync(_shell, files);
        Refresh();
        if (imported.Count > 0)
        {
            SelectOnly(imported);
            Status = $"{imported.Count} audio(s) añadidos. Pulsa «Procesar seleccionados».";
        }
    }

    /// <summary>After a drop: only the dropped files stay ticked, so leftovers from earlier runs are not reprocessed by surprise.</summary>
    public void SelectOnly(IEnumerable<string> paths)
    {
        var wanted = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            file.IsSelected = wanted.Contains(Path.GetFullPath(file.Path));
        }
    }

    [RelayCommand]
    private void OpenInputFolder()
    {
        if (TryResolve(Audio.InputFolder, out var input))
        {
            _shell.OpenFolder(input);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _shell.OpenSettings();

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        Log("Cancelando…");
        _cancellation?.Cancel();
    }

    /// <summary>Called when the window closes mid-run.</summary>
    public void CancelIfRunning() => _cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanProcess))]
    private async Task ProcessAsync()
    {
        var selected = Files.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Status = "Marca al menos un audio.";
            return;
        }

        var problem = Begin(selected, out var run);
        if (problem is not null)
        {
            MessageBox.Show(problem, "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await run;
    }

    /// <summary>One entry per audio of the current or last run, for the web screen.</summary>
    public IReadOnlyList<AudioRunResult> Results => _results;

    private readonly List<AudioRunResult> _results = new();
    private readonly Queue<string> _logTail = new();

    /// <summary>The last log lines; read from any thread.</summary>
    public string[] SnapshotLog()
    {
        lock (_logTail)
        {
            return _logTail.ToArray();
        }
    }

    /// <summary>
    /// Validates and starts a run over the given audios. Returns the reason when it cannot start (nothing is changed
    /// then); otherwise returns null and <paramref name="run"/> completes when the whole run has finished.
    /// </summary>
    public string? Begin(List<AudioFileItem> selected, out Task run)
    {
        run = Task.CompletedTask;
        if (IsBusy)
        {
            return "Ya hay un proceso de audio en marcha.";
        }

        var problems = new List<string>();
        var plan = BuildPlan(problems);
        if (plan is null)
        {
            return "No se puede empezar:\n\n• " + string.Join("\n• ", problems);
        }

        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        StepText = "Empezando…";
        ProgressValue = 0;
        _results.Clear();
        lock (_logTail)
        {
            _logTail.Clear();
        }

        run = RunPlanAsync(plan, selected);
        return null;
    }

    private async Task RunPlanAsync(AudioJobOptions plan, List<AudioFileItem> selected)
    {
        _cancellation ??= new CancellationTokenSource();
        int done = 0, failed = 0, index = 0;
        try
        {
            foreach (var file in selected)
            {
                index++;
                _cancellation.Token.ThrowIfCancellationRequested();
                Log("");
                Log($"━━ [{index}/{selected.Count}] {file.Name}");

                var current = index;
                var progress = new Progress<AudioProgress>(p =>
                {
                    StepText = $"{p.Step} — audio {current}/{selected.Count}";
                    var perFile = p.Fraction is { } f ? ((p.StepNumber - 1) + f) / p.StepCount : (p.StepNumber - 1.0) / p.StepCount;
                    ProgressIndeterminate = false;
                    ProgressValue = ((current - 1) + Math.Clamp(perFile, 0, 1)) / selected.Count * 100;
                });

                var path = file.Path;
                try
                {
                    // Looked up per file, right before it starts: the previous one of this run is already in the folder.
                    path = await Task.Run(() => AudioNumbering.EnsureNumbered(file.Path, plan.CleanAudioFolder, Log));
                    var job = plan with { InputPath = path };
                    var result = await AudioPipeline.RunAsync(job, progress, Log, _cancellation.Token);
                    done++;
                    Log($"✔ Terminado ({result.Outputs.Count} fichero(s) generados).");
                    _results.Add(new AudioRunResult(Path.GetFileName(path), true, $"Terminado ({result.Outputs.Count} fichero(s) generados)."));
                    DeleteOriginal(path);
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
                    _results.Add(new AudioRunResult(Path.GetFileName(path), false, ex.Message));
                }
            }

            Status = failed == 0 ? $"Procesados {done} audio(s) sin errores." : $"{done} correcto(s), {failed} con error (se han conservado).";
            ProgressValue = 100;
            StepText = "Terminado";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelado. El audio en curso se conserva en la carpeta de entrada.";
            StepText = "Cancelado";
            Log("Cancelado por el usuario.");
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            IsBusy = false;
            Refresh();
        }
    }

    private bool CanProcess() => !IsBusy;

    // The input is only removed after every enabled stage succeeded, and goes to the Recycle Bin so a bad result
    // (or a wrong click on a synced folder) is recoverable.
    private void DeleteOriginal(string path)
    {
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            Log("  Audio original enviado a la Papelera de reciclaje.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log($"  ⚠ No se pudo borrar el audio original: {ex.Message}");
        }
    }

    /// <summary>Validates the configuration and builds the per-file job template; null (with reasons) if something is missing.</summary>
    private AudioJobOptions? BuildPlan(List<string> problems)
    {
        var app = _shell.Config.App;
        var audio = app.Audio;
        var needTranscript = audio.Transcribe || audio.Summarize;

        if (!audio.CleanNoise && !audio.Transcribe && !audio.Summarize)
        {
            problems.Add("No hay ningún paso activado (Configurar › Audio › Pasos del procesado).");
        }

        string? clean = null, transcripts = null, summaries = null;
        if (audio.CleanNoise && !TryResolve(audio.CleanAudioFolder, out clean)) problems.Add("Falta la carpeta de «Audio procesado».");
        if (audio.Transcribe && !TryResolve(audio.TranscriptFolder, out transcripts)) problems.Add("Falta la carpeta de «Transcripciones».");
        if (audio.Summarize && !TryResolve(audio.SummaryFolder, out summaries)) problems.Add("Falta la carpeta de «Resúmenes».");

        var tools = LocateTools();
        if (tools.Ffmpeg is null) problems.Add("Falta ffmpeg (Configurar › Audio › Descargar herramientas).");
        if (audio.CleanNoise && tools.DeepFilter is null) problems.Add("Falta deep-filter (Configurar › Audio › Descargar herramientas).");
        if (needTranscript && !tools.ModelExists) problems.Add($"Falta el modelo de Whisper: {tools.ModelPath}");

        ILlmClient? llm = null;
        string? model = null;
        if (audio.Summarize)
        {
            (llm, model) = CreateLlm(problems);
        }

        if (problems.Count > 0)
        {
            return null;
        }

        return new AudioJobOptions(
            "", Shell.TempDirectory, tools, audio.CleanNoise, audio.Transcribe, audio.Summarize,
            clean, transcripts, summaries, audio.Language, llm, model, app.Ai.Temperature, audio.SummaryPrompt,
            _shell.ResolveCacheFolder());
    }

    private (ILlmClient? Client, string? Model) CreateLlm(List<string> problems) =>
        LlmSetup.Create(_shell.Config.App.Ai, problems);

    private AudioTools LocateTools()
    {
        var audio = Audio;
        string? folder = null;
        if (!string.IsNullOrWhiteSpace(audio.ToolsFolder) && _shell.Config.Paths.TryResolve(audio.ToolsFolder, out var resolved))
        {
            folder = resolved;
        }

        return AudioTools.Locate(folder, audio.WhisperModelFile);
    }

    private string BuildToolsWarning()
    {
        var tools = LocateTools();
        var audio = Audio;
        var missing = new List<string>();
        if (tools.Ffmpeg is null) missing.Add("ffmpeg");
        if (audio.CleanNoise && tools.DeepFilter is null) missing.Add("deep-filter");
        if ((audio.Transcribe || audio.Summarize) && !tools.ModelExists) missing.Add("modelo de Whisper");
        return missing.Count == 0 ? "" : "Faltan herramientas: " + string.Join(", ", missing) + ". Descárgalas en Configurar › Audio.";
    }

    private bool TryResolve(string? configured, out string path) =>
        _shell.Config.Paths.TryResolve(configured, out path);

    private void Log(string line)
    {
        AppLog.Info("[audio] " + line);
        lock (_logTail)
        {
            _logTail.Enqueue(line);
            while (_logTail.Count > 300)
            {
                _logTail.Dequeue();
            }
        }

        LogLine?.Invoke(line);
    }
}
