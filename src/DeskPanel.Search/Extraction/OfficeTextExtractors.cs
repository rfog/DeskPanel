using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DeskPanel.Search.Extraction;

internal static class ZipParts
{
    // Decompressed size of one XML part; real documents are far below this, bombs are not.
    public const long MaxPartBytes = 100L * 1024 * 1024;

    public static XmlReader OpenXml(ZipArchiveEntry entry)
    {
        var bytes = TextDecoding.ReadLimited(entry.Open(), MaxPartBytes);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CloseInput = true };
        return XmlReader.Create(new MemoryStream(bytes), settings);
    }
}

/// <summary>Word, Excel and PowerPoint (Office Open XML): text lives in &lt;t&gt; elements of a few known parts.</summary>
public sealed partial class OpenXmlTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> Extensions { get; } = new[]
    {
        ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xltx", ".pptx", ".pptm", ".potx", ".ppsx",
    };

    public string Extract(Stream stream, string fileName)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var builder = new StringBuilder();

        foreach (var entry in zip.Entries.Where(e => TextPart().IsMatch(e.FullName)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var reader = ZipParts.OpenXml(entry);
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && !reader.IsEmptyElement)
                {
                    // ReadElementContentAsString already advances past the element, so do not Read() again here.
                    builder.Append(reader.ReadElementContentAsString()).Append(' ');
                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "si" or "row")
                {
                    builder.Append('\n');
                }

                reader.Read();
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^(word/(document|footnotes|endnotes|comments|header\d*|footer\d*)\.xml|xl/sharedStrings\.xml|xl/worksheets/[^/]+\.xml|ppt/(slides/slide|notesSlides/notesSlide)\d+\.xml)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TextPart();
}

/// <summary>OpenDocument (LibreOffice): everything is in content.xml as plain text nodes.</summary>
public sealed class OpenDocumentTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".odt", ".ods", ".odp", ".ott", ".ots", ".otp" };

    public string Extract(Stream stream, string fileName)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("content.xml");
        if (entry is null)
        {
            return "";
        }

        var builder = new StringBuilder();
        using var reader = ZipParts.OpenXml(entry);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace:
                    builder.Append(reader.Value);
                    break;
                case XmlNodeType.Element when reader.LocalName is "s" or "tab":
                    builder.Append(' ');
                    break;
                case XmlNodeType.Element when reader.LocalName == "line-break":
                case XmlNodeType.EndElement when reader.LocalName is "p" or "h":
                    builder.Append('\n');
                    break;
            }
        }

        return builder.ToString();
    }
}

public sealed class EpubTextExtractor : ITextExtractor
{
    private const long MaxChapterBytes = 20L * 1024 * 1024;

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".epub" };

    public string Extract(Stream stream, string fileName)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var builder = new StringBuilder();
        foreach (var entry in zip.Entries
                     .Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                                 || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                 || e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            using var part = entry.Open();
            builder.AppendLine(HtmlTextExtractor.StripTags(TextDecoding.DecodeSmart(TextDecoding.ReadLimited(part, MaxChapterBytes))));
        }

        return builder.ToString();
    }
}
