using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DeskPanel.Core.Configuration;
using DeskPanel.Mac.Audio;
using DeskPanel.Mac.Devonthink;
using DeskPanel.Mac.Feeds;
using DeskPanel.Mac.Folders;
using DeskPanel.Mac.Indexes;
using DeskPanel.Mac.Search;
using DeskPanel.Mac.Services;
using DeskPanel.Mac.Settings;

namespace DeskPanel.Mac;

public partial class MainWindow : Window
{
    private readonly MacServices _services = null!;
    private FeedsWindow? _feeds;
    private IndexesWindow? _indexes;
    private IndexSearchWindow? _indexSearch;
    private DevonthinkSearchWindow? _devonthinkSearch;
    private AudioWindow? _audio;
    private FoldersWindow? _folders;
    private SettingsWindow? _settings;
    private bool _closeConfirmed;

    public MainWindow() => InitializeComponent();

    public MainWindow(MacServices services) : this()
    {
        _services = services;
        StatusText.Text = BuildStatus(services.Config);
        Closing += OnClosing;
        FeedsButton.Click += (_, _) => ShowSingleton(ref _feeds, () => new FeedsWindow(_services));
        SearchIndexesButton.Click += (_, _) => ShowSingleton(ref _indexSearch, () => new IndexSearchWindow(_services));
        SearchDevonthinkButton.Click += (_, _) => ShowSingleton(ref _devonthinkSearch, () => new DevonthinkSearchWindow(_services));
        AudioButton.Click += (_, _) => ShowSingleton(ref _audio, () => new AudioWindow(_services));
        FoldersButton.Click += (_, _) => ShowSingleton(ref _folders, () => new FoldersWindow(_services));
        IndexesButton.Click += (_, _) => ShowSingleton(ref _indexes, () => new IndexesWindow(_services));
        LogButton.Click += (_, _) => _services.OpenLog();
        SettingsButton.Click += (_, _) => ShowSingleton(ref _settings, () => new SettingsWindow(_services));
    }

    // Closing the main window closes every window that hangs from it. The only things worth a question are the ones that
    // would be lost: a running audio job (closing cancels it; the original stays in the input folder) and summaries that
    // were edited or generated but never saved. One question covers them all, so the child windows do not ask again.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        var unsaved = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .OfType<FeedSummaryWindow>().Where(w => w.HasUnsavedText).ToList() ?? new List<FeedSummaryWindow>();
        var audioBusy = _audio is { IsLoaded: true, IsBusy: true };
        if (unsaved.Count == 0 && !audioBusy)
        {
            return;
        }

        var problems = new List<string>();
        if (audioBusy)
        {
            problems.Add("hay audios procesándose (se cancelará el proceso; el audio en curso se conserva en la carpeta de entrada)");
        }

        if (unsaved.Count > 0)
        {
            problems.Add(unsaved.Count == 1 ? "hay un resumen sin guardar" : $"hay {unsaved.Count} resúmenes sin guardar");
        }

        e.Cancel = true;
        if (await Dialogs.ConfirmAsync(this, "Si cierras DeskPanel: " + string.Join(" y ", problems) + ". ¿Cerrar de todos modos?", "Cerrar"))
        {
            foreach (var window in unsaved)
            {
                window.SkipCloseQuestion();
            }

            _closeConfirmed = true;
            Close();
        }
    }

    // A second click brings the existing window forward instead of opening a duplicate.
    private void ShowSingleton<T>(ref T? window, Func<T> create) where T : Window
    {
        if (window is null || !window.IsLoaded)
        {
            var created = create();
            window = created;
            created.Show(this);
        }
        else
        {
            window.Activate();
        }
    }

    private static string BuildStatus(ConfigService config)
    {
        var chrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        var text = new StringBuilder();
        text.AppendLine($"Configuración: {config.AppConfigPath}");
        text.AppendLine($"Chrome: {(File.Exists(chrome) ? "encontrado" : "no encontrado")}");
        text.Append($"Perfiles de PDF: {config.App.Feeds.Profiles.Count}");
        return text.ToString();
    }
}
