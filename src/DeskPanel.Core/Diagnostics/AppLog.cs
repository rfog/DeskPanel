namespace DeskPanel.Core.Diagnostics;

/// <summary>
/// Minimal file logger. A tray app has no console, so without this, failures in background work are invisible.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _logDirectory;

    public static void Initialize(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
    }

    /// <summary>Deletes daily log files older than <paramref name="age"/>, so the folder does not grow for ever.</summary>
    public static void PruneOlderThan(TimeSpan age)
    {
        if (_logDirectory is null)
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "deskpanel-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now - age)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be pruned is not worth a failure.
        }
    }

    public static string? CurrentLogFile =>
        _logDirectory is null ? null : Path.Combine(_logDirectory, $"deskpanel-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var file = CurrentLogFile;
        if (file is null)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + ex;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down; a locked log file is not worth a crash.
        }
    }
}
