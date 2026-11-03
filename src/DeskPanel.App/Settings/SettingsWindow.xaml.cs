using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeskPanel.App.Settings;

internal partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += Close;
    }

    private void OnWebPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.WebPassword = ((PasswordBox)sender).Password;

    // PasswordBox.Password cannot be data-bound (deliberately, so secrets are not kept in a bindable property),
    // so the secrets are copied by hand between the boxes and the view model.
    private void OnSecretBoxLoaded(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.Password = (box.Tag as string) switch
        {
            "lm" => _viewModel.LmStudioToken,
            "gemini" => _viewModel.GeminiKey,
            _ => _viewModel.ClaudeKey,
        };
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        switch (box.Tag as string)
        {
            case "lm": _viewModel.LmStudioToken = box.Password; break;
            case "gemini": _viewModel.GeminiKey = box.Password; break;
            default: _viewModel.ClaudeKey = box.Password; break;
        }
    }

    /// <summary>Captures the chord itself instead of parsing typed text, which avoids invalid combinations.</summary>
    private void OnHotKeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CaptureChord(e) is var (modifiers, key))
        {
            _viewModel.SetHotKey(modifiers, key);
        }
    }

    /// <summary>The same capture for the boxes of the "Atajos" tab; each box's data context is its row.</summary>
    private void OnSystemKeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SystemKeyRow row && CaptureChord(e) is var (modifiers, key))
        {
            row.SetChord(modifiers, key);
        }
    }

    /// <summary>Marks the event handled and returns the chord when a real key was pressed with a modifier.</summary>
    private static (ModifierKeys Modifiers, Key Key)? CaptureChord(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            return null; // keep keyboard navigation out of the box
        }

        e.Handled = true;
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return null;
        }

        return modifiers != ModifierKeys.None ? (modifiers, key) : null;
    }
}
