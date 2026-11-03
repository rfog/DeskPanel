using System.Globalization;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.IO;

namespace DeskPanel.Audio;

public sealed record AudioProgress(string Step, int StepNumber, int StepCount, double? Fraction);

public sealed record AudioJobOptions(
    string InputPath,
    string TempRoot,
    AudioTools Tools,
    bool CleanNoise,
    bool Transcribe,
    bool Summarize,
    string? CleanAudioFolder,
    string? TranscriptFolder,
    string? SummaryFolder,
    string Language,
    ILlmClient? Llm,
    string? LlmModel,
    double Temperature,
    string? SummaryPrompt = null,
    string? CacheFolder = null,
    bool CacheCleanAudio = true);

public sealed record AudioJobResult(IReadOnlyList<string> Outputs);

/// <summary>
/// One audio file through the enabled stages: DeepFilterNet noise removal + mastering (exported as m4a),
/// whisper.cpp transcription, and an LLM summary. Each result is written to its folder as soon as its stage ends.
/// Anything that fails throws, so the caller keeps the input audio.
/// </summary>
public static class AudioPipeline
{
    // Same chain the original scripts applied after DeepFilterNet.
    private const string MasteringFilter =
        "highpass=f=80,lowpass=f=15000,acompressor=threshold=-18dB:ratio=3:attack=5:release=50,loudnorm=I=-16:TP=-1.5:LRA=11";

    public static async Task<AudioJobResult> RunAsync(
        AudioJobOptions job, IProgress<AudioProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var needTranscript = job.Transcribe || job.Summarize;
        var stepCount = (job.CleanNoise ? 1 : 0) + (needTranscript ? 1 : 0) + (job.Summarize ? 1 : 0);
        if (stepCount == 0)
        {
            throw new InvalidOperationException("No hay ningún paso activado en la configuración de audio.");
        }

        var baseName = Path.GetFileNameWithoutExtension(job.InputPath);
        var outputs = new List<string>();
        var step = 0;
        void Report(string name, double? fraction) => progress?.Report(new AudioProgress(name, step, stepCount, fraction));

        var work = Path.Combine(job.TempRoot, "audio-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var ffmpeg = job.Tools.Ffmpeg ?? throw new FileNotFoundException("No se encuentra ffmpeg.");
            var duration = await ProcessRunner.ProbeDurationAsync(ffmpeg, job.InputPath, ct);
            log($"Duración: {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}");

            var audioForAsr = job.InputPath;

            if (job.CleanNoise)
            {
                step++;
                Report("Limpiando ruido", null);
                var cleaned = await DenoiseAsync(job, ffmpeg, work, duration, f => Report("Limpiando ruido", f), log, ct);

                var target = Path.Combine(job.CleanAudioFolder!, baseName + ".m4a");
                Directory.CreateDirectory(job.CleanAudioFolder!);
                AtomicFile.ReplaceWith(cleaned, target);
                outputs.Add(target);
                log($"Audio limpio: {target}");
                if (job.CacheCleanAudio)
                {
                    CacheMirror.Copy(job.CacheFolder, target, overwrite: true, log);
                }

                audioForAsr = target;
            }

            string? transcript = null;
            if (needTranscript)
            {
                step++;
                Report("Transcribiendo", 0);
                transcript = await TranscribeAsync(job, ffmpeg, audioForAsr, work, duration, f => Report("Transcribiendo", f), log, ct);

                if (job.Transcribe)
                {
                    var target = Path.Combine(job.TranscriptFolder!, baseName + ".txt");
                    Directory.CreateDirectory(job.TranscriptFolder!);
                    AtomicFile.WriteAllText(target, transcript);
                    outputs.Add(target);
                    log($"Transcripción: {target} ({transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} palabras)");
                    CacheMirror.Copy(job.CacheFolder, target, overwrite: true, log);
                }
            }

            if (job.Summarize)
            {
                step++;
                Report("Generando resumen", null);
                var llm = job.Llm ?? throw new InvalidOperationException("No hay proveedor de IA configurado.");
                var model = job.LlmModel ?? throw new InvalidOperationException("No hay modelo de IA elegido.");
                log($"Resumen con {llm.DisplayName} / {model}");

                var summary = await SummaryService.SummarizeAsync(llm, model, job.Temperature, transcript!, job.Language, job.SummaryPrompt, log, ct);
                var target = Path.Combine(job.SummaryFolder!, baseName + ".md");
                Directory.CreateDirectory(job.SummaryFolder!);
                AtomicFile.WriteAllText(target, SummaryService.BuildDocument(summary, baseName, llm.DisplayName, model));
                outputs.Add(target);
                log($"Resumen: {target}");
                CacheMirror.Copy(job.CacheFolder, target, overwrite: true, log);
            }

            progress?.Report(new AudioProgress("Terminado", stepCount, stepCount, 1));
            return new AudioJobResult(outputs);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private static async Task<string> DenoiseAsync(
        AudioJobOptions job, string ffmpeg, string work, double duration, Action<double> onFraction, Action<string> log, CancellationToken ct)
    {
        var deepFilter = job.Tools.DeepFilter
                         ?? throw new FileNotFoundException("No se encuentra deep-filter. Ejecuta Install-AudioTools.ps1.");

        // DeepFilterNet works at 48 kHz mono; converting first also makes any input format (m4a, mp3, …) acceptable.
        var wav48 = Path.Combine(work, "in48.wav");
        log("Convirtiendo a WAV 48 kHz mono…");
        await RunFfmpegAsync(ffmpeg, new[] { "-y", "-i", job.InputPath, "-vn", "-ar", "48000", "-ac", "1", "-c:a", "pcm_s16le", wav48 },
            duration, f => onFraction(f * 0.2), log, ct);

        var outDir = Path.Combine(work, "df");
        Directory.CreateDirectory(outDir);
        log("Quitando ruido con DeepFilterNet3…");
        onFraction(0.2);
        var captured = new List<string>();
        var exit = await ProcessRunner.RunAsync(deepFilter, new[] { wav48, "-o", outDir }, line =>
        {
            captured.Add(line);
            log("  " + line);
        }, ct);

        var denoised = Path.Combine(outDir, "in48.wav");
        if (exit != 0 || !File.Exists(denoised))
        {
            throw new InvalidOperationException($"deep-filter falló (código {exit}). {string.Join(" | ", captured.TakeLast(3))}");
        }

        onFraction(0.7);
        var m4a = Path.Combine(work, "final.m4a");
        log("Masterizando y exportando a m4a…");
        await RunFfmpegAsync(ffmpeg,
            new[] { "-y", "-i", denoised, "-af", MasteringFilter, "-ar", "48000", "-c:a", "aac", "-b:a", "128k", m4a },
            duration, f => onFraction(0.7 + f * 0.3), log, ct);
        return m4a;
    }

    private static async Task<string> TranscribeAsync(
        AudioJobOptions job, string ffmpeg, string audio, string work, double duration, Action<double> onFraction,
        Action<string> log, CancellationToken ct)
    {
        if (!job.Tools.ModelExists)
        {
            throw new FileNotFoundException($"No se encuentra el modelo de Whisper: {job.Tools.ModelPath}");
        }

        var wav16 = Path.Combine(work, "asr16.wav");
        log("Preparando audio 16 kHz para Whisper…");
        await RunFfmpegAsync(ffmpeg, new[] { "-y", "-i", audio, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav16 },
            duration, null, log, ct);

        log($"Transcribiendo con {Path.GetFileName(job.Tools.ModelPath)} (idioma {job.Language})…");
        return await WhisperTranscriber.TranscribeAsync(job.Tools.ModelPath, wav16, job.Language, duration, onFraction, log, ct);
    }

    /// <summary>Runs ffmpeg with <c>-progress</c> so long conversions can report a fraction.</summary>
    private static async Task RunFfmpegAsync(
        string ffmpeg, IEnumerable<string> arguments, double duration, Action<double>? onFraction, Action<string> log, CancellationToken ct)
    {
        var tail = new Queue<string>();
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1" };
        args.AddRange(arguments);

        var exit = await ProcessRunner.RunAsync(ffmpeg, args, line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && duration > 0
                && long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            {
                onFraction?.Invoke(Math.Clamp(micros / 1_000_000.0 / duration, 0, 1));
            }
            else if (!line.Contains('=') || line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                // With -progress, status lines are key=value; anything else is a real message.
                tail.Enqueue(line);
                if (tail.Count > 5)
                {
                    tail.Dequeue();
                }
            }
        }, ct);

        if (exit != 0)
        {
            throw new InvalidOperationException($"ffmpeg falló (código {exit}). {string.Join(" | ", tail)}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Left for the startup temp sweep.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
