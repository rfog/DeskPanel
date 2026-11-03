using System.Globalization;

namespace DeskPanel.Search.Searching;

/// <summary>
/// Finds the query terms inside a document's text, the same way for every viewer (WPF preview and web page).
/// Index terms are lower-cased and accent-folded, so matching ignores case and diacritics: "canción" in the
/// document is found by the term "cancion".
/// </summary>
public static class TermMatcher
{
    private const CompareOptions MatchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>
    /// Every match in document order, overlaps between terms merged. Offsets are UTF-16 indexes, which is what both
    /// AvalonEdit and JavaScript strings use. Pure string work, safe off the UI thread.
    /// </summary>
    public static List<(int Offset, int Length)> FindAll(string text, IEnumerable<string> terms, int maxPerTerm = 20_000)
    {
        var all = new List<(int Offset, int Length)>();
        foreach (var term in terms)
        {
            if (term.Length == 0)
            {
                continue;
            }

            var position = 0;
            var found = 0;
            while (position < text.Length && found < maxPerTerm)
            {
                var index = Compare.IndexOf(text.AsSpan(position), term.AsSpan(), MatchOptions, out var length);
                if (index < 0 || length == 0)
                {
                    break;
                }

                all.Add((position + index, length));
                position += index + length;
                found++;
            }
        }

        all.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var merged = new List<(int Offset, int Length)>(all.Count);
        var lastEnd = -1;
        foreach (var match in all)
        {
            if (match.Offset >= lastEnd)
            {
                merged.Add(match);
                lastEnd = match.Offset + match.Length;
            }
        }

        return merged;
    }
}
