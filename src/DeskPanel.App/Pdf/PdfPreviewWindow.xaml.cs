using System;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace DeskPanel.App.Pdf;

internal partial class PdfPreviewWindow : Window
{
    private readonly Shell _shell;
    private readonly string _pdfPath;
    private readonly string? _searchText;
    private bool _closeConfirmed;

    public PdfPreviewWindow(Shell shell, string pdfPath, string? searchText)
    {
        _shell = shell;
        _pdfPath = pdfPath;
        _searchText = searchText;
        InitializeComponent();

        Title = Path.GetFileName(pdfPath);
        FileLabel.Text = Path.GetFileName(pdfPath);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Viewer.Saved += _ => FileLabel.Text = $"{Path.GetFileName(_pdfPath)}  (guardado {DateTime.Now:HH:mm:ss})";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Viewer.LoadAsync(_shell.WebView, _pdfPath, _searchText);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo abrir el PDF:\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        // Closing must be cancelled synchronously; the async check re-issues Close once it knows the answer.
        e.Cancel = true;

        // Yield first: a synchronous completion would otherwise call Close() from inside the Closing event.
        await System.Windows.Threading.Dispatcher.Yield();
        if (!await Viewer.ResolveUnsavedChangesAsync(this, allowCancel: true))
        {
            return;
        }

        _closeConfirmed = true;
        Close();
    }

    private void OnSummarizeClick(object sender, RoutedEventArgs e) => PdfSummary.Open(_shell, _pdfPath);

    private void OnRevealClick(object sender, RoutedEventArgs e) => _shell.RevealFile(_pdfPath);

    private void OnOpenExternalClick(object sender, RoutedEventArgs e) => _shell.OpenWithShell(_pdfPath);
}
