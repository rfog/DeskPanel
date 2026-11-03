using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskPanel.Core.Configuration;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.Feeds;

/// <param name="Entry">The entry with its title and text in Spanish and the closing note; the images and markup are the original's.</param>
/// <param name="Language">The detected original language, in Spanish ("inglés").</param>
/// <param name="Untranslated">Segments the model could not translate reliably; they stay in the original language.</param>
public sealed record FeedTranslation(FeedEntry Entry, string Language, string Provider, int Untranslated);

/// <param name="Result">Null when nothing was translated (then <paramref name="Status"/> says why).</param>
public sealed record FeedTranslationOutcome(FeedTranslation? Result, string Status);

/// <summary>
/// Translates a feed entry to Spanish with the model chosen in Settings › IA. Only the text nodes of the HTML go to the
/// model (as a JSON array, in order); the answer is put back node by node, so images, links and layout are untouched.
/// </summary>
public static class FeedTranslator
{
    // Room left in a request for the instructions; the answer needs about as much room as the question.
    private const int PromptOverheadChars = 3_000;
    private const int MaxSegmentsPerRequest = 80;

    public static async Task<FeedTranslationOutcome> TranslateAsync(AppConfig config, ISecretStore secrets, FeedEntry entry, CancellationToken ct, Action<string>? progress = null)
    {
        var problems = new List<string>();
        var (client, model) = LlmSetup.Create(config.Ai, secrets, problems);
        if (client is null || problems.Count > 0)
        {
            return new FeedTranslationOutcome(null, string.Join(" ", problems));
        }

        return await TranslateAsync(client, model!, config.Feeds.TranslationPrompt, config.Ai.Temperature, entry, ct, progress);
    }

    /// <summary>The translation itself, with the model passed in (so it can be exercised without the user's configuration).</summary>
    internal static async Task<FeedTranslationOutcome> TranslateAsync(
        ILlmClient client, string model, string? template, double configuredTemperature, FeedEntry entry, CancellationToken ct, Action<string>? progress = null)
    {
        try
        {
            var document = HtmlTextSegments.Parse(entry.ContentHtml);
            if (document.Segments.Sum(s => s.Length) < 80)
            {
                return new FeedTranslationOutcome(null, "Hay muy poco texto para traducir. Descarga antes el artículo completo.");
            }

            // The title travels as the first segment, so it is translated in the same context as the text.
            var all = new List<string> { entry.Title };
            all.AddRange(document.Segments);

            progress?.Invoke("Detectando el idioma…");
            var language = await DetectLanguageAsync(client, model, all, ct);
            if (FeedTranslationPrompt.IsSpanish(language))
            {
                return new FeedTranslationOutcome(null, "El artículo ya está en español.");
            }

            var budget = Math.Clamp((client.MaxInputChars - PromptOverheadChars - (template?.Length ?? FeedTranslationPrompt.Default.Length)) / 2, 1_500, 8_000);
            var temperature = Math.Min(configuredTemperature, 0.3);

            var translated = new string?[all.Count];
            var untranslated = 0;
            var done = 0;

            async Task TranslateRangeAsync(int start, int count)
            {
                ct.ThrowIfCancellationRequested();
                var slice = all.GetRange(start, count);
                var answer = await client.CompleteAsync(FeedTranslationPrompt.SystemMessage,
                    FeedTranslationPrompt.Render(template, language, slice), model, temperature, ct);

                // The count is the contract: one answer per segment, in order. Anything else cannot be put back safely.
                if (FeedTranslationPrompt.ParseAnswer(answer) is { } parsed && parsed.Length == count)
                {
                    Array.Copy(parsed, 0, translated, start, count);
                    done += count;
                    progress?.Invoke($"Traduciendo del {language}… {done} de {all.Count} fragmentos");
                    return;
                }

                if (count == 1)
                {
                    untranslated++;
                    done++;
                    AppLog.Warn($"Translation: segment {start} could not be translated; kept as is.");
                    return;
                }

                // Ask again in smaller pieces: a model that loses count on 60 items usually keeps it on 30.
                var half = count / 2;
                await TranslateRangeAsync(start, half);
                await TranslateRangeAsync(start + half, count - half);
            }

            foreach (var (start, count) in Pack(all, budget))
            {
                await TranslateRangeAsync(start, count);
            }

            if (untranslated == all.Count)
            {
                return new FeedTranslationOutcome(null, "El modelo no devolvió una traducción utilizable. Prueba con otro modelo o ajusta el prompt.");
            }

            for (var i = 1; i < all.Count; i++)
            {
                if (translated[i] is { Length: > 0 } text)
                {
                    document.Replace(i - 1, text);
                }
            }

            var provider = $"{client.DisplayName} ({model})";
            var title = translated[0] is { Length: > 0 } t ? t.Replace('\n', ' ').Trim() : entry.Title;
            var result = entry with { Title = title, ContentHtml = document.ToHtml() + FeedTranslationPrompt.BuildNote(language, provider) };
            var status = $"Traducido del {language} con {provider}."
                         + (untranslated > 0 ? $" {untranslated} fragmento(s) se han quedado sin traducir." : "");
            return new FeedTranslationOutcome(new FeedTranslation(result, language, provider, untranslated), status);
        }
        catch (LlmException ex)
        {
            AppLog.Warn("Feed translation failed.", ex);
            return new FeedTranslationOutcome(null, $"Error de la IA: {ex.Message}");
        }
    }

    private static async Task<string> DetectLanguageAsync(ILlmClient client, string model, List<string> segments, CancellationToken ct)
    {
        var sample = string.Join(" ", segments);

        // Counting common words beats asking: a model tuned for translation may not answer a plain question at all.
        if (LanguageGuess.Detect(sample.Length > 4_000 ? sample[..4_000] : sample) is { } guessed)
        {
            return guessed;
        }

        sample = sample.Length > 800 ? sample[..800] : sample;
        var answer = await client.CompleteAsync(
            "Eres un detector de idiomas. Respondes solo con el nombre del idioma.",
            "Indica en qué idioma está escrito el siguiente texto. Responde únicamente con el nombre del idioma en español y en minúsculas " +
            "(por ejemplo: inglés, francés, alemán), sin nada más.\n\nTEXTO:\n" + sample,
            model, 0, ct);

        var line = answer.Trim().Split('\n')[0].Trim().Trim('.', '"', '\'', ' ', '«', '»');
        if (line.Length is 0 or > 30)
        {
            // A chatty answer: the first word is the language more often than not.
            line = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim('.', ',', '"') ?? "idioma original";
        }

        return line.ToLowerInvariant();
    }

    /// <summary>Consecutive runs of segments that fit one request (by characters and by count).</summary>
    private static IEnumerable<(int Start, int Count)> Pack(List<string> segments, int budget)
    {
        var start = 0;
        var size = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var cost = segments[i].Length + 6;
            if (i > start && (size + cost > budget || i - start >= MaxSegmentsPerRequest))
            {
                yield return (start, i - start);
                start = i;
                size = 0;
            }

            size += cost;
        }

        if (start < segments.Count)
        {
            yield return (start, segments.Count - start);
        }
    }
}
