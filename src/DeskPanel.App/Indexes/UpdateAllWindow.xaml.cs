using System.ComponentModel;
using System.Windows;

namespace DeskPanel.App.Indexes;

internal partial class UpdateAllWindow : Window
{
    private readonly UpdateAllViewModel _viewModel;

    public UpdateAllWindow(UpdateAllViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // The view model decides when it is over; the window only obeys.
        viewModel.CloseRequested += Close;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // The X button (or Esc) while indexing means "cancel", and the window stays until the work has really stopped.
        if (_viewModel.IsRunning)
        {
            e.Cancel = true;
            _viewModel.RequestCancel();
        }
    }
}
