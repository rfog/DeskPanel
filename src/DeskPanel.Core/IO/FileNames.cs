using System.Text;

namespace DeskPanel.Core.IO;

public static class FileNames
{
    // Windows' reserved set on every platform: a name made on the Mac ends up in folders that sync to Windows,
    // where a colon or a question mark in a title would be rejected.
    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));

    public static string Sanitize(string name, int maxLength = 120)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            builder.Append(Invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        var result = builder.ToString().Trim(' ', '.');

        // Trailing dots/spaces are silently stripped by Win32, which breaks later exists-checks.
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd(' ', '.');
        }

        return result.Length == 0 ? "untitled" : result;
    }

    public static string MakeUnique(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
