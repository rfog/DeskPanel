using System.Diagnostics;
using DeskPanel.Core.Platform;

namespace DeskPanel.Mac.Services;

/// <summary>Opens things through <c>/usr/bin/open</c>, which also routes custom URL schemes to their apps.</summary>
public sealed class MacShellOpener : IShellOpener
{
    public void OpenFile(string path) => Run(path);

    public void OpenFolder(string path) => Run(path);

    public void OpenUrl(string url) => Run(url);

    public void RevealFile(string path)
    {
        var info = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        info.ArgumentList.Add("-R");
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(path);
        Process.Start(info)?.Dispose();
    }

    private static void Run(string target)
    {
        // ArgumentList avoids shell quoting: a target starting with '-' would still be read as an option, so '--' ends them.
        var info = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(target);
        Process.Start(info)?.Dispose();
    }
}
