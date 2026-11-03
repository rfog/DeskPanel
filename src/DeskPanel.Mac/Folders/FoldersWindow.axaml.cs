using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Folders;

/// <summary>One line of the list: a category header or a folder.</summary>
public sealed class FolderRow
{
    public FolderRow(string title) => Title = title;

    public FolderRow(FolderEntry entry)
    {
        Entry = entry;
        Title = entry.Name;
        Path = entry.DisplayPath;
    }

    public FolderEntry? Entry { get; }

    public string Title { get; }

    public string Path { get; } = "";

    public bool IsEntry => Entry is not null;

    public FontWeight TitleWeight => IsEntry ? FontWeight.Normal : FontWeight.Bold;

    // A folder that does not exist is shown dimmed, so a stale path is easy to spot.
    public double TitleOpacity => Entry is { Exists: false } ? 0.45 : 1.0;

    public Thickness TitleMargin => IsEntry ? new Thickness(12, 0, 0, 0) : new Thickness(0, 6, 0, 0);
}

/// <summary>Jump list of every folder the app uses; Enter or a double click opens it in Finder.</summary>
public partial class FoldersWindow : Window
{
    private readonly MacServices _services = null!;
    private List<FolderEntry> _entries = new();

    public FoldersWindow() => InitializeComponent();

    public FoldersWindow(MacServices services) : this()
    {
        _services = services;
        FilterBox.TextChanged += (_, _) => Fill();
        OpenButton.Click += (_, _) => OpenSelected();
        FolderList.DoubleTapped += (_, _) => OpenSelected();
        FolderList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenSelected();
            }
        };

        // Settings may have changed the folders while this window was in the background.
        Activated += (_, _) => Reload();
        Reload();
        FilterBox.Focus();
    }

    private void Reload()
    {
        _entries = FolderCatalog.Build(_services.Config);
        Fill();
    }

    private void Fill()
    {
        var filter = (FilterBox.Text ?? "").Trim();
        var rows = new List<FolderRow>();
        foreach (var group in _entries
                     .Where(e => filter.Length == 0
                                 || e.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                 || e.Category.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                 || e.DisplayPath.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                     .GroupBy(e => e.Category))
        {
            rows.Add(new FolderRow(group.Key));
            rows.AddRange(group.Select(e => new FolderRow(e)));
        }

        FolderList.ItemsSource = rows;
        FolderList.SelectedItem = rows.FirstOrDefault(r => r.IsEntry);
        StatusText.Text = rows.Count(r => r.IsEntry) == 0 ? "Nada que mostrar. Revisa Ajustes." : "";
    }

    private void OpenSelected()
    {
        if (FolderList.SelectedItem is not FolderRow { Entry: { } entry })
        {
            return;
        }

        if (entry.ResolvedPath is null)
        {
            StatusText.Text = $"No se puede resolver la ruta: {entry.ConfiguredPath}";
        }
        else if (!entry.Exists)
        {
            StatusText.Text = $"La carpeta no existe: {entry.ResolvedPath}";
        }
        else
        {
            _services.Opener.OpenFolder(entry.ResolvedPath);
        }
    }
}
