using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Configuration;

namespace DeskPanel.App.Settings;

/// <summary>One line of the "Atajos" tab: a global shortcut that can be switched off and rebound.</summary>
internal sealed partial class SystemKeyRow : ObservableObject
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _text = "";

    public SystemKeyRow(string title, string description, Func<AppConfig, SystemKeyConfig> select, string defaultKey)
    {
        Title = title;
        Description = description;
        Select = select;
        DefaultKey = defaultKey;
    }

    public string Title { get; }
    public string Description { get; }
    public Func<AppConfig, SystemKeyConfig> Select { get; }
    public string DefaultKey { get; }

    public ModifierKeys Modifiers { get; private set; }
    public Key Key { get; private set; }

    public void SetChord(ModifierKeys modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
        Text = SettingsViewModel.FormatChord(modifiers, key);
    }

    public bool HasValidChord => Key != Key.None && Modifiers != ModifierKeys.None;
}

/// <summary>The "Atajos" tab. Stored in deskpanel.json (shared): the same shortcuts on every PC.</summary>
internal sealed partial class SettingsViewModel
{
    public ObservableCollection<SystemKeyRow> SystemKeyRows { get; } = new()
    {
        new SystemKeyRow("Subir volumen", "Sube el volumen del sistema (se repite al mantener pulsado).", a => a.SystemKeys.VolumeUp, "Up"),
        new SystemKeyRow("Bajar volumen", "Baja el volumen del sistema (se repite al mantener pulsado).", a => a.SystemKeys.VolumeDown, "Down"),
        new SystemKeyRow("Silenciar", "Activa o quita el silencio.", a => a.SystemKeys.Mute, "M"),
        new SystemKeyRow("Apagar pantallas", "Apaga los monitores sin bloquear el equipo; se encienden al mover el ratón o pulsar una tecla.", a => a.SystemKeys.ScreenOff, "H"),
    };

    private void LoadSystemKeys(AppConfig app)
    {
        foreach (var row in SystemKeyRows)
        {
            var config = row.Select(app);
            row.Enabled = config.Enabled;
            var modifiers = Enum.TryParse<ModifierKeys>(config.Modifiers, ignoreCase: true, out var m) ? m : ModifierKeys.None;
            var key = Enum.TryParse<Key>(config.Key, ignoreCase: true, out var k) ? k : Key.None;
            row.SetChord(modifiers, key);
        }
    }

    private void ApplySystemKeys(AppConfig app)
    {
        foreach (var row in SystemKeyRows)
        {
            var config = row.Select(app);
            config.Enabled = row.Enabled;
            config.Modifiers = row.Modifiers.ToString();
            config.Key = row.Key.ToString();
        }
    }

    /// <summary>The first problem with the shortcuts that are switched on, or null.</summary>
    private string? ValidateSystemKeys()
    {
        var active = SystemKeyRows.Where(r => r.Enabled).ToList();
        if (active.FirstOrDefault(r => !r.HasValidChord) is { } incomplete)
        {
            return $"El atajo «{incomplete.Title}» necesita al menos un modificador (Ctrl, Alt, Mayús o Win) y una tecla.";
        }

        // Two actions on the same chord: the second registration would fail at start-up, so say it now.
        foreach (var group in active.GroupBy(r => (r.Modifiers, r.Key)).Where(g => g.Count() > 1))
        {
            return $"«{group.First().Title}» y «{group.Last().Title}» usan el mismo atajo ({group.Key.Modifiers} + {group.Key.Key}).";
        }

        if (active.FirstOrDefault(r => r.Modifiers == _hotKeyModifiers && r.Key == _hotKeyKey) is { } clash)
        {
            return $"«{clash.Title}» usa el mismo atajo que el panel ({FormatChord(_hotKeyModifiers, _hotKeyKey)}).";
        }

        return null;
    }

    [RelayCommand]
    private void RestoreSystemKeys()
    {
        foreach (var row in SystemKeyRows)
        {
            row.Enabled = true;
            row.SetChord(ModifierKeys.Control | ModifierKeys.Alt, Enum.Parse<Key>(row.DefaultKey));
        }
    }
}
