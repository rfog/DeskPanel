using System;
using System.ComponentModel;
using System.Windows;

namespace DeskPanel.App.Audio;

internal partial class AudioWindow : Window
{
    private readonly AudioViewModel _viewModel;

    public AudioWindow(AudioViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Dropping audio on the window itself works too (the panel closes when it loses focus, which a drag from
        // Explorer does, so dropping on the window is the easy path).
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = DroppedPaths(e) is { } paths && AudioImporter.HasAudio(paths) && !viewModel.IsBusy ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            if (DroppedPaths(e) is { } paths)
            {
                await viewModel.ImportAsync(paths);
            }
        };

        // The pipeline reports from background threads through Progress<T> and the log callback; marshal to the UI.
        // The view model outlives the window (the web interface shares it), so the handler must go with the window.
        Action<string> onLog = line => Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(line + "\n");
            LogBox.ScrollToEnd();
        });
        viewModel.LogLine += onLog;
        Closed += (_, _) => viewModel.LogLine -= onLog;

        // Picking up changes made in the settings dialog (tools just installed, folders) when coming back here.
        Activated += (_, _) =>
        {
            if (viewModel.RefreshCommand.CanExecute(null))
            {
                viewModel.RefreshCommand.Execute(null);
            }
        };
        Closing += OnClosing;
    }

    public AudioViewModel ViewModel => _viewModel;

    private static string[]? DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy)
        {
            return;
        }

        var answer = MessageBox.Show(this, "Hay audios en proceso. ¿Cancelar el proceso y cerrar?", "DeskPanel",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            _viewModel.CancelIfRunning();
        }
        else
        {
            e.Cancel = true;
        }
    }
}
