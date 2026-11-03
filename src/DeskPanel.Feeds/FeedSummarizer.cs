using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeskPanel.Core.Configuration;
using DeskPanel.Audio.Ai;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;

namespace DeskPanel.Feeds;

/// <param name="Markdown">The finished document, or null when it could not be made (then <paramref name="Status"/> says why).</param>
/// <param name="Entry">The entry, possibly completed (a PDF's URL is only known once its text has been read).</param>
public sealed record FeedSummaryOutcome(string? Markdown, string Status, FeedEntry Entry);

/// <summary>Summarises a feed entry with the model chosen in Settings › IA. Shared by the summary window and the web.</summary>
public static class FeedSummarizer
{
    // Room left in a request for the instructions and the model's answer.
    private const int PromptOverheadChars = 3_000;

    /// <param name="plainTextSource">Supplies the text directly (a PDF's) instead of converting the entry's HTML; runs on a worker thread.</param>
    public static async Task<FeedSummaryOutcome> GenerateAsync(
        AppConfig config, ISecretStore secrets, FeedEntry entry, Func<string>? plainTextSource, CancellationToken ct, Action<string>? progress = null)
    {
        try
        {
            var problems = new List<string>();
            var (client, model) = LlmSetup.Create(config.Ai, secrets, problems);
            if (client is null || problems.Count > 0)
            {
                return new FeedSummaryOutcome(null, string.Join(" ", problems), entry);
            }

            string text;
            if (plainTextSource is null)
            {
                text = FeedSummaryPrompt.ToPlainText(entry.ContentHtml);
            }
            else
            {
                progress?.Invoke("Extrayendo el texto del PDF…");
                text = await Task.Run(plainTextSource, ct);
                if (string.IsNullOrEmpty(entry.Link) && FeedSummaryPrompt.FindUrl(text) is { } found)
                {
                    entry = entry with { Link = found };
                }
            }

            if (text.Length < 200)
            {
                return new FeedSummaryOutcome(null, plainTextSource is null
                    ? "Hay muy poco texto para resumir. Descarga antes el artículo completo."
                    : "El PDF tiene muy poco texto (¿es un escaneo sin OCR?).", entry);
            }

            var template = config.Feeds.SummaryPrompt;
            var budget = Math.Max(4_000, client.MaxInputChars - PromptOverheadChars - (template?.Length ?? FeedSummaryPrompt.Default.Length));
            var note = "";
            if (text.Length > budget)
            {
                text = text[..budget];
                note = $" El artículo es muy largo: se ha resumido solo su principio ({budget:N0} caracteres).";
            }

            progress?.Invoke($"Generando el resumen con {client.DisplayName} ({model})…");
            var language = config.Audio.Language;
            var languageName = language.Equals("es", StringComparison.OrdinalIgnoreCase) ? "español" : language;
            var system = $"Eres un redactor experto que resume artículos en {languageName}. " +
                         "Eres fiel al texto: no inventas datos, nombres ni cifras que no estén en él.";
            var answer = await client.CompleteAsync(system, FeedSummaryPrompt.Render(template, entry, text, languageName), model!,
                config.Ai.Temperature, ct);

            return new FeedSummaryOutcome(FeedSummaryPrompt.BuildDocument(answer, entry, client.DisplayName, model!), "Resumen listo." + note, entry);
        }
        catch (LlmException ex)
        {
            AppLog.Warn("Feed summary failed.", ex);
            return new FeedSummaryOutcome(null, $"Error de la IA: {ex.Message}", entry);
        }
    }

    /// <summary>Writes the Markdown to the summaries folder and returns the path.</summary>
    /// <exception cref="InvalidOperationException">No summaries folder is configured.</exception>
    public static string Save(FeedsConfig feeds, PathResolver paths, string? cacheFolder, FeedEntry entry, string markdown)
    {
        var configured = feeds.SummaryFolder;
        if (string.IsNullOrWhiteSpace(configured) || !paths.TryResolve(configured, out var folder))
        {
            throw new InvalidOperationException("Configura la carpeta de resúmenes (Configurar › Feeds › Carpeta de resúmenes).");
        }

        Directory.CreateDirectory(folder);
        var name = FileNames.Sanitize(ArticleHtmlBuilder.SuggestFileName(entry)) + ".md";
        var target = FileNames.MakeUnique(folder, name);
        AtomicFile.WriteAllText(target, markdown);

        // Covers entry summaries and PDF summaries alike: both are saved here.
        CacheMirror.Copy(cacheFolder, target, overwrite: false, line => AppLog.Info("[cache] " + line.Trim()));
        return target;
    }
}
