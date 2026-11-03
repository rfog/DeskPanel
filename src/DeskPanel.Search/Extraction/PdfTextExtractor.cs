using System.Text;
using UglyToad.PdfPig;

namespace DeskPanel.Search.Extraction;

public sealed class PdfTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".pdf" };

    public string Extract(Stream stream, string fileName)
    {
        using var document = PdfDocument.Open(stream);
        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            // page.Text concatenates glyphs without separators; word segmentation keeps terms tokenizable.
            foreach (var word in page.GetWords())
            {
                builder.Append(word.Text).Append(' ');
            }

            builder.AppendLine();
        }

        // Scanned PDFs without an OCR layer yield nothing; they are indexed by name only rather than failing.
        return builder.ToString();
    }
}
