using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.IO;

namespace DeskPanel.App.Infrastructure;

/// <summary>
/// API keys and tokens, encrypted with the Windows user's DPAPI key and kept in %LOCALAPPDATA%. They must never
/// go into deskpanel.json: that file is synced to every machine (and to whatever the cloud provider indexes).
/// A copy of this file on another account or PC cannot be decrypted, which is the intent.
/// </summary>
internal static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeskPanel.SecretStore.v1");

    private static string FilePath => Path.Combine(ConfigService.LocalAppDirectory, "secrets.json");

    public static string? Get(string name)
    {
        var all = Read();
        if (!all.TryGetValue(name, out var protectedValue))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Different Windows account or a damaged file: treat as "not set" so the user is simply asked again.
            return null;
        }
    }

    public static void Set(string name, string? value)
    {
        var all = Read();
        if (string.IsNullOrWhiteSpace(value))
        {
            all.Remove(name);
        }
        else
        {
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser);
            all[name] = Convert.ToBase64String(protectedBytes);
        }

        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all, ConfigService.JsonOptions));
    }

    private static Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath), ConfigService.JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }
}

/// <summary>Lets the shared services (DeskPanel.Feeds) read secrets through <see cref="ISecretStore"/>.</summary>
internal sealed class SecretStoreAdapter : ISecretStore
{
    public static SecretStoreAdapter Instance { get; } = new();

    public string? Get(string name) => SecretStore.Get(name);

    public void Set(string name, string? value) => SecretStore.Set(name, value);
}
