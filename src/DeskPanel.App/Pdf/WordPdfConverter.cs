using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskPanel.App.Pdf;

/// <summary>
/// Renders Word documents through the installed Word, always opened read-only, as a PDF that pdf.js then shows.
/// Embedding a live Word window is not an option: the old DSO Framer OCX is unsupported and does not work with
/// current Office. A PDF export keeps Word's own layout, cannot be edited, and gets the same find/navigation
/// toolbar as any other PDF. Late binding (dynamic) avoids shipping Office interop assemblies.
/// </summary>
internal static class WordPdfConverter
{
    private const int WdExportFormatPdf = 17;
    private const int WdExportOptimizeForPrint = 0;
    private const int WdExportCreateHeadingBookmarks = 1;
    private const int MsoAutomationSecurityForceDisable = 3;

    private static readonly string[] Extensions = { ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt" };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<Type?> WordType = new(() => Type.GetTypeFromProgID("Word.Application"));

    public static bool IsAvailable => WordType.Value is not null;

    public static bool CanConvert(string extension) =>
        IsAvailable && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a PDF for the document, reusing a previous conversion while the source is unchanged. Conversions are
    /// serialised: each one is a Word process, and arrow-key browsing through results would otherwise start many.
    /// </summary>
    public static async Task<string> ConvertAsync(string source, string cacheDirectory, CancellationToken ct)
    {
        var info = new FileInfo(source);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}")));
        Directory.CreateDirectory(cacheDirectory);
        var target = Path.Combine(cacheDirectory, $"word-{key}.pdf");
        if (File.Exists(target))
        {
            return target;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            ct.ThrowIfCancellationRequested();
            await RunOnStaThread(() => Export(source, target)).WaitAsync(TimeSpan.FromSeconds(90), ct);
            return target;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Opens the document in a visible Word window, read-only (no changes can be saved over the original).</summary>
    public static Task OpenReadOnlyAsync(string path) => RunOnStaThread(() =>
    {
        dynamic app = Activator.CreateInstance(WordType.Value!)!;
        try
        {
            app.Visible = true;
            app.Documents.Open(FileName: path, ReadOnly: true, AddToRecentFiles: false);
            app.Activate();
        }
        finally
        {
            // Word stays open for the user: it is visible, so releasing our references does not close it.
            Marshal.FinalReleaseComObject((object)app);
        }
    });

    private static void Export(string source, string target)
    {
        dynamic? app = null;
        dynamic? document = null;
        var owned = false;
        var staging = Path.Combine(Path.GetDirectoryName(target)!, $"~{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.part.pdf");

        try
        {
            app = Activator.CreateInstance(WordType.Value!)!;

            // Settings are only changed (and Word only quit) when this is a private, empty, invisible instance.
            // If automation attached to Word the user is working in, it must be left exactly as it was.
            owned = app.Documents.Count == 0 && !(bool)app.Visible;
            if (owned)
            {
                app.Visible = false;
                app.DisplayAlerts = 0;
                app.ScreenUpdating = false;
                app.AutomationSecurity = MsoAutomationSecurityForceDisable;
            }

            // Dummy passwords make an encrypted document fail immediately instead of waiting on a hidden prompt.
            var dummy = Guid.NewGuid().ToString("N");
            document = app.Documents.Open(
                FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: dummy, PasswordTemplate: dummy, Revert: false,
                WritePasswordDocument: dummy, WritePasswordTemplate: dummy,
                Visible: false, OpenAndRepair: false, NoEncodingDialog: true);

            document.ExportAsFixedFormat(
                OutputFileName: staging, ExportFormat: WdExportFormatPdf, OpenAfterExport: false,
                OptimizeFor: WdExportOptimizeForPrint, CreateBookmarks: WdExportCreateHeadingBookmarks,
                DocStructureTags: true, IncludeDocProps: true);

            File.Move(staging, target, overwrite: true);
        }
        finally
        {
            TryCom(() => document?.Close(SaveChanges: 0));
            if (owned)
            {
                TryCom(() => app?.Quit(SaveChanges: 0));
            }

            if (document is not null) Marshal.FinalReleaseComObject((object)document);
            if (app is not null) Marshal.FinalReleaseComObject((object)app);
            document = null;
            app = null;

            // Late binding leaves hidden COM wrappers (e.g. app.Documents) alive until collected, and Word does not
            // exit while any of them is referenced. Without this, a WINWORD.EXE lingers after every conversion.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private static void TryCom(Action action)
    {
        try
        {
            action();
        }
        catch (COMException)
        {
            // Closing is best-effort; the original error (if any) is the one worth reporting.
        }
    }

    private static Task RunOnStaThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Word automation" };

        // Word is an STA COM server; calling it from a thread-pool (MTA) thread works only through slow marshalling.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
