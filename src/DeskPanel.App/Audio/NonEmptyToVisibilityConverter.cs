using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeskPanel.App.Audio;

/// <summary>Visible only when the bound text is not empty; used for banners that show a message when there is one.</summary>
internal sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
