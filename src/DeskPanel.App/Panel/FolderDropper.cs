using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;

namespace DeskPanel.App.Panel;

/// <summary>Copies files dropped on a folder of the panel's list into that folder.</summary>
internal static class FolderDropper
{
    public static bool CanAccept(FolderEntry folder, IEnumerable<string> paths) =>
        folder.Exists && paths.Any(File.Exists);

    /// <summary>Returns a one-line summary for the tray. Folders in the drop are ignored; the originals are never touched.</summary>
    public static async Task<(string Message, bool IsWarning)> CopyAsync(FolderEntry folder, IEnumerable<string> paths)
    {
        var target = folder.ResolvedPath!;
        var files = paths.Where(File.Exists).ToList();
        var copied = 0;
        var skipped = 0;
        string? error = null;

        foreach (var source in files)
        {
            try
            {
                if (IsInside(source, target))
                {
                    skipped++;
                    continue;
                }

                // Through a temp name and a rename: the target is often a synced folder and must not expose half a copy.
                await Task.Run(() => AtomicFile.ReplaceWith(source, FileNames.MakeUnique(target, Path.GetFileName(source))));
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"Could not copy '{source}' to '{target}'.", ex);
                error ??= ex.Message;
            }
        }

        var text = copied == 1 ? $"1 fichero copiado a «{folder.Name}»." : $"{copied} ficheros copiados a «{folder.Name}».";
        if (skipped > 0)
        {
            text += $" {skipped} ya estaba(n) en esa carpeta.";
        }

        if (error is not null)
        {
            text += $" Error: {error}";
        }

        return (text, error is not null);
    }

    private static bool IsInside(string source, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(source))!),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
}
