namespace DeskPanel.Core.Configuration;

/// <summary>
/// Per-machine bootstrap kept in %LOCALAPPDATA%. It is the only file that knows real drive paths,
/// which is what lets the shared configuration and the search indexes move between machines.
/// </summary>
public sealed class MachineConfig
{
    /// <summary>Directory holding deskpanel.json; normally inside a synced folder.</summary>
    public string ConfigDirectory { get; set; } = "";

    /// <summary>Alias name → absolute local path, e.g. "Cloud" → "D:\GoogleDrive".</summary>
    public Dictionary<string, string> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The web server that lets phones and tablets search the indexes. It lives here, not in the synced deskpanel.json,
    /// because whether a machine serves (and on which port) is a decision about that machine: otherwise every PC that
    /// syncs the configuration would start serving too.
    /// </summary>
    public WebServerConfig Web { get; set; } = new();
}

public sealed class WebServerConfig
{
    public const string ScopeLocal = "Local";
    public const string ScopeLan = "Lan";

    public bool Enabled { get; set; }

    /// <summary><see cref="ScopeLocal"/> = this PC only; <see cref="ScopeLan"/> = every network interface (home network / VPN).</summary>
    public string Scope { get; set; } = ScopeLan;

    public int Port { get; set; } = 8787;

    /// <summary>When false the "download" button is gone from the page and the endpoint answers 403.</summary>
    public bool AllowDownloads { get; set; } = true;

    /// <summary>
    /// When true the web can also *do* things on this PC: process audios and convert feed entries to PDF. Off by default:
    /// reading is one thing, starting work another.
    /// </summary>
    public bool AllowActions { get; set; }

    /// <summary>Names of the indexes visible from the web. Empty means none: publishing is always an explicit choice.</summary>
    public List<string> PublishedIndexes { get; set; } = new();

    /// <summary>
    /// Address of another DeskPanel's web interface, opened by the panel's "Web remota" button. A bare host or IP uses
    /// <see cref="Port"/>; "host:port" and a full "http://…" address are taken as written.
    /// </summary>
    public string? RemoteAddress { get; set; }

    /// <summary>The page the "Web remota" button opens, or null when the text is empty or not a usable address.</summary>
    public static Uri? BuildRemoteUrl(string? address, int defaultPort)
    {
        var text = address?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var hasScheme = text.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(hasScheme ? text : "http://" + text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        // Uri reports the default port (80) both for "host" and for "host:80", so look at what was typed.
        var authority = text[(hasScheme ? text.IndexOf("://", StringComparison.Ordinal) + 3 : 0)..].Split('/')[0];
        var portTyped = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
        if (portTyped)
        {
            return uri;
        }

        return new UriBuilder(uri) { Port = defaultPort }.Uri;
    }
}
