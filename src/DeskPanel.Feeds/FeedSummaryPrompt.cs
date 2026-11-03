using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskPanel.Feeds;

/// <summary>The prompt that summarises a feed entry, and the plain-text conversion of its HTML.</summary>
public static class FeedSummaryPrompt
{
    /// <summary>
    /// Used while the user has not customised it. Placeholders: {texto} (the article), {titulo}, {url} (original page),
    /// {fuente} (feed name) and {idioma}.
    /// </summary>
    public const string Default = """
        Resume el siguiente artículo en {idioma}.

        Título: {titulo}
        Fuente: {fuente}
        URL original: {url}

        Responde ÚNICAMENTE con Markdown y esta estructura exacta:

        # <Título del artículo>

        **URL:** {url}

        > <Una sola frase con el resumen general del artículo.>

        ## Resumen detallado
        <Resumen detallado en varios párrafos: los argumentos, datos, nombres y conclusiones, siguiendo el orden del artículo.>

        Reglas: no inventes nada que no esté en el texto, no omitas ideas importantes y no añadas texto antes ni después del Markdown.

        TEXTO:
        {texto}
        """;

    /// <summary>Fills the template. {texto} goes last so braces inside the article are never taken for placeholders.</summary>
    public static string Render(string? template, FeedEntry entry, string text, string language)
    {
        var prompt = string.IsNullOrWhiteSpace(template) ? Default : template;

        // A template that forgot {texto} would send the model instructions with nothing to summarise.
        if (!prompt.Contains("{texto}", StringComparison.Ordinal))
        {
            prompt += "\n\nTEXTO:\n{texto}";
        }

        // One pass: a title or an article that itself contains "{texto}" must not be taken for a placeholder.
        return Placeholder.Replace(prompt, m => m.Groups[1].Value switch
        {
            "titulo" => entry.Title,
            "url" => entry.Link ?? "",
            "fuente" => entry.SourceName,
            "idioma" => language,
            _ => text,
        });
    }

    private static readonly Regex Placeholder = new(@"\{(texto|titulo|url|fuente|idioma)\}", RegexOptions.Compiled);

    private static readonly Regex Dropped = new(@"<(script|style|noscript)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BlockEnd = new(@"</(p|div|h[1-6]|tr|blockquote|section|article|ul|ol|table|figure)\s*>|<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Blanks = new(@"[ \t ]+", RegexOptions.Compiled);
    private static readonly Regex ManyNewlines = new(@"\n\s*\n\s*\n+", RegexOptions.Compiled);

    private static readonly Regex Url = new(@"https?://[^\s<>""]+", RegexOptions.Compiled);

    /// <summary>The first web address in a text (feed PDFs print the article's URL in their header), or null.</summary>
    public static string? FindUrl(string text)
    {
        var match = Url.Match(text);
        return match.Success ? match.Value.TrimEnd('.', ',', ')', ']') : null;
    }

    /// <summary>Readable text of an HTML fragment: paragraphs and list items on their own lines, no markup.</summary>
    public static string ToPlainText(string html)
    {
        var text = Dropped.Replace(html, " ");
        text = ListItem.Replace(text, "\n- ");
        text = BlockEnd.Replace(text, "\n\n");
        text = Tags.Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = Blanks.Replace(text, " ");
        text = string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => l.Trim()));
        return ManyNewlines.Replace(text, "\n\n").Trim();
    }

    /// <summary>The summary plus a small YAML header (title, URL, date, model) and a provenance footer, ready to save as .md.</summary>
    public static string BuildDocument(string summaryMarkdown, FeedEntry entry, string providerName, string model)
    {
        var markdown = StripCodeFence(summaryMarkdown);

        // The prompt asks for the URL, but a small model may drop it: it must always be in the saved note.
        if (!string.IsNullOrEmpty(entry.Link) && !markdown.Contains(entry.Link, StringComparison.Ordinal))
        {
            var firstLineEnd = markdown.IndexOf('\n');
            var line = $"\n\n**URL:** {entry.Link}";
            markdown = firstLineEnd < 0 ? markdown + line : markdown.Insert(firstLineEnd, line);
        }

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"titulo: \"{entry.Title.Replace("\"", "'")}\"");
        if (!string.IsNullOrEmpty(entry.Link))
        {
            builder.AppendLine($"url: {entry.Link}");
        }

        builder.AppendLine($"fuente: {entry.SourceName}");
        if (entry.Published is { } published)
        {
            builder.AppendLine($"publicado: {published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine($"generado: {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"modelo: {providerName} / {model}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(markdown);
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine($"<small><em>Resumen generado automáticamente por {providerName} ({model}). Puede contener errores.</em></small>");
        return builder.ToString();
    }

    // Models like to wrap the answer in a code fence.
    private static string StripCodeFence(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            cleaned = firstNewline >= 0 ? cleaned[(firstNewline + 1)..] : cleaned;
            var fence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            cleaned = fence >= 0 ? cleaned[..fence] : cleaned;
        }

        return cleaned.Trim();
    }
}
