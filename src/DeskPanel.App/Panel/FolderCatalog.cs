using System.Collections.Generic;
using System.IO;
using DeskPanel.Core.Configuration;

namespace DeskPanel.App.Panel;

internal sealed record FolderEntry(string Category, string Name, string ConfiguredPath, string? ResolvedPath)
{
    public bool Exists => ResolvedPath is not null && Directory.Exists(ResolvedPath);

    public string DisplayPath => ResolvedPath ?? $"{ConfiguredPath} (alias sin resolver)";
}

/// <summary>
/// Builds the jump list from the configuration itself, so every folder a feature writes to is reachable
/// without having to duplicate it under QuickFolders.
/// </summary>
internal static class FolderCatalog
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
        Add("Feeds", "PDF archivados", app.Feeds.ArchiveFolder);
        Add("Feeds", "Resúmenes de feeds", app.Feeds.SummaryFolder);

        Add("Audio", "Entrada", app.Audio.InputFolder);
        Add("Audio", "Audio procesado", app.Audio.CleanAudioFolder);
        Add("Audio", "Transcripciones", app.Audio.TranscriptFolder);
        Add("Audio", "Resúmenes", app.Audio.SummaryFolder);

        foreach (var index in app.Search.Indexes)
        {
            Add("Índices", index.Name, index.BaseRoot);
        }

        Add("Sistema", "Caché", app.CacheFolder);
        Add("Sistema", "Configuración", config.ConfigDirectory);
        Add("Sistema", "Almacén de índices", app.Search.IndexStorageFolder);
        return entries;
    }
}
