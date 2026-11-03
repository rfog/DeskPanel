using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;
using DeskPanel.Search.Indexing;

namespace DeskPanel.App.Feeds;

/// <summary>
/// Moves the generated PDFs out of the feeds output folder into <c>{archive}\yyyy\MM</c> (by the PDF's creation date)
/// and brings the archive's search index up to date.
/// </summary>
internal static class FeedArchiver
{
    // A second click while the first run is still indexing would fight over the Lucene write lock.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task RunAsync(Shell shell)
    {
        if (!await Gate.WaitAsync(0))
        {
            shell.Notify("Archivar PDF", "Ya hay un archivado en curso.", isWarning: true);
            return;
        }

        try
        {
            var (text, isWarning) = await ArchiveAsync(shell);
            shell.Notify("Archivar PDF", text, isWarning);
        }
        catch (Exception ex)
        {
            AppLog.Error("Archiving feed PDFs failed.", ex);
            shell.Notify("Archivar PDF", $"Error: {ex.Message}", isWarning: true);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<(string Text, bool IsWarning)> ArchiveAsync(Shell shell)
    {
        var config = shell.Config;
        var feeds = config.App.Feeds;
        if (string.IsNullOrWhiteSpace(feeds.ArchiveFolder) || !config.Paths.TryResolve(feeds.ArchiveFolder, out var archive))
        {
            return ("Configura la carpeta de archivo (Configurar › Feeds › Carpeta de archivo).", true);
        }

        if (!config.Paths.TryResolve(feeds.OutputFolder, out var source) || !Directory.Exists(source))
        {
            return ("La carpeta de PDF generados no existe.", true);
        }

        if (SamePath(source, archive))
        {
            return ("La carpeta de archivo no puede ser la misma que la de PDF generados.", true);
        }

        var pdfs = Directory.EnumerateFiles(source, "*.pdf", SearchOption.TopDirectoryOnly).Select(p => new FileInfo(p)).ToList();
        var moved = 0;
        var failed = new List<string>();
        foreach (var pdf in pdfs)
        {
            try
            {
                var created = pdf.CreationTime;
                var target = Path.Combine(archive, created.Year.ToString("0000"), created.Month.ToString("00"));
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(target);

                    // Copy through a temp name and rename (the archive is synced), and only then remove the original.
                    AtomicFile.ReplaceWith(pdf.FullName, FileNames.MakeUnique(target, pdf.Name));
                    pdf.Delete();
                });
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"Could not archive '{pdf.FullName}'.", ex);
                failed.Add($"{pdf.Name}: {ex.Message}");
            }
        }

        var summary = pdfs.Count == 0 ? "No hay PDF que archivar." : $"{moved} PDF archivados.";
        if (failed.Count > 0)
        {
            summary += $" {failed.Count} con error ({failed[0]}).";
        }

        var (indexNote, indexWarning) = await UpdateIndexAsync(shell, feeds.ArchiveIndexName, archive);
        return (summary + indexNote, failed.Count > 0 || indexWarning);
    }

    private static async Task<(string Note, bool IsWarning)> UpdateIndexAsync(Shell shell, string indexName, string archive)
    {
        var config = shell.Config;
        var definition = config.App.Search.Indexes.ToArray().FirstOrDefault(d => string.Equals(d.Name, indexName, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return ($" No existe el índice «{indexName}»: créalo en Índices sobre la carpeta de archivo.", true);
        }

        if (!config.Paths.TryResolve(definition.BaseRoot, out var baseRoot) || !Directory.Exists(baseRoot))
        {
            return ($" La carpeta base del índice «{indexName}» no existe.", true);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseRoot));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archive));
        if (!SamePath(root, folder) && !folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return ($" La carpeta de archivo no está dentro de la base del índice «{indexName}».", true);
        }

        if (!config.Paths.TryResolve(config.App.Search.IndexStorageFolder, out var storage))
        {
            return (" No se pudo resolver el almacén de índices.", true);
        }

        try
        {
            var result = await shell.Indexer.UpdateAsync(definition, baseRoot, IndexLocator.GetIndexDirectory(storage, definition),
                rebuild: false, progress: null, CancellationToken.None);
            return ($" Índice «{indexName}»: {result.Added} añadidos.", result.Failures.Count > 0);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not update index '{indexName}' after archiving.", ex);
            return ($" No se pudo actualizar el índice «{indexName}»: {ex.Message}", true);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
