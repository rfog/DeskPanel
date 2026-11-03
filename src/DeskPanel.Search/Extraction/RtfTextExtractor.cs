using System.Globalization;
using System.Text;

namespace DeskPanel.Search.Extraction;

/// <summary>
/// Minimal RTF reader: drops control words and non-text destinations (font tables, pictures, ...) and decodes
/// \'xx and \uN escapes. Enough for full-text search; it does not try to preserve layout.
/// </summary>
public sealed class RtfTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "themedata", "datastore",
    };

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".rtf" };

    public string Extract(Stream stream, string fileName)
    {
        var rtf = Encoding.Latin1.GetString(TextDecoding.ReadLimited(stream, TextDecoding.DefaultMaxBytes));
        var builder = new StringBuilder();
        var groups = new Stack<bool>();
        var skip = false;
        var skipFallback = 0;

        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    groups.Push(skip);
                    break;
                case '}':
                    skip = groups.Count > 0 ? groups.Pop() : skip;
                    break;
                case '\r' or '\n':
                    break;
                case '\\' when i + 1 < rtf.Length:
                    i = ReadControl(rtf, i + 1, builder, ref skip, ref skipFallback);
                    break;
                default:
                    if (skipFallback > 0)
                    {
                        skipFallback--;
                    }
                    else if (!skip)
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    // Returns the index of the last character consumed.
    private static int ReadControl(string rtf, int start, StringBuilder builder, ref bool skip, ref int skipFallback)
    {
        var next = rtf[start];
        if (next is '\\' or '{' or '}')
        {
            if (!skip) builder.Append(next);
            return start;
        }

        if (next == '*')
        {
            skip = true;
            return start;
        }

        if (next == '\'')
        {
            if (start + 2 < rtf.Length
                && byte.TryParse(rtf.AsSpan(start + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                if (!skip) builder.Append((char)value);
                return start + 2;
            }

            return start;
        }

        if (!char.IsAsciiLetter(next))
        {
            return start;
        }

        var end = start;
        while (end < rtf.Length && char.IsAsciiLetter(rtf[end])) end++;
        var word = rtf[start..end];

        var numberStart = end;
        if (end < rtf.Length && rtf[end] == '-') end++;
        while (end < rtf.Length && char.IsAsciiDigit(rtf[end])) end++;
        _ = int.TryParse(rtf.AsSpan(numberStart, end - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number);

        // One space after a control word is its delimiter, not text.
        var last = end - 1;
        if (end < rtf.Length && rtf[end] == ' ')
        {
            last = end;
        }

        if (SkippedDestinations.Contains(word))
        {
            skip = true;
        }
        else if (!skip)
        {
            switch (word)
            {
                case "par" or "line" or "row" or "sect" or "page":
                    builder.Append('\n');
                    break;
                case "tab" or "cell":
                    builder.Append(' ');
                    break;
                case "u":
                    builder.Append((char)(short)number);
                    skipFallback = 1;
                    break;
            }
        }

        return last;
    }
}
