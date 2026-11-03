using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DeskPanel.Audio;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;

namespace DeskPanel.App.Audio;

/// <summary>Copies audio files dropped by the user into the configured input folder.</summary>
internal static class AudioImporter
{
    /// <summary>True when at least one of the dropped paths is an audio file (folders and other types are ignored).</summary>
    public static bool HasAudio(IEnumerable<string> paths) => paths.Any(p => File.Exists(p) && AudioFileTypes.IsAudio(p));

    /// <summary>
    /// Copies the audio files to the input folder and returns their paths there. A file already inside the folder is
    /// used as is; a name clash gets a numeric suffix, so nothing already waiting to be processed is overwritten.
    /// Returns an empty list (after telling the user why) when nothing could be imported.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ImportAsync(Shell shell, IEnumerable<string> paths)
    {
        var audio = paths.Where(p => File.Exists(p) && AudioFileTypes.IsAudio(p)).ToList();
        if (audio.Count == 0)
        {
            MessageBox.Show("Lo que has soltado no contiene ningún fichero de audio.", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Information);
            return Array.Empty<string>();
        }

        if (!shell.Config.Paths.TryResolve(shell.Config.App.Audio.InputFolder, out var input))
        {
            MessageBox.Show("Configura primero la carpeta de audios sin procesar (Configurar › Audio).", "DeskPanel",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return Array.Empty<string>();
        }

        try
        {
            Directory.CreateDirectory(input);
            var imported = new List<string>();
            foreach (var source in audio)
            {
                imported.Add(await Task.Run(() => CopyOne(source, input)));
            }

            return imported;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not import dropped audio.", ex);
            MessageBox.Show($"No se pudo copiar el audio a la carpeta de entrada:\n{ex.Message}", "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Array.Empty<string>();
        }
    }

    private static string CopyOne(string source, string inputFolder)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        if (string.Equals(Path.TrimEndingDirectorySeparator(sourceDirectory), Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputFolder)),
                StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var target = FileNames.MakeUnique(inputFolder, Path.GetFileName(source));

        // Through a temp name and a rename: the input folder is usually synced and must not expose half a copy.
        AtomicFile.ReplaceWith(source, target);
        return target;
    }
}
