using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;
using DeskPanel.Feeds;

namespace DeskPanel.App.Feeds;

internal sealed partial class FeedItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isRead;

    public FeedItemViewModel(FeedEntry entry, bool isRead)
    {
        Entry = entry;
        _isRead = isRead;
    }

    public FeedEntry Entry { get; }

    /// <summary>The article downloaded from the original page (sanitized); null while only the feed's own content is used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Effective), nameof(HasFullContent))]
    private string? _fullContentHtml;

    public bool HasFullContent => FullContentHtml is not null;

    /// <summary>The entry with the full article when one was downloaded, in its original language: what a summary works from.</summary>
    public FeedEntry Effective => FullContentHtml is null ? Entry : Entry with { ContentHtml = FullContentHtml };

    /// <summary>The Spanish version of <see cref="Effective"/>, with its closing note; null until the entry is translated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForRender), nameof(HasTranslation))]
    private FeedTranslation? _translation;

    public bool HasTranslation => Translation is not null;

    /// <summary>What the preview and the PDF show: the translation if there is one, otherwise <see cref="Effective"/>.</summary>
    public FeedEntry ForRender => Translation?.Entry ?? Effective;

    // A translation belongs to the text it was made from; a different text (full article on/off) makes it stale.
    partial void OnFullContentHtmlChanged(string? value) => Translation = null;

    public string Meta
    {
        get
        {
            var date = Entry.Published?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "sin fecha";
            return string.IsNullOrWhiteSpace(Entry.Author)
                ? $"{Entry.SourceName} · {date}"
                : $"{Entry.SourceName} · {Entry.Author} · {date}";
        }
    }
}

internal sealed partial class FeedsViewModel : ObservableObject
{
    public const string AllSources = "Todas las fuentes";

    private readonly Shell _shell;
    private readonly FeedReader _reader;
    private List<FeedEntry> _entries = new();

    [ObservableProperty]
    private string _selectedSource = AllSources;

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private FeedItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _showOriginalWeb;

    [ObservableProperty]
    private bool _showRead;

    [ObservableProperty]
    private bool _openPreviewAfterGenerate;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Pulsa Actualizar para descargar las fuentes.";

    public FeedsViewModel(Shell shell)
    {
        _shell = shell;
        _reader = new FeedReader(shell.Http);

        var feeds = shell.Config.App.Feeds;
        Sources.Add(AllSources);
        foreach (var source in feeds.Sources)
        {
            Sources.Add(source.Name);
        }

        foreach (var profile in feeds.Profiles)
        {
            Profiles.Add(profile.Name);
        }

        SelectedProfile = Profiles.Contains(feeds.DefaultProfile) ? feeds.DefaultProfile : Profiles.FirstOrDefault();
        OpenPreviewAfterGenerate = feeds.OpenPreviewAfterGenerate;
    }

    public ObservableCollection<FeedItemViewModel> Items { get; } = new();
    public ObservableCollection<string> Sources { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();

    private FeedsConfig Config => _shell.Config.App.Feeds;

    internal Shell Shell => _shell;

    /// <summary>
    /// The stylesheet picked from the preview's context menu for this session: null = the configured one
    /// (Settings › Feeds), "" = none (profile CSS only), otherwise a "css/name.css" path relative to the config directory.
    /// It replaces only the custom layer; the profile's own CSS always applies.
    /// </summary>
    [ObservableProperty]
    private string? _cssOverride;

    private string? EffectiveCustomCss => CssOverride is null ? Config.CustomCssFile : CssOverride;

    /// <summary>What "by default" means right now, for the menu.</summary>
    public string DefaultCssLabel => string.IsNullOrWhiteSpace(Config.CustomCssFile) ? "ninguno" : Config.CustomCssFile!;

    /// <summary>The stylesheets in the config directory's css folder, as "css/name.css".</summary>
    public IReadOnlyList<string> ListCssFiles()
    {
        try
        {
            var directory = Path.GetDirectoryName(_shell.Config.ResolveConfigRelative("css/x.css"));
            return directory is not null && Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.css").Select(f => "css/" + Path.GetFileName(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not list the css folder.", ex);
            return Array.Empty<string>();
        }
    }

    public void SetCss(string? css)
    {
        CssOverride = css;
        Status = css is null ? $"Estilo: el configurado ({DefaultCssLabel})."
            : css.Length == 0 ? "Estilo: solo el CSS del perfil." : $"Estilo: {css}.";
    }

    public Task<string> BuildPreviewHtmlAsync(FeedItemViewModel item) =>
        FeedRendering.BuildPreviewHtmlAsync(_shell, item.ForRender, FeedRendering.FindProfile(Config, SelectedProfile), EffectiveCustomCss);

    /// <summary>Raised when the content of the selected entry changes (full article downloaded), so the preview reloads.</summary>
    public event Action<FeedItemViewModel>? ContentChanged;

    [RelayCommand]
    private void Summarize(FeedItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // Each summary gets its own window, so several can be compared or kept open while reading on.
        var window = new FeedSummaryWindow(new FeedSummaryViewModel(_shell, item.Effective))
        {
            Owner = System.Windows.Application.Current.Windows.OfType<FeedsWindow>().FirstOrDefault(),
        };
        window.Show();
    }

    [RelayCommand]
    private async Task TranslateAsync(FeedItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        if (item.HasTranslation)
        {
            // Second click goes back to the original language.
            item.Translation = null;
            Status = "Se muestra de nuevo el idioma original.";
            ContentChanged?.Invoke(item);
            return;
        }

        IsBusy = true;
        Status = $"Traduciendo: {item.Entry.Title}";
        try
        {
            var outcome = await FeedTranslator.TranslateAsync(_shell, item.Effective, CancellationToken.None, message => Status = message);
            if (outcome.Result is not null)
            {
                item.Translation = outcome.Result;
                ContentChanged?.Invoke(item);
            }

            Status = outcome.Status + (outcome.Result is null ? "" : " Genera el PDF para guardarlo.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FetchFullAsync(FeedItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        if (item.HasFullContent)
        {
            // Second click goes back to what the feed itself carries.
            item.FullContentHtml = null;
            Status = "Se usa de nuevo el contenido del feed.";
            ContentChanged?.Invoke(item);
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Entry.Link))
        {
            Status = "La entrada no tiene enlace al artículo original.";
            return;
        }

        IsBusy = true;
        Status = $"Descargando el artículo completo: {item.Entry.Title}";
        try
        {
            item.FullContentHtml = await new FullArticleFetcher(_shell.Http).FetchAsync(item.Entry.Link);
            Status = "Artículo completo descargado. Genera el PDF para guardarlo.";
            ContentChanged?.Invoke(item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn($"Full article download failed for '{item.Entry.Link}'.", ex);
            Status = $"No se pudo descargar el artículo completo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Descargando fuentes…";
        var errors = new ConcurrentBag<string>();

        try
        {
            // Re-read the synced state first: another machine may have generated PDFs since the window opened.
            _shell.ReadState.Load();

            var results = await Task.WhenAll(Config.Sources.Select(async source =>
            {
                try
                {
                    return await _reader.ReadAsync(source.Url, source.Name);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Feed '{source.Name}' failed.", ex);
                    errors.Add($"{source.Name}: {ex.Message}");
                    return (IReadOnlyList<FeedEntry>)Array.Empty<FeedEntry>();
                }
            }));

            _entries = results.SelectMany(r => r)
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .OrderByDescending(e => e.Published ?? DateTimeOffset.MinValue)
                .ToList();

            ApplyFilter();
            Status = errors.IsEmpty
                ? $"{Items.Count} entradas mostradas de {_entries.Count}."
                : $"{Items.Count} entradas. Errores: {string.Join(" | ", errors)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GeneratePdfAsync(FeedItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var profile = Config.Profiles.FirstOrDefault(p => p.Name == SelectedProfile);
        if (profile is null)
        {
            Status = "No hay ningún perfil PDF seleccionado.";
            return;
        }

        IsBusy = true;
        Status = $"Generando PDF: {item.Entry.Title}";
        try
        {
            var target = await FeedRendering.WritePdfAsync(_shell, item.ForRender, profile, EffectiveCustomCss);

            _shell.ReadState.MarkRead(item.Entry.Id);
            _shell.ReadState.Save(Config.ReadStateRetentionDays);
            item.IsRead = true;
            if (!ShowRead)
            {
                Items.Remove(item);
            }

            Status = $"Generado: {target}";
            if (OpenPreviewAfterGenerate)
            {
                _shell.OpenPdfPreview(target);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"PDF generation failed for '{item.Entry.Title}'.", ex);
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleRead(FeedItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsRead)
        {
            _shell.ReadState.MarkUnread(item.Entry.Id);
        }
        else
        {
            _shell.ReadState.MarkRead(item.Entry.Id);
        }

        item.IsRead = !item.IsRead;
        _shell.ReadState.Save(Config.ReadStateRetentionDays);
        if (item.IsRead && !ShowRead)
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void OpenInBrowser(FeedItemViewModel? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Entry.Link))
        {
            _shell.OpenWithShell(item.Entry.Link);
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (_shell.Config.Paths.TryResolve(Config.OutputFolder, out var folder))
        {
            _shell.OpenFolder(folder);
        }
    }

    partial void OnSelectedSourceChanged(string value) => ApplyFilter();

    partial void OnShowReadChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var entry in _entries)
        {
            if (SelectedSource != AllSources && entry.SourceName != SelectedSource)
            {
                continue;
            }

            var isRead = _shell.ReadState.IsRead(entry.Id);
            if (isRead && !ShowRead)
            {
                continue;
            }

            Items.Add(new FeedItemViewModel(entry, isRead));
        }

        SelectedItem = Items.FirstOrDefault();
    }
}
