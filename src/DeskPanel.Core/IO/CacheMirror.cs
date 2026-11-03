namespace DeskPanel.Core.IO;

/// <summary>
/// Keeps a second copy of everything the app produces in the "Caché" folder (Settings › General). It is a side effect
/// of the real work, never a reason for it to fail: a missing or unreachable cache only produces a log line.
/// </summary>
public static class CacheMirror
{
    /// <param name="cacheFolder">The resolved cache folder, or null/blank when none is configured (then nothing happens).</param>
    /// <param name="overwrite">
    /// True replaces a file of the same name (outputs that are themselves overwritten, like the processed audio); false keeps
    /// what is there and numbers the copy "(2)", like the PDFs and summaries do in their own folders.
    /// </param>
    /// <returns>The path of the copy, or null if there was none to make or it failed.</returns>
    public static string? Copy(string? cacheFolder, string sourcePath, bool overwrite, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(cacheFolder) || !File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheFolder));
            var sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
            if (string.Equals(folder, sourceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return null; // the cache is the folder the file is already in
            }

            Directory.CreateDirectory(folder);
            var name = Path.GetFileName(sourcePath);
            var target = overwrite ? Path.Combine(folder, name) : FileNames.MakeUnique(folder, name);

            // Through a temp name and a rename: the cache is usually a synced folder too.
            AtomicFile.ReplaceWith(sourcePath, target);
            log?.Invoke($"  Copia en la caché: {Path.GetFileName(target)}");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log?.Invoke($"  ⚠ No se pudo copiar a la caché: {ex.Message}");
            return null;
        }
    }
}
