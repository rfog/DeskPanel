namespace DeskPanel.Audio;

public static class AudioFileTypes
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp3", ".wav", ".flac", ".ogg", ".opus", ".aac", ".wma", ".mp4", ".m4b", ".aiff", ".aif", ".amr",
    };

    public static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path));
}
