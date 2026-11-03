using System.Diagnostics;
using Avalonia.Controls;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Devonthink;

/// <summary>Makes sure DEVONthink is running before it is queried, asking first instead of launching it behind the user's back.</summary>
public static class DevonthinkApp
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(40);

    public static bool IsRunning() => Process.GetProcessesByName("DEVONthink").Length > 0;

    /// <returns>True when DEVONthink is running (already, or after the user agreed to open it); false if they declined or it did not start.</returns>
    public static async Task<bool> EnsureRunningAsync(Window owner)
    {
        if (IsRunning())
        {
            return true;
        }

        if (!await Dialogs.ConfirmAsync(owner, "DEVONthink no está abierto. ¿Quieres abrirlo ahora para hacer la consulta?", "Abrir DEVONthink"))
        {
            return false;
        }

        var info = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };

        // -g keeps focus where it is: the user is working in DeskPanel, not about to use DEVONthink.
        foreach (var argument in new[] { "-g", "-a", "DEVONthink" })
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info)?.Dispose();
        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (!IsRunning() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        if (!IsRunning())
        {
            return false;
        }

        // The process exists before it can answer Apple Events and MCP calls; give it a moment to open its databases.
        await Task.Delay(4_000);
        return true;
    }
}
