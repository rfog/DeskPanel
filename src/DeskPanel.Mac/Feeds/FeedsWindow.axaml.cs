using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Interactivity;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Feeds;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Feeds;

public partial class FeedsWindow : Window
{
    private const string AllSources = "Todas las fuentes";

    private readonly MacServices _services = null!;
    private readonly FeedReader _reader = null!;
    private readonly ObservableCollection<FeedItem> _items = new();
    private List<FeedEntry> _entries = new();
    private bool _isBusy;

    // The stylesheet picked from the preview's context menu for this session: null = the configured one (Settings),
    // "" = the profile's CSS only, otherwise "css/name.css". It replaces only the custom layer; the profile CSS always applies.
    private string? _cssOverride;
    private int _previewVersion;
    private bool _loadingPreview;

    public FeedsWindow() : this(null!)
    {
    }

    public FeedsWindow(MacServices services)
    {
        InitializeComponent();
        _services = services;
        if (services is null)
        {
            return;
        }

        _reader = new FeedReader(services.Http);
        EntriesList.ItemsSource = _items;

        SourceBox.ItemsSource = new[] { AllSources }.Concat(Config.Sources.Select(s => s.Name)).ToList();
        SourceBox.SelectedIndex = 0;
        ProfileBox.ItemsSource = Config.Profiles.Select(p => p.Name).ToList();
        ProfileBox.SelectedItem = Config.Profiles.Any(p => p.Name == Config.DefaultProfile) ? Config.DefaultProfile : Config.Profiles.FirstOrDefault()?.Name;
        SourceBox.SelectionChanged += (_, _) => ApplyFilter();
        ShowReadBox.IsCheckedChanged += (_, _) => ApplyFilter();
        EntriesList.SelectionChanged += (_, _) => ShowSelection();
        ProfileBox.SelectionChanged += (_, _) => _ = RefreshPreviewAsync();
        PreviewView.NavigationStarted += OnPreviewNavigationStarted;
        PreviewView.NavigationCompleted += (_, _) => _loadingPreview = false;
        PreviewView.NewWindowRequested += OnPreviewNewWindowRequested;
        PreviewView.WebMessageReceived += OnPreviewMessage;

        RefreshButton.Click += async (_, _) => await RefreshAsync();
        SummarizeButton.Click += (_, _) => Summarize();
        PdfButton.Click += async (_, _) => await GeneratePdfAsync();
        TranslateButton.Click += async (_, _) => await TranslateAsync();
        FullButton.Click += async (_, _) => await FetchFullAsync();
        ToggleReadButton.Click += (_, _) => ToggleRead();
        OpenLinkButton.Click += (_, _) => OpenLink();
        OpenFolderButton.Click += (_, _) => OpenOutputFolder();
        ShowSelection();
    }

    private FeedsConfig Config => _services.Config.App.Feeds;

    private string? EffectiveCustomCss => _cssOverride ?? Config.CustomCssFile;

    private FeedItem? Selected => EntriesList.SelectedItem as FeedItem;

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RefreshButton.IsEnabled = !busy;
        TranslateButton.IsEnabled = !busy;
        FullButton.IsEnabled = !busy;
        PdfButton.IsEnabled = !busy;
    }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        StatusText.Text = "Descargando fuentes…";
        var errors = new List<string>();
        try
        {
            // Re-read the state first: it is a file and may have changed since the window opened.
            _services.ReadState.Load();

            var results = await Task.WhenAll(Config.Sources.Select(async source =>
            {
                try
                {
                    return await _reader.ReadAsync(source.Url, source.Name);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Feed '{source.Name}' failed.", ex);
                    lock (errors)
                    {
                        errors.Add($"{source.Name}: {ex.Message}");
                    }

                    return (IReadOnlyList<FeedEntry>)Array.Empty<FeedEntry>();
                }
            }));

            _entries = results.SelectMany(r => r)
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .OrderByDescending(e => e.Published ?? DateTimeOffset.MinValue)
                .ToList();

            ApplyFilter();
            StatusText.Text = errors.Count == 0
                ? $"{_items.Count} entradas mostradas de {_entries.Count}."
                : $"{_items.Count} entradas. Errores: {string.Join(" | ", errors)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyFilter()
    {
        var source = SourceBox.SelectedItem as string ?? AllSources;
        var showRead = ShowReadBox.IsChecked == true;

        _items.Clear();
        foreach (var entry in _entries)
        {
            if (source != AllSources && entry.SourceName != source)
            {
                continue;
            }

            var isRead = _services.ReadState.IsRead(entry.Id);
            if (isRead && !showRead)
            {
                continue;
            }

            _items.Add(new FeedItem(entry, isRead));
        }

        EntriesList.SelectedItem = _items.FirstOrDefault();
    }

    private void ShowSelection()
    {
        var item = Selected;
        var hasItem = item is not null;
        SummarizeButton.IsEnabled = hasItem;
        ToggleReadButton.IsEnabled = hasItem;
        OpenLinkButton.IsEnabled = hasItem && !string.IsNullOrWhiteSpace(item!.Entry.Link);
        TranslateButton.Content = item?.Translation is not null ? "Ver original" : "Traducir";
        FullButton.Content = item?.FullContentHtml is not null ? "Contenido del feed" : "Artículo completo";
        ToggleReadButton.Content = item?.IsRead == true ? "Marcar no leída" : "Marcar leída";

        _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        var item = Selected;
        try
        {
            var profile = Config.Profiles.FirstOrDefault(p => p.Name == ProfileBox.SelectedItem as string);
            var html = item is null
                ? "<!DOCTYPE html><html><body></body></html>"
                : await FeedPreview.BuildAsync(_services, item.ForRender, profile, EffectiveCustomCss);

            // Selecting quickly queues several renders; only the last one is shown.
            if (version != _previewVersion)
            {
                return;
            }

            _loadingPreview = true;
            Uri.TryCreate(item?.Entry.Link, UriKind.Absolute, out var baseUri);
            PreviewView.NavigateToString(html, baseUri!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not build the preview.", ex);
            StatusText.Text = $"No se pudo preparar la vista previa: {ex.Message}";
        }
    }

    // Our own load is allowed; anything else (a click on a link inside the article) goes to the default browser instead.
    private void OnPreviewNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_loadingPreview || e.Request is null || e.Request.Scheme is not ("http" or "https"))
        {
            return;
        }

        e.Cancel = true;
        _services.Opener.OpenUrl(e.Request.AbsoluteUri);
    }

    private void OnPreviewNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { Scheme: "http" or "https" })
        {
            _services.Opener.OpenUrl(e.Request.AbsoluteUri);
        }
    }

    private void OnPreviewMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        if (FeedPreview.ParseContextMenu(e.Body) is { } position)
        {
            Dispatcher.UIThread.Post(() => ShowCssMenu(position.X, position.Y));
        }
    }

    /// <summary>The "Estilo del artículo (CSS)" menu: the configured style, the profile's CSS alone, or any .css of the shared folder.</summary>
    private void ShowCssMenu(double x, double y)
    {
        var configured = string.IsNullOrWhiteSpace(Config.CustomCssFile) ? "ninguno" : Config.CustomCssFile;
        var menu = new ContextMenu { PlacementTarget = PreviewView, Placement = PlacementMode.Bottom, PlacementRect = new Rect(x, y, 1, 1) };

        MenuItem Choice(string header, string? value)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, GroupName = "css", IsChecked = _cssOverride == value };
            item.Click += (_, _) => SetCss(value);
            return item;
        }

        menu.Items.Add(new MenuItem { Header = "Estilo del artículo (CSS)", IsEnabled = false });
        menu.Items.Add(Choice($"Por defecto ({configured})", null));
        menu.Items.Add(Choice("Solo el CSS del perfil", ""));
        menu.Items.Add(new Separator());
        foreach (var css in ListCssFiles())
        {
            menu.Items.Add(Choice(css, css));
        }

        menu.Open(PreviewView);
    }

    private void SetCss(string? css)
    {
        _cssOverride = css;
        StatusText.Text = css is null ? $"Estilo: el configurado ({(string.IsNullOrWhiteSpace(Config.CustomCssFile) ? "ninguno" : Config.CustomCssFile)})."
            : css.Length == 0 ? "Estilo: solo el CSS del perfil." : $"Estilo: {css}.";
        _ = RefreshPreviewAsync();
    }

    /// <summary>The stylesheets of the CSS folder, as "css/name.css" (the form the configuration stores).</summary>
    private List<string> ListCssFiles()
    {
        try
        {
            var directory = Path.GetDirectoryName(_services.ResolveCss("css/x.css"));
            return directory is not null && Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.css").Select(f => "css/" + Path.GetFileName(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not list the css folder.", ex);
            return new List<string>();
        }
    }

    private void Summarize()
    {
        if (Selected is not { } item)
        {
            return;
        }

        // One window per summary, so several can be compared while reading on.
        new FeedSummaryWindow(_services, item.Effective).Show(this);
    }

    private async Task TranslateAsync()
    {
        if (Selected is not { } item || _isBusy)
        {
            return;
        }

        if (item.Translation is not null)
        {
            item.Translation = null;
            StatusText.Text = "Se muestra de nuevo el idioma original.";
            ShowSelection();
            return;
        }

        SetBusy(true);
        StatusText.Text = $"Traduciendo: {item.Entry.Title}";
        try
        {
            var outcome = await FeedTranslator.TranslateAsync(
                _services.Config.App, _services.Secrets, item.Effective, CancellationToken.None, message => StatusText.Text = message);
            if (outcome.Result is not null)
            {
                item.Translation = outcome.Result;
            }

            StatusText.Text = outcome.Status;
        }
        finally
        {
            SetBusy(false);
            ShowSelection();
        }
    }

    private async Task FetchFullAsync()
    {
        if (Selected is not { } item || _isBusy)
        {
            return;
        }

        if (item.FullContentHtml is not null)
        {
            item.FullContentHtml = null;
            StatusText.Text = "Se usa de nuevo el contenido del feed.";
            ShowSelection();
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Entry.Link))
        {
            StatusText.Text = "La entrada no tiene enlace al artículo original.";
            return;
        }

        SetBusy(true);
        StatusText.Text = $"Descargando el artículo completo: {item.Entry.Title}";
        try
        {
            item.FullContentHtml = await new FullArticleFetcher(_services.Http).FetchAsync(item.Entry.Link);
            StatusText.Text = "Artículo completo descargado.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn($"Full article download failed for '{item.Entry.Link}'.", ex);
            StatusText.Text = $"No se pudo descargar el artículo completo: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
            ShowSelection();
        }
    }

    private async Task GeneratePdfAsync()
    {
        if (Selected is not { } item || _isBusy)
        {
            return;
        }

        var profile = Config.Profiles.FirstOrDefault(p => p.Name == ProfileBox.SelectedItem as string);
        if (profile is null)
        {
            StatusText.Text = "No hay ningún perfil de PDF seleccionado (Ajustes › Feeds).";
            return;
        }

        SetBusy(true);
        StatusText.Text = $"Generando PDF: {item.Title}";
        try
        {
            var result = await FeedPdfService.WritePdfAsync(
                _services, item.ForRender, item.Translation is not null, profile, EffectiveCustomCss);

            _services.ReadState.MarkRead(item.Entry.Id);
            _services.ReadState.Save(Config.ReadStateRetentionDays);
            item.IsRead = true;
            if (ShowReadBox.IsChecked != true)
            {
                _items.Remove(item);
            }

            StatusText.Text = result.CachePath is null
                ? $"Generado: {result.OutputPath}"
                : $"Generado: {result.OutputPath} (copia en {result.CachePath})";
        }
        catch (Exception ex)
        {
            AppLog.Error($"PDF generation failed for '{item.Title}'.", ex);
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
            ShowSelection();
        }
    }

    private void ToggleRead()
    {
        if (Selected is not { } item)
        {
            return;
        }

        if (item.IsRead)
        {
            _services.ReadState.MarkUnread(item.Entry.Id);
        }
        else
        {
            _services.ReadState.MarkRead(item.Entry.Id);
        }

        item.IsRead = !item.IsRead;
        _services.ReadState.Save(Config.ReadStateRetentionDays);
        if (item.IsRead && ShowReadBox.IsChecked != true)
        {
            _items.Remove(item);
        }

        ShowSelection();
    }

    private void OpenLink()
    {
        if (Selected?.Entry.Link is { Length: > 0 } link && Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            _services.Opener.OpenUrl(link);
        }
    }

    private void OpenOutputFolder()
    {
        if (_services.Config.Paths.TryResolve(Config.OutputFolder, out var folder))
        {
            Directory.CreateDirectory(folder);
            _services.Opener.OpenFolder(folder);
        }
    }
}
