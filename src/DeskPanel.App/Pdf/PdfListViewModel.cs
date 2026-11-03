using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.App.Pdf;

internal sealed record PdfFileItem(string FullPath, string Name, DateTime Modified, long Size)
{
    public string Meta => $"{Modified:dd/MM/yyyy HH:mm}  ·  {(Size < 1024 * 1024 ? $"{Math.Max(1, Size / 1024)} KB" : $"{Size / 1024.0 / 1024.0:0.0} MB")}";
}

/// <summary>The PDFs in the feeds output folder, to open (and annotate) or summarise one.</summary>
internal sealed partial class PdfListViewModel : ObservableObject
{
    private readonly Shell _shell;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private PdfFileItem? _selected;

    [ObservableProperty]
    private string _status = "";

    public PdfListViewModel(Shell shell)
    {
        _shell = shell;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => string.IsNullOrWhiteSpace(Filter)
                           || o is PdfFileItem item && item.Name.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase);
        Refresh();
    }

    public ObservableCollection<PdfFileItem> Items { get; } = new();

    public ICollectionView View { get; }

    partial void OnFilterChanged(string value) => View.Refresh();

    [RelayCommand]
    public void Refresh()
    {
        var selectedPath = Selected?.FullPath;
        Items.Clear();

        if (!_shell.Config.Paths.TryResolve(_shell.Config.App.Feeds.OutputFolder, out var folder) || !Directory.Exists(folder))
        {
            Status = "La carpeta de PDF generados no existe (Configurar › Feeds).";
            return;
        }

        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.pdf").OrderByDescending(f => f.LastWriteTime))
            {
                Items.Add(new PdfFileItem(file.FullName, Path.GetFileNameWithoutExtension(file.Name), file.LastWriteTime, file.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not list the PDF folder.", ex);
            Status = $"No se pudo leer la carpeta: {ex.Message}";
            return;
        }

        Status = $"{Items.Count} PDF en {folder}";
        Selected = Items.FirstOrDefault(i => i.FullPath == selectedPath);
    }

    [RelayCommand]
    private void Open(PdfFileItem? item)
    {
        if (item is not null)
        {
            _shell.OpenPdfPreview(item.FullPath);
        }
    }

    [RelayCommand]
    private void Summarize(PdfFileItem? item)
    {
        if (item is not null)
        {
            PdfSummary.Open(_shell, item.FullPath);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_shell.Config.Paths.TryResolve(_shell.Config.App.Feeds.OutputFolder, out var folder))
        {
            _shell.OpenFolder(folder);
        }
    }
}
