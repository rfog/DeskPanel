using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DeskPanel.Search.Searching;

namespace DeskPanel.App.Search;

/// <summary>
/// Attached property that turns a marker-delimited snippet into TextBlock inlines with highlighted runs,
/// avoiding an HTML renderer per result row.
/// </summary>
internal static class SnippetText
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(SnippetText), new PropertyMetadata(null, OnValueChanged));

    public static string? GetValue(DependencyObject target) => (string?)target.GetValue(ValueProperty);

    public static void SetValue(DependencyObject target, string? value) => target.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        var text = e.NewValue as string ?? "";
        var highlight = (System.Windows.Media.Brush)Application.Current.Resources["HighlightBrush"];
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
                    run.Background = highlight;
                    run.FontWeight = FontWeights.SemiBold;
                }

                block.Inlines.Add(run);
            }

            if (!atEnd)
            {
                inHighlight = text[i] == SearchHit.HighlightStart;
            }

            start = i + 1;
        }
    }
}
