using System.Globalization;
using System.Text;
using DeskPanel.Audio.Ai;

namespace DeskPanel.Audio;

/// <summary>
/// Turns a transcript into a Markdown summary. Transcripts longer than the model's input budget are summarised in
/// pieces first and the notes are then merged (map/reduce), so a long podcast works with a small local model too.
/// </summary>
public static class SummaryService
{
    // Headroom for the instructions and for the model's answer inside the same request.
    private const int PromptOverheadChars = 3_000;

    /// <summary>
    /// The prompt used when the user has not customised it. Placeholders: {idioma} (language name), {origen} (whether the
    /// text is a transcript or the notes of a long one) and {texto} (the material to summarise).
    /// </summary>
    public const string DefaultPrompt = """
        A continuación tienes {origen}.
        Escribe un resumen en {idioma}.

        Responde ÚNICAMENTE con Markdown y esta estructura exacta:

        # <Título breve y descriptivo>

        **Palabras clave:** <exactamente cinco palabras clave separadas por comas>

        ## Resumen
        <Un párrafo de 5 a 8 líneas con la idea general.>

        ## Puntos clave
        - <Las ideas, decisiones, datos o conclusiones importantes, una por viñeta.>

        ## Temas tratados
        - <Cada tema o anécdota que aparece, con una frase.>

        Reglas: no omitas ideas importantes, no inventes nada y no añadas texto antes ni después del Markdown.

        TEXTO:
        {texto}
        """;

    /// <param name="promptTemplate">User's own prompt, or null/blank for <see cref="DefaultPrompt"/>.</param>
    public static async Task<string> SummarizeAsync(
        ILlmClient client, string model, double temperature, string transcript, string language, string? promptTemplate,
        Action<string> log, CancellationToken ct)
    {
        var languageName = language.Equals("es", StringComparison.OrdinalIgnoreCase) ? "español" : language;
        var system = $"Eres un redactor experto que resume contenido hablado en {languageName}. " +
                     "Eres fiel al texto: no inventas datos, nombres ni cifras que no estén en él.";

        // A long custom prompt takes room away from the text it is applied to.
        var budget = Math.Max(4_000, client.MaxInputChars - PromptOverheadChars - (promptTemplate?.Length ?? 0));

        var material = transcript.Trim();
        var round = 0;
        while (material.Length > budget)
        {
            round++;
            var chunks = SplitIntoChunks(material, budget);
            log($"La transcripción es larga: se resume en {chunks.Count} partes (ronda {round}).");
            var notes = new StringBuilder();
            for (var i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                log($"  Parte {i + 1}/{chunks.Count}…");
                var part = await client.CompleteAsync(system, NotesPrompt(chunks[i], languageName, i + 1, chunks.Count), model, temperature, ct);
                notes.AppendLine(part).AppendLine();
            }

            if (notes.Length >= material.Length)
            {
                throw new LlmException("El modelo no condensa el texto (las notas son tan largas como el original).");
            }

            material = notes.ToString().Trim();
        }

        log("Generando el resumen final…");
        var markdown = await client.CompleteAsync(system, RenderPrompt(promptTemplate, material, languageName, round > 0), model, temperature, ct);
        return CleanMarkdown(markdown);
    }

    /// <summary>Fills a prompt template. Exposed so the settings screen and tests can check exactly what is sent.</summary>
    public static string RenderPrompt(string? template, string material, string languageName, bool fromNotes)
    {
        var prompt = string.IsNullOrWhiteSpace(template) ? DefaultPrompt : template;

        // A template that forgot {texto} would send the model instructions with nothing to summarise.
        if (!prompt.Contains("{texto}", StringComparison.Ordinal))
        {
            prompt += "\n\nTEXTO:\n{texto}";
        }

        var origin = fromNotes
            ? "las notas tomadas de cada parte de una grabación de audio"
            : "la transcripción de una grabación de audio";

        // {texto} goes last so braces inside the transcript itself are never taken for placeholders.
        return prompt.Replace("{origen}", origin, StringComparison.Ordinal)
            .Replace("{idioma}", languageName, StringComparison.Ordinal)
            .Replace("{texto}", material, StringComparison.Ordinal);
    }

    /// <summary>The summary plus a small header and the provenance footer, ready to save as .md.</summary>
    public static string BuildDocument(string summaryMarkdown, string baseName, string providerName, string model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"audio: {baseName}");
        builder.AppendLine($"generado: {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"modelo: {providerName} / {model}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(summaryMarkdown.Trim());
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine($"<small><em>Resumen generado automáticamente por {providerName} ({model}) a partir de la transcripción del audio. Puede contener errores.</em></small>");
        return builder.ToString();
    }

    // The map step of long transcripts is an internal detail and is not user-editable.
    private static string NotesPrompt(string chunk, string languageName, int index, int total) =>
        $"""
         Esto es la parte {index} de {total} de la transcripción de un audio.
         Toma notas en {languageName}: una lista con viñetas de TODAS las ideas, datos, nombres, decisiones y anécdotas
         de esta parte, sin omitir nada relevante y sin inventar. Responde solo con la lista.

         TEXTO:
         {chunk}
         """;

    // Models like to wrap the answer in a code fence or add a preamble; keep only the Markdown from the title on.
    private static string CleanMarkdown(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            cleaned = firstNewline >= 0 ? cleaned[(firstNewline + 1)..] : cleaned;
            var fence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            cleaned = fence >= 0 ? cleaned[..fence] : cleaned;
        }

        var title = cleaned.IndexOf("\n# ", StringComparison.Ordinal);
        if (!cleaned.StartsWith("# ") && title >= 0)
        {
            cleaned = cleaned[(title + 1)..];
        }

        return cleaned.Trim();
    }

    // Splits on paragraph breaks so no sentence is cut in the middle where possible.
    private static List<string> SplitIntoChunks(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = paragraph;
            while (remaining.Length > maxChars)
            {
                Flush();
                var cut = remaining.LastIndexOf(' ', maxChars - 1);
                cut = cut <= 0 ? maxChars : cut;
                chunks.Add(remaining[..cut]);
                remaining = remaining[cut..].TrimStart();
            }

            if (current.Length + remaining.Length + 2 > maxChars)
            {
                Flush();
            }

            current.Append(remaining).Append("\n\n");
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
        }
    }
}
