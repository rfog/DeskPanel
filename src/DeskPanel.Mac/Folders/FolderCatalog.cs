using DeskPanel.Core.Configuration;

namespace DeskPanel.Mac.Folders;

public sealed record FolderEntry(string Category, string Name, string ConfiguredPath, string? ResolvedPath)
{
    public bool Exists => ResolvedPath is not null && Directory.Exists(ResolvedPath);

    public string DisplayPath => ResolvedPath ?? $"{ConfiguredPath} (no se puede resolver)";
}

/// <summary>
/// Builds the jump list from the configuration itself, so every folder a feature writes to is reachable without
/// having to repeat it under QuickFolders. Same idea as the Windows panel's list, with the Mac-only folders added.
/// </summary>
public static class FolderCatalog
{
    public static List<FolderEntry> Build(ConfigService config)
    {
        var app = config.App;
        var entries = new List<FolderEntry>();

        void Add(string category, string name, string? configured)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                return;
            }

            entries.Add(new FolderEntry(category, name, configured,
                config.Paths.TryResolve(configured, out var resolved) ? resolved : null));
        }

        foreach (var quick in app.QuickFolders)
        {
            Add("Accesos", quick.Name, quick.Path);
        }

        Add("Feeds", "PDF generados", app.Feeds.OutputFolder);
        Add("Feeds", "Resúmenes de feeds", app.Feeds.SummaryFolder);
        Add("Feeds", "CSS", app.Feeds.CssFolder);

        Add("Audio", "Entrada", app.Audio.InputFolder);
        Add("Audio", "Audio procesado", app.Audio.CleanAudioFolder);
        Add("Audio", "Transcripciones", app.Audio.TranscriptFolder);
        Add("Audio", "Resúmenes", app.Audio.SummaryFolder);

        foreach (var index in app.Search.Indexes)
        {
            Add("Índices", index.Name, index.BaseRoot);
        }

        Add("Sistema", "Caché (importación de DEVONthink)", app.CacheFolder);
        Add("Sistema", "Perfil local", config.ConfigDirectory);
        Add("Sistema", "Almacén de índices", app.Search.IndexStorageFolder);
        Add("Sistema", "Herramientas de audio", app.Audio.ToolsFolder);
        return entries
            .GroupBy(e => (e.Category, e.ResolvedPath ?? e.ConfiguredPath, e.Name))
            .Select(g => g.First())
            .ToList();
    }
}
