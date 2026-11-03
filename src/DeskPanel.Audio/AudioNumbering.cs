namespace DeskPanel.Audio;

/// <summary>
/// Gives an audio without a leading number the next one in the processed-audio folder ("3004 - Title" → the next is
/// "3005 - Other title"), so the library keeps a running sequence without the user typing it.
/// </summary>
public static class AudioNumbering
{
    public static bool StartsWithNumber(string fileName) => fileName.Length > 0 && char.IsAsciiDigit(fileName[0]);

    /// <summary>The largest leading number among the files of the folder (subfolders included), or 0 when none has one.</summary>
    public static long HighestNumber(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        long highest = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var name in Directory.EnumerateFiles(folder, "*", options).Select(Path.GetFileName))
        {
            if (name is not null && LeadingNumber(name) is { } number && number > highest)
            {
                highest = number;
            }
        }

        return highest;
    }

    /// <summary>
    /// Renames the input file to "{next} - {name}" and returns its new path; a file that already starts with a number, or
    /// that cannot be renamed, is returned unchanged (the processing goes on under its own name).
    /// </summary>
    public static string EnsureNumbered(string inputPath, string? cleanFolder, Action<string> log)
    {
        var name = Path.GetFileName(inputPath);
        if (string.IsNullOrWhiteSpace(cleanFolder) || StartsWithNumber(name))
        {
            return inputPath;
        }

        try
        {
            var directory = Path.GetDirectoryName(inputPath)!;
            var next = HighestNumber(cleanFolder) + 1;

            // Another file may already hold that number in the input folder; never overwrite it.
            string target;
            while (File.Exists(target = Path.Combine(directory, $"{next} - {name}")))
            {
                next++;
            }

            File.Move(inputPath, target);
            log($"  Numerado: «{Path.GetFileName(target)}» (el último de Audio procesado era el {next - 1}).");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"  ⚠ No se pudo numerar el audio, se procesa con su nombre: {ex.Message}");
            return inputPath;
        }
    }

    private static long? LeadingNumber(string fileName)
    {
        var length = 0;
        while (length < fileName.Length && char.IsAsciiDigit(fileName[length]))
        {
            length++;
        }

        // 18 digits is the most a long holds for sure; anything longer is not a sequence number.
        return length is > 0 and <= 18 ? long.Parse(fileName.AsSpan(0, length)) : null;
    }
}
