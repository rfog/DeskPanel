using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DeskPanel.App.Feeds;
using DeskPanel.Feeds;

namespace DeskPanel.App.Pdf;

/// <summary>Summarises a PDF from its own text, for when the original web page is no longer there.</summary>
internal static partial class PdfSummary
{
    // "2026-10-03 - Title": the name the feed PDFs are saved with.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} - ")]
    private static partial Regex DatePrefix();

    // The footer printed on every page of a feed PDF.
    [GeneratedRegex(@"^(\d+ de \d+|Creado el .*)$")]
    private static partial Regex PageFooter();

    public static void Open(Shell shell, string pdfPath)
    {
        var title = DatePrefix().Replace(Path.GetFileNameWithoutExtension(pdfPath), "");
        var created = File.GetCreationTime(pdfPath);

        // The original URL is only known once the text is read (feed PDFs print it in their header), so the view model
        // fills it in from the extracted text.
        var window = new FeedSummaryWindow(new FeedSummaryViewModel(shell,
            new FeedEntry(pdfPath, title, null, new DateTimeOffset(created), null, "", "PDF"),
            () => ExtractText(shell, pdfPath)));
        window.Show();
        window.Activate();
    }

    // The extractor often returns a page's text as one long line, so the footer ("Creado el … 1 de 10") also has to go inline.
    [GeneratedRegex(@"Creado el \d{1,2} \p{L}+ \d{4}, \d{1,2}:\d{2}\s+\d+ de \d+")]
    private static partial Regex InlineFooter();

    private static string ExtractText(Shell shell, string pdfPath)
    {
        var text = shell.Extractors.TryGet(".pdf", out var extractor) ? extractor.Extract(pdfPath) : "";

        // Ligatures such as "ﬁ" come out as one character; the model reads plain letters better.
        text = InlineFooter().Replace(text.Normalize(NormalizationForm.FormKC), " ");

        // The header with the article URL is printed on every page: keep the first one only.
        if (FeedSummaryPrompt.FindUrl(text) is { } url)
        {
            var first = text.IndexOf(url, StringComparison.Ordinal) + url.Length;
            text = string.Concat(text.AsSpan(0, first), text[first..].Replace(url, " ", StringComparison.Ordinal));
        }

        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => !PageFooter().IsMatch(l));
        return string.Join("\n", lines).Trim();
    }
}
