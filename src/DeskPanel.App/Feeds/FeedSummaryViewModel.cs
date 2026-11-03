using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Feeds;

namespace DeskPanel.App.Feeds;

/// <summary>Summarises one feed entry with the model chosen in Settings › IA and saves the result as Markdown.</summary>
internal sealed partial class FeedSummaryViewModel : ObservableObject
{
    private readonly Shell _shell;
    private FeedEntry _entry;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CopyCommand))]
    private string _markdown = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CopyCommand), nameof(RegenerateCommand))]
    private bool _isBusy;

    private readonly Func<string>? _plainTextSource;

    /// <param name="plainTextSource">
    /// Supplies the text directly (a PDF's extracted text) instead of converting <see cref="FeedEntry.ContentHtml"/>.
    /// It runs on a worker thread because extracting a long PDF takes a while.
    /// </param>
    public FeedSummaryViewModel(Shell shell, FeedEntry entry, Func<string>? plainTextSource = null)
    {
        _shell = shell;
        _entry = entry;
        _plainTextSource = plainTextSource;
    }

    public string Title => _entry.Title;

    public string? Link => _entry.Link;

    public async Task StartAsync()
    {
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var ct = _cancellation.Token;

        IsBusy = true;
        Markdown = "";
        try
        {
            var outcome = await FeedSummarizer.GenerateAsync(_shell, _entry, _plainTextSource, ct, message => Status = message);
            _entry = outcome.Entry;
            Markdown = outcome.Markdown ?? "";
            Status = outcome.Status;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelado.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    private bool HasMarkdown() => !IsBusy && Markdown.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private Task RegenerateAsync() => StartAsync();

    private bool CanRegenerate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(HasMarkdown))]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(Markdown);
            Status = "Copiado al portapapeles.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Status = "No se pudo copiar (el portapapeles está en uso).";
        }
    }

    [RelayCommand(CanExecute = nameof(HasMarkdown))]
    private void Save()
    {
        try
        {
            Status = $"Guardado: {FeedSummarizer.Save(_shell, _entry, Markdown)}";
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not save the feed summary.", ex);
            Status = $"No se pudo guardar: {ex.Message}";
        }
    }
}
