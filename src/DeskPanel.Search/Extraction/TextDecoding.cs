using System.Text;

namespace DeskPanel.Search.Extraction;

public static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public const long DefaultMaxBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Decodes a text file as UTF-8 (BOM-aware) and falls back to Latin-1 when the bytes are not valid UTF-8.
    /// Older Spanish notes are frequently ANSI-encoded; decoding them as UTF-8 would silently corrupt accented
    /// characters and make them unsearchable.
    /// </summary>
    public static string ReadAllTextSmart(string path, long maxBytes = DefaultMaxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadStreamSmart(stream, maxBytes);
    }

    public static string ReadStreamSmart(Stream stream, long maxBytes = DefaultMaxBytes) =>
        DecodeSmart(ReadLimited(stream, maxBytes));

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> and throws beyond that. The declared length of a zip entry
    /// cannot be trusted, so the limit is enforced on what is actually read.
    /// </summary>
    public static byte[] ReadLimited(Stream stream, long maxBytes)
    {
        using var buffer = new MemoryStream();
        CopyLimited(stream, buffer, maxBytes);
        return buffer.ToArray();
    }

    public static void CopyLimited(Stream source, Stream destination, long maxBytes)
    {
        var chunk = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Content exceeds {maxBytes / (1024 * 1024)} MB limit.");
            }

            destination.Write(chunk, 0, read);
        }
    }

    public static string DecodeSmart(byte[] bytes)
    {
        var span = bytes.AsSpan();

        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Encoding.UTF8.GetString(span[3..]);
        }

        if (span.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(span[2..]);
        }

        if (span.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(span[2..]);
        }

        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(span);
        }
    }
}
