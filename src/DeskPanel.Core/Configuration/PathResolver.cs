using System.Text.RegularExpressions;

namespace DeskPanel.Core.Configuration;

/// <summary>
/// Translates portable paths (<c>{Alias}/sub/dir</c>, environment variables) into local absolute paths and back.
/// </summary>
public sealed class PathResolver
{
    private static readonly Regex RootToken = new(@"^\{(?<name>[A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _roots;

    public PathResolver(IReadOnlyDictionary<string, string> roots)
    {
        _roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in roots)
        {
            _roots[name] = Environment.ExpandEnvironmentVariables(path);
        }
    }

    public IReadOnlyDictionary<string, string> Roots => _roots;

    public string Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException("Path is empty.", nameof(configuredPath));
        }

        var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        var match = RootToken.Match(expanded);
        if (match.Success)
        {
            var name = match.Groups["name"].Value;
            if (!_roots.TryGetValue(name, out var root))
            {
                throw new InvalidOperationException($"Root alias '{{{name}}}' is not defined in machine.json.");
            }

            var rest = expanded[match.Length..].TrimStart('/', '\\');
            expanded = rest.Length == 0 ? root : Path.Combine(root, rest);
        }

        return Path.GetFullPath(Normalize(expanded));
    }

    /// <summary>Resolves a path that may be relative to <paramref name="baseDirectory"/> (typically the config directory).</summary>
    public string ResolveRelativeTo(string configuredPath, string baseDirectory)
    {
        var trimmed = configuredPath.Trim();
        if (RootToken.IsMatch(trimmed) || Path.IsPathRooted(Environment.ExpandEnvironmentVariables(trimmed)))
        {
            return Resolve(trimmed);
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, Normalize(trimmed)));
    }

    public bool TryResolve(string? configuredPath, out string resolved)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        try
        {
            resolved = Resolve(configuredPath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts an absolute path picked in a dialog into its aliased form, choosing the longest matching root
    /// so nested roots (e.g. {Cloud} and {Podcast} inside it) map to the most specific alias.
    /// </summary>
    public string MakePortable(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        string? bestName = null;
        string? bestRoot = null;

        foreach (var (name, root) in _roots)
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var isSame = string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase);
            var isChild = full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if ((isSame || isChild) && (bestRoot is null || normalizedRoot.Length > bestRoot.Length))
            {
                bestName = name;
                bestRoot = normalizedRoot;
            }
        }

        if (bestName is null || bestRoot is null)
        {
            return full;
        }

        var relative = full.Length > bestRoot.Length ? full[(bestRoot.Length + 1)..] : "";
        return relative.Length == 0 ? $"{{{bestName}}}" : $"{{{bestName}}}/{relative.Replace('\\', '/')}";
    }

    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}
