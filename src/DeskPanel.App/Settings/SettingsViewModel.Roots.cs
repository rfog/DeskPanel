using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.Core.Configuration;
using Microsoft.Win32;

namespace DeskPanel.App.Settings;

/// <summary>One alias → folder pair of this machine. Observable so "Elegir…" refreshes the grid cell.</summary>
internal sealed partial class RootRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _path = "";
}

/// <summary>
/// The "Carpetas base" section of the General tab: the alias map stored in machine.json. Aliases are what make
/// deskpanel.json and the search indexes portable, so the rules here (valid names, absolute paths, warning before
/// removing an alias that something still uses) protect every other setting.
/// </summary>
internal sealed partial class SettingsViewModel
{
    private static readonly Regex AliasPattern = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    [ObservableProperty]
    private RootRow? _selectedRoot;

    [ObservableProperty]
    private string _rootTestResult = "";

    public ObservableCollection<RootRow> Roots { get; } = new();

    /// <summary>
    /// A resolver built from what is in the grid right now, not from what is saved: folders chosen in other tabs of
    /// this same dialog must already convert to an alias that has just been added here.
    /// </summary>
    private PathResolver CurrentPaths => new(RootsAsDictionary());

    private void LoadRoots(MachineConfig machine)
    {
        foreach (var (name, path) in machine.Roots.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            Roots.Add(new RootRow { Name = name, Path = path });
        }
    }

    // People type "{Synology}" as often as "Synology"; the braces are syntax, not part of the name.
    private static string CleanAlias(string name) => name.Trim().Trim('{', '}').Trim();

    private Dictionary<string, string> RootsAsDictionary()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Roots)
        {
            var name = CleanAlias(row.Name);
            if (name.Length > 0 && !string.IsNullOrWhiteSpace(row.Path))
            {
                map.TryAdd(name, row.Path.Trim());
            }
        }

        return map;
    }

    private static bool SameRoots(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && string.Equals(other, kv.Value, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void AddRoot()
    {
        var name = "Cloud";
        for (var i = 2; Roots.Any(r => string.Equals(CleanAlias(r.Name), name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = "Alias" + i;
        }

        var row = new RootRow { Name = name };
        Roots.Add(row);
        SelectedRoot = row;
    }

    [RelayCommand]
    private void RemoveRoot()
    {
        if (SelectedRoot is { } row)
        {
            Roots.Remove(row);
        }
    }

    [RelayCommand]
    private void BrowseRootPath()
    {
        var row = SelectedRoot;
        if (row is null)
        {
            AddRoot();
            row = SelectedRoot!;
        }

        var dialog = new OpenFolderDialog { Title = $"Carpeta que representa a {{{CleanAlias(row.Name)}}} en este equipo" };
        var current = Environment.ExpandEnvironmentVariables(row.Path.Trim());
        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog() == true)
        {
            // A bare drive root comes back as "D:\"; the trailing separator would only make every alias path look odd.
            row.Path = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        }
    }

    /// <summary>Lets the user see how a folder would be stored, before relying on it in an index or a setting.</summary>
    [RelayCommand]
    private void TestFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Elige una carpeta para ver cómo se guardaría" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var stored = CurrentPaths.MakePortable(dialog.FolderName);
        RootTestResult = stored.StartsWith('{')
            ? $"{dialog.FolderName}\n   se guarda como   {stored}"
            : $"{dialog.FolderName}\n   no cae bajo ningún alias: se guardaría la ruta completa, que no sirve en otros equipos.";
    }

    /// <summary>The first problem with the alias list, or null if it is fine.</summary>
    private string? ValidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Roots.Where(r => !string.IsNullOrWhiteSpace(r.Name) || !string.IsNullOrWhiteSpace(r.Path)))
        {
            var name = CleanAlias(row.Name);
            if (name.Length == 0)
            {
                return "Hay una carpeta base sin nombre de alias.";
            }

            if (!AliasPattern.IsMatch(name))
            {
                return $"El alias «{name}» solo puede tener letras, números y guion bajo (sin espacios).";
            }

            if (!seen.Add(name))
            {
                return $"El alias «{name}» está repetido.";
            }

            if (string.IsNullOrWhiteSpace(row.Path))
            {
                return $"Falta la carpeta del alias «{name}».";
            }

            if (!Path.IsPathRooted(Environment.ExpandEnvironmentVariables(row.Path.Trim())))
            {
                return $"La carpeta del alias «{name}» debe ser una ruta completa (por ejemplo D:\\Carpeta).";
            }
        }

        return null;
    }

    /// <summary>Asks before saving changes that would leave existing settings unresolvable; false = the user backed out.</summary>
    private bool ConfirmRootChanges()
    {
        var previous = _shell.Config.Machine.Roots;
        var updated = RootsAsDictionary();
        if (SameRoots(previous, updated))
        {
            return true;
        }

        var problems = new StringBuilder();

        foreach (var removed in previous.Keys.Where(k => !updated.ContainsKey(k)))
        {
            var users = ConfiguredPaths()
                .Where(p => p.Value is not null && Regex.IsMatch(p.Value, @"^\s*\{" + Regex.Escape(removed) + @"\}", RegexOptions.IgnoreCase))
                .Select(p => p.Label)
                .ToList();
            if (users.Count > 0)
            {
                problems.AppendLine($"• {{{removed}}} se quita o se renombra, y lo usan: {string.Join(", ", users)}.");
            }
        }

        foreach (var (name, path) in updated)
        {
            var changed = !previous.TryGetValue(name, out var old) || !string.Equals(old, path, StringComparison.OrdinalIgnoreCase);
            if (changed && !Directory.Exists(Environment.ExpandEnvironmentVariables(path)))
            {
                problems.AppendLine($"• La carpeta de {{{name}}} no existe ahora mismo: {path}");
            }
        }

        if (problems.Length == 0)
        {
            return true;
        }

        var answer = MessageBox.Show(
            "Revisa esto antes de guardar las carpetas base:\n\n" + problems +
            "\nLas rutas que usen un alias que ya no existe dejarán de resolverse en este equipo.\n¿Guardar igualmente?",
            "DeskPanel", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>Every path setting, as shown in this dialog or (for those without a field here) as saved.</summary>
    private IEnumerable<(string Label, string? Value)> ConfiguredPaths()
    {
        var app = _shell.Config.App;
        yield return ("la carpeta de configuración", ConfigDirectory);
        yield return ("la carpeta de caché", CacheFolder);
        yield return ("la carpeta de PDF", FeedsOutputFolder);
        yield return ("la carpeta de archivo de PDF", FeedsArchiveFolder);
        yield return ("la carpeta de resúmenes de feeds", FeedsSummaryFolder);
        yield return ("los audios sin procesar", AudioInputFolder);
        yield return ("el audio procesado", AudioCleanFolder);
        yield return ("las transcripciones", AudioTranscriptFolder);
        yield return ("los resúmenes", AudioSummaryFolder);
        yield return ("el almacén de índices", IndexStorageFolder);
        yield return ("las herramientas de audio", app.Audio.ToolsFolder);

        foreach (var index in app.Search.Indexes)
        {
            yield return ($"el índice «{index.Name}»", index.BaseRoot);
        }

        foreach (var quick in app.QuickFolders)
        {
            yield return ($"el acceso «{quick.Name}»", quick.Path);
        }
    }
}
