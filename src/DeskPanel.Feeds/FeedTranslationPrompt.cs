using System.Net;
using System.Text.Json;

namespace DeskPanel.Feeds;

/// <summary>The prompt that translates the text segments of a feed entry, and the helpers around its JSON answer.</summary>
public static class FeedTranslationPrompt
{
    /// <summary>
    /// Used while the user has not customised it. Placeholders: {idioma} (the detected original language), {n} (how many
    /// segments) and {segmentos} (the JSON array to translate). The answer must stay a JSON array of the same length:
    /// that is what lets the translation go back into the page without touching its markup.
    /// </summary>
    public const string Default = """
        Traduce al español los siguientes fragmentos de un artículo. El idioma original es {idioma}.

        Reglas:
        - Responde ÚNICAMENTE con un array JSON de cadenas con exactamente {n} elementos, en el mismo orden: ni uno más ni uno menos, y sin texto fuera del array.
        - Los fragmentos son partes de un mismo texto y varios seguidos pueden formar una sola frase; traduce con coherencia entre ellos.
        - Conserva tal cual los nombres propios, marcas, direcciones web, correos, código, cifras y emojis.
        - Mantén el tono y el registro del original. No resumas, no omitas nada, no expliques ni añadas comentarios.

        FRAGMENTOS:
        {segmentos}
        """;

    public const string SystemMessage =
        "Eres un traductor profesional al español. Eres fiel al original: no resumes, no omites y no añades nada.";

    /// <summary>Fills the template. {segmentos} goes last so braces inside the article are never taken for placeholders.</summary>
    public static string Render(string? template, string language, IReadOnlyList<string> segments)
    {
        var prompt = string.IsNullOrWhiteSpace(template) ? Default : template;

        // A template that forgot {segmentos} would send the instructions with nothing to translate.
        if (!prompt.Contains("{segmentos}", StringComparison.Ordinal))
        {
            prompt += "\n\nFRAGMENTOS:\n{segmentos}";
        }

        var json = JsonSerializer.Serialize(segments, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return prompt
            .Replace("{idioma}", language, StringComparison.Ordinal)
            .Replace("{n}", segments.Count.ToString(), StringComparison.Ordinal)
            .Replace("{segmentos}", json, StringComparison.Ordinal);
    }

    /// <summary>The strings of the first JSON array in a model's answer (it may wrap it in a code fence or add a sentence), or null.</summary>
    public static string[]? ParseAnswer(string answer)
    {
        var start = answer.IndexOf('[');
        var end = answer.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(answer[start..(end + 1)]);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return document.RootElement.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString())
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when a detected language name means Spanish.</summary>
    public static bool IsSpanish(string language)
    {
        var name = language.Trim().ToLowerInvariant();
        return name.StartsWith("español") || name.StartsWith("espanol") || name.StartsWith("castellano") || name.StartsWith("spanish");
    }

    /// <summary>The closing paragraph of a translated document, in the page's own style hook.</summary>
    public static string BuildNote(string language, string provider) =>
        $"<p class=\"dp-translation-note\"><em>Este PDF ha sido traducido del {WebUtility.HtmlEncode(language.Trim().ToLowerInvariant())} con {WebUtility.HtmlEncode(provider)}.</em></p>";
}
