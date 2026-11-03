using DeskPanel.Core.Configuration;

namespace DeskPanel.Core.Platform;

/// <summary>Turns an HTML document into a PDF laid out according to a <see cref="PdfProfile"/>.</summary>
public interface IPdfRenderer
{
    /// <summary>
    /// Writes the PDF to <paramref name="outputPath"/>. The caller chooses that path, so a caller that fills a synced or
    /// watched folder renders to a temporary file elsewhere and renames it into place afterwards.
    /// </summary>
    /// <param name="headerText">Shown in the page header (the article URL); the footer carries creation date and "n de total".</param>
    Task RenderAsync(string html, string outputPath, PdfProfile profile, string? headerText = null, CancellationToken ct = default);
}
