using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Devonthink;

/// <summary>A stored database with the tick that says whether a search includes it.</summary>
public sealed class SelectableDatabase : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public SelectableDatabase(DevonthinkDatabase database) => Database = database;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DevonthinkDatabase Database { get; }

    public string Name => Database.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>Searches DEVONthink with its own query syntax; records open in DEVONthink through their item link.</summary>
public partial class DevonthinkSearchWindow : Window
{
    private readonly MacServices _services = null!;
    private readonly DevonthinkService _devonthink = null!;
    private readonly ObservableCollection<DevonthinkHit> _results = new();

    public DevonthinkSearchWindow() => InitializeComponent();

    public DevonthinkSearchWindow(MacServices services) : this()
    {
        _services = services;
        _devonthink = new DevonthinkService(services);
        ResultsList.ItemsSource = _results;
        ShowDatabases(services.Config.App.Devonthink.Databases);

        SearchButton.Click += async (_, _) => await SearchAsync();
        QueryBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                await SearchAsync();
            }
        };
        RefreshDatabasesButton.Click += async (_, _) => await RefreshDatabasesAsync();
        OpenButton.Click += (_, _) => OpenSelected();
        ResultsList.DoubleTapped += (_, _) => OpenSelected();

        // Settings may have refreshed the stored list while this window was in the background.
        Activated += (_, _) => ShowDatabases(services.Config.App.Devonthink.Databases);
    }

    private void ShowDatabases(IEnumerable<DevonthinkDatabase> databases)
    {
        var current = DatabasesList.ItemsSource as IEnumerable<SelectableDatabase>;
        var deselected = current?.Where(d => !d.IsSelected).Select(d => d.Database.Uuid).ToHashSet() ?? new HashSet<string>();
        DatabasesList.ItemsSource = databases.Select(d => new SelectableDatabase(d) { IsSelected = !deselected.Contains(d.Uuid) }).ToList();
    }

    private async Task RefreshDatabasesAsync()
    {
        if (!await DevonthinkApp.EnsureRunningAsync(this))
        {
            StatusText.Text = "DEVONthink no está abierto.";
            return;
        }

        RefreshDatabasesButton.IsEnabled = false;
        StatusText.Text = "Leyendo las bases de datos de DEVONthink…";
        try
        {
            var databases = await _devonthink.RefreshDatabasesAsync(CancellationToken.None);
            ShowDatabases(databases);
            StatusText.Text = $"{databases.Count} bases de datos guardadas.";
        }
        catch (McpException ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            RefreshDatabasesButton.IsEnabled = true;
        }
    }

    private async Task SearchAsync()
    {
        var query = (QueryBox.Text ?? "").Trim();
        if (query.Length == 0)
        {
            return;
        }

        // Every stored database ticked means "all open ones", which also covers databases opened since the last read.
        var all = (DatabasesList.ItemsSource as IEnumerable<SelectableDatabase> ?? []).ToList();
        var selected = all.Where(d => d.IsSelected).Select(d => d.Database.Uuid).ToList();
        if (all.Count > 0 && selected.Count == 0)
        {
            StatusText.Text = "Selecciona al menos una base de datos.";
            return;
        }

        if (!await DevonthinkApp.EnsureRunningAsync(this))
        {
            StatusText.Text = "DEVONthink no está abierto.";
            return;
        }

        SearchButton.IsEnabled = false;
        StatusText.Text = "Buscando…";
        try
        {
            var result = await _devonthink.SearchAsync(query, selected.Count == all.Count ? null : selected, CancellationToken.None);
            _results.Clear();
            foreach (var hit in result.Hits)
            {
                _results.Add(hit);
            }

            StatusText.Text = result.Total > result.Hits.Count
                ? $"{result.Hits.Count} de {result.Total} resultados"
                : $"{result.Total} resultados";
            ResultsList.SelectedItem = _results.FirstOrDefault();
        }
        catch (McpException ex)
        {
            AppLog.Warn("DEVONthink search failed.", ex);
            StatusText.Text = ex.Message;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or TimeoutException)
        {
            AppLog.Error("DEVONthink search failed.", ex);
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void OpenSelected()
    {
        if (ResultsList.SelectedItem is DevonthinkHit hit)
        {
            _services.Opener.OpenUrl(hit.ItemUrl);
        }
    }
}
