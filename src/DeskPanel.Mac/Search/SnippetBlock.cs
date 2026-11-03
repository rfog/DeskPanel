using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DeskPanel.Search.Searching;

namespace DeskPanel.Mac.Search;

/// <summary>
/// A TextBlock that renders a marker-delimited snippet (<see cref="SearchHit.HighlightStart"/> / <see cref="SearchHit.HighlightEnd"/>)
/// with highlighted runs, avoiding an HTML renderer per result row.
/// </summary>
public sealed class SnippetBlock : TextBlock
{
    public static readonly StyledProperty<string?> SnippetProperty =
        AvaloniaProperty.Register<SnippetBlock, string?>(nameof(Snippet));

    static SnippetBlock() => SnippetProperty.Changed.AddClassHandler<SnippetBlock>((block, _) => block.Rebuild());

    public string? Snippet
    {
        get => GetValue(SnippetProperty);
        set => SetValue(SnippetProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    private void Rebuild()
    {
        Inlines?.Clear();
        var text = Snippet ?? "";
        var inHighlight = false;
        var start = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            if (!atEnd && text[i] != SearchHit.HighlightStart && text[i] != SearchHit.HighlightEnd)
            {
                continue;
            }

            if (i > start)
            {
                var run = new Run(text[start..i]);
                if (inHighlight)
                {
                    run.Background = new SolidColorBrush(Color.FromArgb(120, 255, 214, 0));
                    run.FontWeight = FontWeight.SemiBold;
                }

                Inlines!.Add(run);
            }

            if (!atEnd)
            {
                inHighlight = text[i] == SearchHit.HighlightStart;
            }

            start = i + 1;
        }
    }
}
