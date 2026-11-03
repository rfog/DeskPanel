using Avalonia.Controls;
using Avalonia.Input.Platform;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Feeds;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Feeds;

/// <summary>Summarises one entry with the model chosen in Settings › IA and saves the result as Markdown.</summary>
public partial class FeedSummaryWindow : Window
{
    private readonly MacServices _services;
    private FeedEntry _entry;
    private CancellationTokenSource? _cancellation;
    private string _savedText = "";
    private bool _isBusy;
    private bool _closeConfirmed;

    public FeedSummaryWindow() : this(null!, null!)
    {
    }

    public FeedSummaryWindow(MacServices services, FeedEntry entry)
    {
        InitializeComponent();
        _services = services;
        _entry = entry;
        if (services is null)
        {
            return;
        }

        TitleText.Text = entry.Title;
        RegenerateButton.Click += async (_, _) => await StartAsync();
        CopyButton.Click += async (_, _) => await CopyAsync();
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => _cancellation?.Cancel();
        Opened += async (_, _) => await StartAsync();
        Closed += (_, _) => _cancellation?.Cancel();
        Closing += OnClosing;
        SetBusy(false);
    }

    /// <summary>Text on screen that was neither saved nor discarded on purpose: closing the window would lose it.</summary>
    public bool HasUnsavedText => !_isBusy && !string.IsNullOrWhiteSpace(MarkdownBox.Text) && MarkdownBox.Text != _savedText;

    /// <summary>Lets the window close without asking again (the main window already asked on its behalf).</summary>
    public void SkipCloseQuestion() => _closeConfirmed = true;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !HasUnsavedText)
        {
            return;
        }

        e.Cancel = true;
        if (await Dialogs.ConfirmAsync(this, "Este resumen no se ha guardado y se perderá. ¿Cerrar sin guardar?", "Cerrar sin guardar"))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RegenerateButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        var hasText = !busy && !string.IsNullOrEmpty(MarkdownBox.Text);
        CopyButton.IsEnabled = hasText;
        SaveButton.IsEnabled = hasText;
    }

    private async Task StartAsync()
    {
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var ct = _cancellation.Token;

        SetBusy(true);
        MarkdownBox.Text = "";
        try
        {
            var outcome = await FeedSummarizer.GenerateAsync(
                _services.Config.App, _services.Secrets, _entry, null, ct, message => StatusText.Text = message);
            _entry = outcome.Entry;
            MarkdownBox.Text = outcome.Markdown ?? "";
            StatusText.Text = outcome.Status;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelado.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CopyAsync()
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(MarkdownBox.Text ?? "");
            StatusText.Text = "Copiado al portapapeles.";
        }
    }

    private void Save()
    {
        try
        {
            var config = _services.Config;
            var path = FeedSummarizer.Save(config.App.Feeds, config.Paths, _services.ResolveCacheFolder(), _entry, MarkdownBox.Text ?? "");
            _savedText = MarkdownBox.Text ?? "";
            StatusText.Text = $"Guardado: {path}";
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not save the feed summary.", ex);
            StatusText.Text = $"No se pudo guardar: {ex.Message}";
        }
    }
}
