using System.Text;

namespace DeskPanel.Core.IO;

public static class AtomicFile
{
    /// <summary>
    /// Where the temporary files are made. Null (the Windows behaviour) means next to the target, where sync clients
    /// skip the "~" name. When a folder is also watched by something that imports whatever appears in it (the macOS
    /// DEVONthink rule), set this to a folder on the same volume instead, so the watched folder only ever receives a
    /// finished file through a rename.
    /// </summary>
    public static string? StagingDirectory { get; set; }

    private static string StagingPath(string directory, string targetFileName)
    {
        var folder = StagingDirectory ?? directory;
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"~{targetFileName}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Writes through a temp file in the same directory and renames it into place. Sync clients watch the
    /// target file and would otherwise upload a half-written version, which then propagates to every machine.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("Path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);

        // The temp name starts with '~' so most sync clients treat it as transient and skip uploading it.
        var tempPath = StagingPath(directory, Path.GetFileName(path));
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Replaces <paramref name="targetPath"/> with an already-complete file, e.g. a finished download.</summary>
    public static void ReplaceWith(string sourcePath, string targetPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        var staging = StagingPath(directory, Path.GetFileName(targetPath));

        // Copy to the staging folder first: a rename is only atomic within one volume.
        File.Copy(sourcePath, staging, overwrite: false);
        try
        {
            File.Move(staging, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }
}
