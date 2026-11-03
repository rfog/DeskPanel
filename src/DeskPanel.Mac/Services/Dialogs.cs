using Avalonia.Controls;
using Avalonia.Layout;

namespace DeskPanel.Mac.Services;

/// <summary>Small modal prompts; Avalonia has no built-in message box.</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window owner, string message, string yes = "Continuar", string no = "Cancelar")
    {
        var result = false;
        var dialog = new Window
        {
            Title = "DeskPanel",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var yesButton = new Button { Content = yes, IsDefault = true };
        var noButton = new Button { Content = no, IsCancel = true };
        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        noButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { noButton, yesButton },
                },
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
