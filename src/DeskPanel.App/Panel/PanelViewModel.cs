using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.App.Audio;
using DeskPanel.App.Feeds;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.App.Panel;

internal sealed partial class PanelActionItem : ObservableObject
{
    public PanelActionItem(IPanelAction action, Shell shell)
    {
        _shell = shell;
        Action = action;
        Command = new AsyncRelayCommand(async () =>
        {
            // Hide first: most actions open another window, and the panel would otherwise sit on top of it.
            shell.HidePanel();
            try
            {
                await action.ExecuteAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Panel action '{action.Title}' failed.", ex);
                throw;
            }
        }, () => action.IsEnabled);
    }

    public IPanelAction Action { get; }
    public IAsyncRelayCommand Command { get; }

    /// <summary>Whether the button is a drop target at all (the drag-over handler then asks per drop).</summary>
    public bool AcceptsFiles => Action is IFileDropAction { AcceptsFiles: true };

    public bool CanAccept(IReadOnlyList<string> paths) => Action is IFileDropAction drop && drop.CanAccept(paths);

    public async Task DropAsync(IReadOnlyList<string> paths)
    {
        if (Action is not IFileDropAction drop)
        {
            return;
        }

        _shell.HidePanel();
        try
        {
            await drop.OnFilesDroppedAsync(paths);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Panel action '{Action.Title}' failed on drop.", ex);
            throw;
        }
    }

    private readonly Shell _shell;
}

internal sealed partial class PanelViewModel : ObservableObject
{
    private readonly Shell _shell;

    [ObservableProperty]
    private string _folderFilter = "";

    public PanelViewModel(Shell shell)
    {
        _shell = shell;

        Actions = new ObservableCollection<PanelActionItem>(new IPanelAction[]
        {
            new DelegatePanelAction("Feeds a PDF", "\uE7BC", "Entradas no leídas y generación de PDF", shell.OpenFeeds),
            new DelegatePanelAction("Abrir PDFs", "", "Lista de los PDF generados: abrirlos para anotarlos o resumirlos", shell.OpenPdfList),
            new DelegatePanelAction("Buscar", "\uE721", "Búsqueda en los índices", shell.OpenSearch),
            new DelegatePanelAction("Índices", "\uE8F1", "Crear y actualizar índices", shell.OpenIndexManager),
            new DelegatePanelAction("Verificar", "", "Verificar ficheros de carpetas monitorizadas", shell.OpenFileVerifier),
            new DelegatePanelAction("Audio", "\uE720", "Limpiar, transcribir y resumir audios. Suelta aquí un audio para añadirlo", shell.OpenAudio,
                accepts: paths => AudioImporter.HasAudio(paths), onDrop: paths => shell.ImportAudioAsync(paths)),
            new DelegatePanelAction("Archivar PDF", "", "Mueve los PDF de feeds a la carpeta de archivo (año\\mes) y actualiza su índice",
                () => FeedArchiver.RunAsync(shell)),
            new DelegatePanelAction("Web remota", "", "Abre en el navegador la interfaz web de otro DeskPanel (IP en Configurar › Web)",
                shell.OpenRemoteWeb),
            new DelegatePanelAction("Acerca de", "", "Quién hizo qué (y quién se lleva los beneficios)", shell.OpenAbout),
            new DelegatePanelAction("Configurar", "\uE713", "Atajo, feeds, audio e \u00EDndices", shell.OpenSettings),
        }.Select(a => new PanelActionItem(a, shell)));

        FoldersView = CollectionViewSource.GetDefaultView(Folders);
        FoldersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FolderEntry.Category)));
        FoldersView.Filter = MatchesFilter;
        Refresh();
    }

    public Shell Shell => _shell;

    public bool KeepOpenOnFocusLoss => _shell.Config.App.Panel.KeepOpenOnFocusLoss;

    public ObservableCollection<PanelActionItem> Actions { get; }
    public ObservableCollection<FolderEntry> Folders { get; } = new();
    public ICollectionView FoldersView { get; }

    public void Refresh()
    {
        Folders.Clear();
        foreach (var entry in FolderCatalog.Build(_shell.Config))
        {
            Folders.Add(entry);
        }
    }

    [RelayCommand]
    private void OpenFolder(FolderEntry? entry)
    {
        if (entry?.ResolvedPath is null)
        {
            return;
        }

        _shell.HidePanel();
        _shell.OpenFolder(entry.ResolvedPath);
    }

    partial void OnFolderFilterChanged(string value) => FoldersView.Refresh();

    private bool MatchesFilter(object item) =>
        string.IsNullOrWhiteSpace(FolderFilter)
        || item is FolderEntry entry
           && (entry.Name.Contains(FolderFilter, StringComparison.CurrentCultureIgnoreCase)
               || entry.Category.Contains(FolderFilter, StringComparison.CurrentCultureIgnoreCase)
               || entry.DisplayPath.Contains(FolderFilter, StringComparison.CurrentCultureIgnoreCase));
}
