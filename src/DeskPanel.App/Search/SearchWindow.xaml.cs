using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DeskPanel.App.Pdf;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Search.Extraction;
using DeskPanel.Search.Searching;

namespace DeskPanel.App.Search;

internal partial class SearchWindow : Window
{
    private const int MaxPreviewChars = 5_000_000;

    private enum PreviewKind { None, Text, Pdf }

    private readonly Shell _shell;
    private TermHighlighter? _highlighter;
    private int _previewGeneration;
    private bool _closeConfirmed;

    private PreviewKind _kind;
    private IReadOnlyList<string> _terms = Array.Empty<string>();
    private List<(int Offset, int Length)> _textMatches = new();
    private int _textMatchIndex = -1;
    private CancellationTokenSource? _wordCancellation;

    public SearchWindow(Shell shell)
    {
        _shell = shell;
        var viewModel = new SearchViewModel(shell);
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PreviewRequested += ShowPreview;
        Activated += (_, _) => viewModel.ReloadIndexes();
        Loaded += (_, _) => QueryBox.Focus();
        Closing += OnClosing;
        PdfPreview.FindCountChanged += OnPdfFindCountChanged;

        // F3 / Shift+F3 work wherever the focus is, as in browsers and editors.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F3 && _kind != PreviewKind.None)
            {
                Navigate(previous: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
            }
        };
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || PdfPreview.Visibility != Visibility.Visible)
        {
            return;
        }

        // Closing must be cancelled synchronously; Close is re-issued once the user has decided.
        e.Cancel = true;

        // Yield first: when the check finishes synchronously (e.g. a read-only Word rendition) the code below would
        // still run inside the Closing event, where WPF forbids calling Close().
        await System.Windows.Threading.Dispatcher.Yield();
        if (await PdfPreview.ResolveUnsavedChangesAsync(this, allowCancel: true))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async void ShowPreview(SearchHit hit, IReadOnlyList<string> terms)
    {
        // Rapid arrow-key navigation fires several previews; only the latest one may touch the UI.
        var generation = ++_previewGeneration;
        _wordCancellation?.Cancel();
        _terms = terms;
        SetKind(PreviewKind.None);
        OpenWordButton.Visibility = WordPdfConverter.CanConvert(hit.Extension) ? Visibility.Visible : Visibility.Collapsed;

        // The embedded viewer is about to be replaced; edits made in it would otherwise vanish silently.
        if (PdfPreview.Visibility == Visibility.Visible)
        {
            await PdfPreview.ResolveUnsavedChangesAsync(this, allowCancel: false);
            if (generation != _previewGeneration)
            {
                return;
            }
        }

        if (!File.Exists(hit.FullPath))
        {
            ShowPlaceholder($"El fichero no existe en esta máquina:\n{hit.FullPath}");
            return;
        }

        try
        {
            if (hit.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                SetVisible(PdfPreview);
                SetKind(PreviewKind.Pdf);
                await PdfPreview.LoadAsync(_shell.WebView, hit.FullPath, string.Join(' ', terms));
                return;
            }

            if (WordPdfConverter.CanConvert(hit.Extension) && await TryShowWordAsync(hit, terms, generation))
            {
                return;
            }

            if (generation != _previewGeneration)
            {
                return;
            }

            var highlighter = new TermHighlighter(terms.ToList(), (Brush)FindResource("HighlightBrush"));

            // Matches are located off the UI thread together with the text: on a multi-MB document it is not instant.
            var (text, matches) = await Task.Run(() =>
            {
                var content = ReadPreviewText(hit.FullPath);
                return (content, highlighter.FindAll(content));
            });
            if (generation != _previewGeneration)
            {
                return;
            }

            if (_highlighter is not null)
            {
                TextPreview.TextArea.TextView.LineTransformers.Remove(_highlighter);
            }

            TextPreview.Text = text;
            _highlighter = highlighter;
            TextPreview.TextArea.TextView.LineTransformers.Add(_highlighter);
            SetVisible(TextPreview);

            _textMatches = matches;
            _textMatchIndex = matches.Count > 0 ? 0 : -1;
            SetKind(PreviewKind.Text);
            if (_textMatchIndex >= 0)
            {
                SelectTextMatch();
            }
            else
            {
                TextPreview.ScrollToHome();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Preview failed for {hit.FullPath}.", ex);
            ShowPlaceholder($"No se pudo previsualizar:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Word documents are rendered by Word itself (read-only) into a PDF shown in the PDF viewer. Returns false when
    /// that is not possible, so the caller falls back to the extracted-text preview.
    /// </summary>
    private async Task<bool> TryShowWordAsync(SearchHit hit, IReadOnlyList<string> terms, int generation)
    {
        _wordCancellation = new CancellationTokenSource();
        ShowPlaceholder("Preparando la vista con Word (solo lectura)…");
        try
        {
            var pdf = await WordPdfConverter.ConvertAsync(hit.FullPath, Shell.TempDirectory, _wordCancellation.Token);
            if (generation != _previewGeneration)
            {
                return true;
            }

            SetVisible(PdfPreview);
            SetKind(PreviewKind.Pdf);
            await PdfPreview.LoadAsync(_shell.WebView, pdf, string.Join(' ', terms), readOnly: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Word could not render {hit.FullPath}; showing extracted text instead.", ex);
            return false;
        }
    }

    private void SetKind(PreviewKind kind)
    {
        _kind = kind;
        PrevMatchButton.IsEnabled = NextMatchButton.IsEnabled = kind != PreviewKind.None;
        MatchInfo.Text = kind == PreviewKind.Text ? TextMatchInfo() : "";
    }

    private void Navigate(bool previous)
    {
        switch (_kind)
        {
            case PreviewKind.Text when _textMatches.Count > 0:
                var count = _textMatches.Count;
                _textMatchIndex = (_textMatchIndex + (previous ? -1 : 1) + count) % count;
                SelectTextMatch();
                break;
            case PreviewKind.Pdf:
                _ = PdfPreview.FindAsync(_terms, previous);
                break;
        }
    }

    private void SelectTextMatch()
    {
        var (offset, length) = _textMatches[_textMatchIndex];
        var location = TextPreview.Document.GetLocation(offset);
        TextPreview.Select(offset, length);
        TextPreview.ScrollTo(location.Line, location.Column);
        MatchInfo.Text = TextMatchInfo();
    }

    private string TextMatchInfo() =>
        _textMatches.Count == 0 ? "Sin coincidencias" : $"{_textMatchIndex + 1} de {_textMatches.Count}";

    private void OnPdfFindCountChanged(int current, int total)
    {
        if (_kind != PreviewKind.Pdf)
        {
            return;
        }

        MatchInfo.Text = total == 0 ? "Sin coincidencias" : current > 0 ? $"{current} de {total}" : $"{total} coincidencias";
    }

    private void OnPrevMatch(object sender, RoutedEventArgs e) => Navigate(previous: true);

    private void OnNextMatch(object sender, RoutedEventArgs e) => Navigate(previous: false);

    private async void OnOpenInWord(object sender, RoutedEventArgs e)
    {
        if (((SearchViewModel)DataContext).SelectedHit is not { } hit)
        {
            return;
        }

        try
        {
            await WordPdfConverter.OpenReadOnlyAsync(hit.FullPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not open {hit.FullPath} in Word.", ex);
            MessageBox.Show(this, $"No se pudo abrir en Word:\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Every indexed format previews as the text the indexer extracted, so docx/xlsx/odt/epub/rtf/zip need no
    /// dedicated viewer and the highlighting matches what was actually searched.
    /// </summary>
    private string ReadPreviewText(string path)
    {
        var text = _shell.Extractors.TryGet(Path.GetExtension(path), out var extractor)
            ? extractor.Extract(path)
            : TextDecoding.ReadAllTextSmart(path);

        // A multi-gigabyte zip would otherwise produce a document AvalonEdit cannot reasonably open.
        return text.Length > MaxPreviewChars
            ? text[..MaxPreviewChars] + "\n\n[… vista previa truncada …]"
            : text;
    }

    private void ShowPlaceholder(string message)
    {
        PreviewPlaceholder.Text = message;
        SetVisible(PreviewPlaceholder);
    }

    private void SetVisible(UIElement element)
    {
        PreviewPlaceholder.Visibility = element == PreviewPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        TextPreview.Visibility = element == TextPreview ? Visibility.Visible : Visibility.Collapsed;
        PdfPreview.Visibility = element == PdfPreview ? Visibility.Visible : Visibility.Collapsed;
    }
}
