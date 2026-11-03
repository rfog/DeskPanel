using System.IO.Compression;
using System.Text;

namespace DeskPanel.Search.Extraction;

/// <summary>
/// Indexes what is inside a zip by running each entry through the extractor for its own extension, so a zip of
/// PDFs or a zip containing a .docx is searchable. Entry paths are included as text so files can be found by name.
/// </summary>
public sealed class ZipTextExtractor : ITextExtractor
{
    // These bound the damage of a zip bomb: entries are size-checked while being read, not from the header.
    private const long MaxEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 10L * 1024 * 1024 * 1024;
    private const int MaxTextChars = 50_000_000;
    private const int MaxNestingDepth = 2;

    [ThreadStatic]
    private static int _depth;

    private readonly Func<ExtractorRegistry> _registry;

    public ZipTextExtractor(Func<ExtractorRegistry> registry) => _registry = registry;

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".zip" };

    public string Extract(Stream stream, string fileName)
    {
        if (_depth >= MaxNestingDepth)
        {
            return "";
        }

        _depth++;
        try
        {
            return ExtractEntries(stream);
        }
        finally
        {
            _depth--;
        }
    }

    private string ExtractEntries(Stream stream)
    {
        var registry = _registry();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var builder = new StringBuilder();
        long totalBytes = 0;

        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0)
            {
                continue; // directory entry
            }

            builder.Append('\n').Append(entry.FullName).Append('\n');

            var extension = Path.GetExtension(entry.Name);
            if (!registry.TryGet(extension, out var extractor)
                || entry.Length > MaxEntryBytes
                || totalBytes + entry.Length > MaxTotalBytes
                || builder.Length > MaxTextChars)
            {
                continue;
            }

            try
            {
                // Spilled to disk rather than held in memory: a zip of scanned PDFs easily has entries of hundreds of MB.
                using var entryStream = entry.Open();
                using var buffer = new FileStream(
                    Path.Combine(Path.GetTempPath(), $"deskpanel-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
                TextDecoding.CopyLimited(entryStream, buffer, MaxEntryBytes);
                buffer.Position = 0;
                totalBytes += buffer.Length;
                builder.Append(extractor.Extract(buffer, entry.Name));
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException
                                           or IOException or System.Xml.XmlException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
            {
                // Encrypted or damaged entries must not make the whole archive unsearchable; its name is still indexed.
            }
        }

        return builder.ToString();
    }
}
