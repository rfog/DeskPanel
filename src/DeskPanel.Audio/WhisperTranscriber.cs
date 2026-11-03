using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace DeskPanel.Audio;

public static class WhisperTranscriber
{
    // A pause longer than this starts a new paragraph (same rule the original Python scripts used).
    private static readonly TimeSpan ParagraphGap = TimeSpan.FromSeconds(2);

    /// <param name="wavPath">16 kHz mono 16-bit PCM WAV: the only input whisper.cpp accepts.</param>
    public static async Task<string> TranscribeAsync(
        string modelPath, string wavPath, string language, double durationSeconds,
        Action<double> onProgress, Action<string> log, CancellationToken ct)
    {
        using var factory = WhisperFactory.FromPath(modelPath);

        // Whisper.net tries CUDA, then Vulkan, then CPU; saying which one won explains a slow run.
        log($"Motor de Whisper: {RuntimeOptions.LoadedLibrary?.ToString() ?? "desconocido"}");

        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
            .Build();

        var builder = new StringBuilder();
        var paragraph = new List<string>();
        TimeSpan? previousEnd = null;
        string? last = null, beforeLast = null;

        await using var stream = File.OpenRead(wavPath);
        await foreach (var segment in processor.ProcessAsync(stream, ct))
        {
            if (durationSeconds > 0)
            {
                onProgress(Math.Min(1.0, segment.End.TotalSeconds / durationSeconds));
            }

            var text = segment.Text.Trim();
            if (!IsUsable(text) || (text == last && text == beforeLast))
            {
                // Silence and music make whisper emit tags like [BLANK_AUDIO] or loop the same line; neither is speech.
                continue;
            }

            beforeLast = last;
            last = text;

            if (previousEnd is not null && segment.Start - previousEnd.Value > ParagraphGap && paragraph.Count > 0)
            {
                builder.AppendLine(string.Join(' ', paragraph)).AppendLine();
                paragraph.Clear();
            }

            paragraph.Add(text);
            previousEnd = segment.End;
        }

        if (paragraph.Count > 0)
        {
            builder.AppendLine(string.Join(' ', paragraph));
        }

        return builder.ToString().Trim();
    }

    private static bool IsUsable(string text) =>
        text.Length > 0
        && !(text.StartsWith('[') && text.EndsWith(']'))
        && !(text.StartsWith('(') && text.EndsWith(')'));
}
