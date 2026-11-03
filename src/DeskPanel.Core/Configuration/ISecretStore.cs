namespace DeskPanel.Core.Configuration;

/// <summary>
/// API keys and tokens. They never go into deskpanel.json (it can be synced or indexed by a cloud provider), so each
/// platform keeps them in its own protected store: DPAPI on Windows, the login Keychain on macOS.
/// </summary>
public interface ISecretStore
{
    string? Get(string name);

    /// <summary>Stores <paramref name="value"/>; null or blank removes the secret.</summary>
    void Set(string name, string? value);
}

/// <summary>Names under which the language-model credentials are stored, shared by every front end.</summary>
public static class SecretNames
{
    public const string LmStudioToken = "lmstudio-token";
    public const string GeminiKey = "gemini-key";
    public const string ClaudeKey = "claude-key";
    public const string DevonthinkMcpToken = "devonthink-mcp-token";
}
