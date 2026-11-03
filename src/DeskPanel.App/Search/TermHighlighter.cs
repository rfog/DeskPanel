using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using DeskPanel.Search.Searching;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace DeskPanel.App.Search;

/// <summary>
/// Highlights query terms in the text preview. Index terms are lower-cased and accent-folded, so matching uses
/// IgnoreNonSpace to find "canción" in the document when the index term is "cancion".
/// </summary>
internal sealed class TermHighlighter : DocumentColorizingTransformer
{
    private const CompareOptions MatchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    private readonly IReadOnlyList<string> _terms;
    private readonly Brush _brush;

    public TermHighlighter(IReadOnlyList<string> terms, Brush brush)
    {
        _terms = terms;
        _brush = brush;
    }

    /// <summary>Offset and length of the first match in the document, used to scroll the preview to it.</summary>
    public (int Offset, int Length)? FindFirst(TextDocument document)
    {
        (int Offset, int Length)? best = null;
        var text = document.Text;
        foreach (var term in _terms)
        {
            var index = Compare.IndexOf(text.AsSpan(), term.AsSpan(), MatchOptions, out var length);
            if (index >= 0 && (best is null || index < best.Value.Offset))
            {
                best = (index, length);
            }
        }

        return best;
    }

    /// <summary>Every match in document order (shared with the web preview through <see cref="TermMatcher"/>).</summary>
    public List<(int Offset, int Length)> FindAll(string text, int maxPerTerm = 20_000) =>
        TermMatcher.FindAll(text, _terms, maxPerTerm);

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_terms.Count == 0 || line.Length == 0)
        {
            return;
        }

        var lineText = CurrentContext.Document.GetText(line);
        foreach (var term in _terms)
        {
            var position = 0;
            while (position < lineText.Length)
            {
                var index = Compare.IndexOf(lineText.AsSpan(position), term.AsSpan(), MatchOptions, out var length);
                if (index < 0 || length == 0)
                {
                    break;
                }

                var start = line.Offset + position + index;
                ChangeLinePart(start, start + length, element => element.TextRunProperties.SetBackgroundBrush(_brush));
                position += index + length;
            }
        }
    }
}
