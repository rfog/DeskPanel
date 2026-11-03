using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace DeskPanel.App.Pdf;

internal partial class PdfListWindow : Window
{
    private readonly PdfListViewModel _viewModel;

    public PdfListWindow(PdfListViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        List.MouseDoubleClick += (_, _) => OpenSelected();

        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    OpenSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Down when FilterBox.IsKeyboardFocused && List.Items.Count > 0:
                    // Typing a filter and arrowing straight into the results is the quick path.
                    List.SelectedIndex = System.Math.Max(0, List.SelectedIndex);
                    (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as UIElement)?.Focus();
                    e.Handled = true;
                    break;
            }
        };

        // New PDFs may have been generated meanwhile; pick them up on coming back to the window.
        Activated += (_, _) => viewModel.Refresh();
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void OpenSelected()
    {
        // Typing a filter and pressing Enter opens the first match.
        _viewModel.Selected ??= _viewModel.Items.FirstOrDefault(i => _viewModel.View.Filter?.Invoke(i) != false);
        if (_viewModel.Selected is { } item)
        {
            _viewModel.OpenCommand.Execute(item);
        }
    }
}
