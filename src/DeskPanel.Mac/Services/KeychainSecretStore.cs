using System.Diagnostics;
using DeskPanel.Core.Configuration;

namespace DeskPanel.Mac.Services;

/// <summary>Keeps secrets in the login Keychain through the <c>security</c> tool, one generic password per name.</summary>
public sealed class KeychainSecretStore : ISecretStore
{
    private const string Service = "DeskPanel";

    public string? Get(string name)
    {
        var (exitCode, output) = Run("find-generic-password", "-s", Service, "-a", name, "-w");
        return exitCode == 0 && output.Length > 0 ? output : null;
    }

    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Exit code 44 means "not found", which is exactly the state we want.
            Run("delete-generic-password", "-s", Service, "-a", name);
            return;
        }

        // The value is visible in the process list for the instant `security` runs; it is the only way the tool takes
        // a password non-interactively, and only the same user can see it.
        var (exitCode, _) = Run("add-generic-password", "-U", "-s", Service, "-a", name, "-w", value.Trim());
        if (exitCode != 0)
        {
            throw new IOException($"The Keychain refused to store '{name}' (security exited with {exitCode}).");
        }
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new IOException("Could not start /usr/bin/security.");
        var output = process.StandardOutput.ReadToEnd().TrimEnd('\n', '\r');
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
