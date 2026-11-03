using System.Reflection;
using System.Windows;

namespace DeskPanel.App.About;

internal partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // IsCancel/IsDefault only close a dialog shown with ShowDialog; this window is shown with Show().
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "Versión de desarrollo" : $"Versión {version.ToString(3)}";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
