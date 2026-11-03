using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DeskPanel.Core.Configuration;

namespace DeskPanel.Audio;

/// <summary>
/// The external pieces the audio pipeline needs. They are installed by tools/Install-AudioTools.ps1 into one
/// folder, but an ffmpeg or deep-filter already on PATH is used too, so developer machines need no setup.
/// </summary>
public sealed record AudioTools(string ToolsFolder, string? Ffmpeg, string? DeepFilter, string ModelPath)
{
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string DefaultToolsFolder => Path.Combine(ConfigService.LocalAppDirectory, "tools");

    public bool ModelExists => File.Exists(ModelPath);

    public static AudioTools Locate(string? toolsFolder, string whisperModelFile)
    {
        var folder = string.IsNullOrWhiteSpace(toolsFolder) ? DefaultToolsFolder : toolsFolder;
        var ffmpeg = FirstExisting(Path.Combine(folder, Exe("ffmpeg"))) ?? FindOnPath(Exe("ffmpeg"));
        var deepFilter = FirstExisting(Path.Combine(folder, Exe("deep-filter"))) ?? FindOnPath(Exe("deep-filter"));
        var model = Path.IsPathRooted(whisperModelFile) ? whisperModelFile : Path.Combine(folder, "models", whisperModelFile);
        return new AudioTools(folder, ffmpeg, deepFilter, model);
    }

    private static string? FirstExisting(string path) => File.Exists(path) ? path : null;

    // A GUI app on macOS does not inherit the shell's PATH, so the Homebrew prefixes are added by hand.
    private static readonly string[] MacExtraPaths = { "/opt/homebrew/bin", "/usr/local/bin" };

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Concat(OperatingSystem.IsMacOS() ? MacExtraPaths : Array.Empty<string>())
        .Select(dir => SafeCombine(dir.Trim('"'), fileName))
        .FirstOrDefault(p => p is not null && File.Exists(p));

    private static string? SafeCombine(string dir, string file)
    {
        try
        {
            return Path.Combine(dir, file);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Runs a child process, streaming its output line by line; cancelling kills the whole process tree.</summary>
public static class ProcessRunner
{
    public static async Task<int> RunAsync(string exe, IEnumerable<string> arguments, Action<string>? onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        // ArgumentList quotes each argument itself, so paths with spaces or quotes are safe.
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Emit(e.Data);
        process.ErrorDataReceived += (_, e) => Emit(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;

        void Emit(string? line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                onLine?.Invoke(line);
            }
        }
    }

    /// <summary>Duration in seconds as reported by ffmpeg (it prints it while failing to open a missing output).</summary>
    public static async Task<double> ProbeDurationAsync(string ffmpeg, string input, CancellationToken ct)
    {
        double seconds = 0;
        await RunAsync(ffmpeg, new[] { "-hide_banner", "-i", input }, line =>
        {
            var match = Regex.Match(line, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (match.Success)
            {
                seconds = int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60
                          + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }, ct);
        return seconds;
    }
}
