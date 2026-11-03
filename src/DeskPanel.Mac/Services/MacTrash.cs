using System.Diagnostics;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.Mac.Services;

/// <summary>Moves a file to the Trash the way Finder does ("Put Back" keeps working), so a deletion is recoverable.</summary>
public static class MacTrash
{
    /// <exception cref="IOException">Finder refused or could not be reached (e.g. Automation permission denied).</exception>
    public static void MoveToTrash(string path)
    {
        // Finder, not a manual move into ~/.Trash: that folder is protected, and only Finder records where the file came from.
        var info = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add($"tell application \"Finder\" to delete (POSIX file \"{Escape(Path.GetFullPath(path))}\")");

        using var process = Process.Start(info) ?? throw new IOException("Could not start osascript.");
        var error = process.StandardError.ReadToEnd().Trim();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            AppLog.Warn($"Moving '{path}' to the Trash failed: {error}");
            throw new IOException(error.Length > 0 ? error : $"osascript exited with {process.ExitCode}");
        }
    }

    // AppleScript string literals only treat the backslash and the double quote specially.
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
