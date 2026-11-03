namespace DeskPanel.Search.Extraction;

public interface ITextExtractor
{
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>
    /// Extracts text from a seekable stream. Working on streams rather than paths is what lets the same
    /// extractors read documents nested inside a zip.
    /// </summary>
    string Extract(Stream stream, string fileName);

    string Extract(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Extract(stream, Path.GetFileName(path));
    }
}
