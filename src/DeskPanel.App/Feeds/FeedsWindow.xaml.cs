using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using DeskPanel.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace DeskPanel.App.Feeds;

internal partial class FeedsWindow : Window
{
    private readonly FeedsViewModel _viewModel;
    private int _previewGeneration;
    private bool _browserReady;
    private bool _articleMode = true;
    private string? _previewFile;

    public FeedsWindow(FeedsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ContentChanged += async item =>
        {
            if (ReferenceEquals(item, viewModel.SelectedItem))
            {
                await UpdatePreviewAsync();
            }
        };
        Closed += (_, _) => DeletePreviewFile();

        // Fetch on open: the window exists to show what is new, an empty list first is just an extra click.
        Loaded += async (_, _) => await viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The profile matters too: the preview uses the same CSS the PDF will be printed with.
        if (e.PropertyName is nameof(FeedsViewModel.SelectedItem) or nameof(FeedsViewModel.ShowOriginalWeb)
            or nameof(FeedsViewModel.SelectedProfile) or nameof(FeedsViewModel.CssOverride))
        {
            await UpdatePreviewAsync();
        }
    }

    private async System.Threading.Tasks.Task UpdatePreviewAsync()
    {
        // Rapid arrow-key navigation fires many selections; only the latest may navigate the browser.
        var generation = ++_previewGeneration;
        var item = _viewModel.SelectedItem;
        if (item is null)
        {
            PreviewBrowser.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var core = await EnsureBrowserAsync();
            var showWeb = _viewModel.ShowOriginalWeb && !string.IsNullOrWhiteSpace(item.Entry.Link);

            // Feed HTML is third-party content, so it never gets script. The original site is a real page that
            // needs script to render at all, which is the point of that mode.
            core.Settings.IsScriptEnabled = showWeb;
            _articleMode = !showWeb;

            if (showWeb)
            {
                core.Navigate(item.Entry.Link);
            }
            else
            {
                var html = await _viewModel.BuildPreviewHtmlAsync(item);
                if (generation != _previewGeneration)
                {
                    return;
                }

                var previous = _previewFile;
                Directory.CreateDirectory(Shell.TempDirectory);

                // A temp file instead of NavigateToString, which caps content at 2 MB.
                _previewFile = Path.Combine(Shell.TempDirectory, $"feed-preview-{Guid.NewGuid():N}.html");
                await File.WriteAllTextAsync(_previewFile, html, new UTF8Encoding(false));
                core.Navigate(new Uri(_previewFile).AbsoluteUri);
                TryDelete(previous);
            }

            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewBrowser.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Feed preview failed.", ex);
            PreviewPlaceholder.Text = $"No se pudo previsualizar:\n{ex.Message}";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewBrowser.Visibility = Visibility.Collapsed;
        }
    }

    private async System.Threading.Tasks.Task<CoreWebView2> EnsureBrowserAsync()
    {
        if (_browserReady)
        {
            return PreviewBrowser.CoreWebView2;
        }

        await PreviewBrowser.EnsureCoreWebView2Async(await _viewModel.Shell.WebView.GetAsync());
        var core = PreviewBrowser.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;

        // The browser's own menu (Save as, Inspect…) is never wanted here. With default menus off the event may not fire,
        // so they stay on and every request is handled: our menu in article mode, nothing on the original web.
        core.ContextMenuRequested += (_, e) =>
        {
            e.Handled = true;
            if (_articleMode)
            {
                ShowCssMenu();
            }
        };

        // A link clicked inside the article preview would navigate the pane away from the article with no way back.
        core.NavigationStarting += (_, e) =>
        {
            // Only clicks: embedded iframes also raise NavigationStarting and are not links the user followed.
            if (_articleMode && e.IsUserInitiated && !e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                _viewModel.Shell.OpenWithShell(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _viewModel.Shell.OpenWithShell(e.Uri);
        };

        _browserReady = true;
        return core;
    }

    /// <summary>Right-click on the preview: choose the stylesheet the article is rendered (and printed) with.</summary>
    private void ShowCssMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Estilo del artículo (CSS)", IsEnabled = false, FontWeight = FontWeights.SemiBold });

        void Add(string header, string? value)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = _viewModel.CssOverride == value,
            };
            item.Click += (_, _) => _viewModel.SetCss(value);
            menu.Items.Add(item);
        }

        Add($"Por defecto ({_viewModel.DefaultCssLabel})", null);
        Add("Solo el CSS del perfil", "");
        menu.Items.Add(new System.Windows.Controls.Separator());
        foreach (var css in _viewModel.ListCssFiles())
        {
            Add(css, css);
        }

        menu.IsOpen = true;
    }

    private void DeletePreviewFile()
    {
        TryDelete(_previewFile);
        _previewFile = null;
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Left for the startup temp sweep.
        }
    }
}
