using System.ComponentModel;
using System.Windows;

namespace DeskPanel.App.Feeds;

internal partial class FeedSummaryWindow : Window
{
    private readonly FeedSummaryViewModel _viewModel;

    public FeedSummaryWindow(FeedSummaryViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        Loaded += async (_, _) => await viewModel.StartAsync();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // A request still running when the window goes away must not keep a model busy for nothing.
        if (_viewModel.IsBusy)
        {
            _viewModel.Cancel();
        }
    }
}
