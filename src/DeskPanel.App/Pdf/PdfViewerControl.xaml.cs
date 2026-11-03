using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeskPanel.App.Web;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;
using Microsoft.Web.WebView2.Core;

namespace DeskPanel.App.Pdf;

/// <summary>
/// pdf.js viewer hosted in WebView2. Annotation saves arrive as pdf.js "downloads"; they are redirected to a
/// temp file and then atomically swapped over the original, so saving edits the file in place.
/// </summary>
public partial class PdfViewerControl : UserControl
{
    private LocalContentServer? _server;
    private string? _currentPath;
    private string? _currentSearch;
    private TaskCompletionSource<bool>? _saveCompletion;
    private bool _reloadAfterSave = true;
    private bool _readOnly;

    public PdfViewerControl()
    {
        InitializeComponent();
    }

    public event Action<string>? Saved;

    /// <summary>Raised with pdf.js' own match counter: (current, total); current is 0 until a match is selected.</summary>
    public event Action<int, int>? FindCountChanged;

    public string? CurrentPath => _currentPath;

    /// <param name="readOnly">
    /// For documents that are only a rendition (e.g. a Word export): hides pdf.js' annotation and save buttons.
    /// </param>
    internal async Task LoadAsync(WebViewEnvironmentProvider environmentProvider, string pdfPath, string? searchText, bool readOnly = false)
    {
        await EnsureInitializedAsync(environmentProvider);
        _currentPath = pdfPath;
        _currentSearch = searchText;
        _readOnly = readOnly;
        _server!.SetDocument(await File.ReadAllBytesAsync(pdfPath));
        Browser.CoreWebView2.Navigate(LocalContentServer.BuildViewerUrl(searchText));
    }

    /// <summary>
    /// Moves to the next/previous match using pdf.js' find controller. The controller's current state is reused
    /// (the initial #search= query is a word array, and a different query shape would restart from the first match).
    /// </summary>
    public async Task FindAsync(System.Collections.Generic.IReadOnlyList<string> terms, bool previous)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        var script = """
            (() => {
              const app = window.PDFViewerApplication;
              if (!app || !app.eventBus || !app.findController) return false;
              const state = app.findController.state;
              const base = state ? { ...state } : { query: __TERMS__, caseSensitive: false, entireWord: false, highlightAll: true, matchDiacritics: false };
              app.eventBus.dispatch('find', { ...base, source: app, type: 'again', findPrevious: __PREV__ });
              return true;
            })()
            """
            .Replace("__TERMS__", System.Text.Json.JsonSerializer.Serialize(terms))
            .Replace("__PREV__", previous ? "true" : "false");
        await Browser.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }

        try
        {
            // Bridge pdf.js' match counter to the host toolbar. Re-done per navigation: each reload is a new page.
            await Browser.CoreWebView2.ExecuteScriptAsync("""
                (async () => {
                  const app = window.PDFViewerApplication;
                  if (!app) return;
                  await app.initializedPromise;
                  const post = (m) => window.chrome?.webview?.postMessage({ k: 'find', current: m.current, total: m.total });
                  app.eventBus.on('updatefindmatchescount', (e) => post(e.matchesCount));
                  app.eventBus.on('updatefindcontrolstate', (e) => post(e.matchesCount));
                })()
                """);

            if (_readOnly)
            {
                // Inline !important: pdf.js' stylesheet uses cascade layers, where its !important rules beat any
                // unlayered stylesheet of ours, and it toggles a "hidden" class on these buttons once the document loads.
                await Browser.CoreWebView2.ExecuteScriptAsync("""
                    (() => {
                      for (const id of ['editorModeButtons', 'editorModeSeparator', 'downloadButton', 'secondaryDownload']) {
                        document.getElementById(id)?.style.setProperty('display', 'none', 'important');
                      }
                    })()
                    """);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not set up the PDF viewer bridge.", ex);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("k", out var kind) && kind.GetString() == "find")
            {
                FindCountChanged?.Invoke(root.GetProperty("current").GetInt32(), root.GetProperty("total").GetInt32());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Not one of our messages.
        }
    }

    /// <summary>
    /// True when the document has edits that are not in the file on disk. annotationStorage.size is NOT a valid
    /// signal: pdf.js keeps saved annotations in the storage and only clears its "modified" flag, so size stays
    /// above zero after a successful save. The flag is the same one pdf.js uses to decide on auto-saving.
    /// </summary>
    public async Task<bool> HasUnsavedChangesAsync()
    {
        if (Browser.CoreWebView2 is null || _readOnly)
        {
            return false;
        }

        // An annotation still being edited (typed text, a just-drawn stroke) only reaches the storage when committed.
        const string script = """
            (() => {
              const app = window.PDFViewerApplication;
              if (!app || !app.pdfDocument) return false;
              app.pdfViewer?._layerProperties?.annotationEditorUIManager?.endCurrentEditing();
              return !!(app._annotationStorageModified
                || app.pdfThumbnailViewer?.hasStructuralChanges?.()
                || app._mergedDocumentNeedsSaving === true);
            })()
            """;
        return await Browser.CoreWebView2.ExecuteScriptAsync(script) == "true";
    }

    /// <summary>
    /// Saves through pdf.js' own save, which arrives as a download and is committed by <see cref="CommitSave"/>.
    /// Completes when the original file has been replaced.
    /// </summary>
    public async Task<bool> SaveAsync(bool reloadAfterSave)
    {
        if (Browser.CoreWebView2 is null)
        {
            return false;
        }

        _reloadAfterSave = reloadAfterSave;
        var completion = _saveCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync("PDFViewerApplication.downloadOrSave()");
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Timed out waiting for pdf.js to deliver the saved PDF.");
            return false;
        }
        finally
        {
            _saveCompletion = null;
            _reloadAfterSave = true;
        }
    }

    /// <summary>
    /// Asks what to do with unsaved edits before the document is closed or replaced.
    /// Returns false only when the user chose to keep editing (or saving failed).
    /// </summary>
    public async Task<bool> ResolveUnsavedChangesAsync(Window owner, bool allowCancel)
    {
        if (!await HasUnsavedChangesAsync())
        {
            return true;
        }

        var message = "Este PDF tiene cambios sin guardar.\n\nSí: guardarlos en el PDF\nNo: descartarlos"
                      + (allowCancel ? "\nCancelar: seguir editando" : "");
        var answer = MessageBox.Show(owner, message, "DeskPanel",
            allowCancel ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (answer == MessageBoxResult.No)
        {
            return true;
        }

        if (await SaveAsync(reloadAfterSave: false))
        {
            return true;
        }

        MessageBox.Show(owner, "No se pudo guardar el PDF. Los cambios siguen sin guardar.", "DeskPanel",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task EnsureInitializedAsync(WebViewEnvironmentProvider environmentProvider)
    {
        if (_server is not null)
        {
            return;
        }

        var environment = await environmentProvider.GetAsync();
        await Browser.EnsureCoreWebView2Async(environment);

        _server = new LocalContentServer(environment, Path.Combine(Shell.AssetsDirectory, "pdfjs"));
        _server.Attach(Browser.CoreWebView2);
        Browser.CoreWebView2.DownloadStarting += OnDownloadStarting;
        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        // pdf.js registers a beforeunload handler whenever it holds annotations. Without this, the reload after a
        // save raises a "leave site?" prompt that can cancel the navigation and leave the viewer in its old state.
        // Unsaved edits are handled by ResolveUnsavedChangesAsync before anything navigates away.
        Browser.CoreWebView2.ScriptDialogOpening += (_, e) =>
        {
            if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload)
            {
                e.Accept();
            }
        };
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var target = _currentPath;
        if (target is null)
        {
            e.Cancel = true;
            return;
        }

        Directory.CreateDirectory(Shell.TempDirectory);
        var temp = Path.Combine(Shell.TempDirectory, $"annot-{Guid.NewGuid():N}.pdf");
        e.ResultFilePath = temp;

        // Suppresses the browser download flyout; from the user's point of view this is a save, not a download.
        e.Handled = true;

        var operation = e.DownloadOperation;
        operation.StateChanged += (_, _) =>
        {
            switch (operation.State)
            {
                case CoreWebView2DownloadState.Completed:
                    CommitSave(temp, target);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    AppLog.Warn($"Annotation save interrupted: {operation.InterruptReason}");
                    _saveCompletion?.TrySetResult(false);
                    break;
            }
        };
    }

    private async void CommitSave(string temp, string target)
    {
        try
        {
            AtomicFile.ReplaceWith(temp, target);
            TryDelete(temp);
            Saved?.Invoke(target);
        }
        catch (Exception ex)
        {
            _saveCompletion?.TrySetResult(false);
            AppLog.Error($"Could not save annotations to {target}.", ex);
            MessageBox.Show($"No se pudieron guardar las anotaciones en:\n{target}\n\n{ex.Message}\n\nLa copia está en:\n{temp}",
                "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The file is on disk from here on: whatever happens next must not report the save as failed.
        var reload = _reloadAfterSave;
        _saveCompletion?.TrySetResult(true);
        if (!reload)
        {
            return;
        }

        try
        {
            // Reloading from disk proves the save round-trips and resets pdf.js' modified tracking. Its
            // beforeunload guard is switched off first: unsaved state is already handled by this control.
            await Browser.CoreWebView2.ExecuteScriptAsync("if (window.PDFViewerApplication) PDFViewerApplication._hasChanges = () => false;");
            _server!.SetDocument(await File.ReadAllBytesAsync(target));
            Browser.CoreWebView2.Navigate(LocalContentServer.BuildViewerUrl(_currentSearch));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Saved {target} but could not reload the viewer.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the startup temp sweep.
        }
    }
}
