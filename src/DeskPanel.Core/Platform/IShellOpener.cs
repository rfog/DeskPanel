namespace DeskPanel.Core.Platform;

/// <summary>Hands files, folders and URLs to the operating system's default handler.</summary>
public interface IShellOpener
{
    void OpenFile(string path);

    void OpenFolder(string path);

    /// <summary>Opens an http(s) address or any URL scheme the system knows (e.g. <c>x-devonthink-item://</c>).</summary>
    void OpenUrl(string url);

    /// <summary>Shows the file selected in the system file manager (Finder / Explorer) instead of opening it.</summary>
    void RevealFile(string path);
}
