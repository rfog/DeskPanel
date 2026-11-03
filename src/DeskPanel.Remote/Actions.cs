namespace DeskPanel.Remote;

public sealed record WebAudioFile(string Name, long Size, DateTime ModifiedUtc);

public sealed record WebAudioResult(string Name, bool Ok, string Message);

/// <summary>One snapshot of the audio screen: what is waiting, and how the current or last run is going.</summary>
public sealed record WebAudioState(
    IReadOnlyList<WebAudioFile> Files,
    bool Running,
    string Step,
    double Progress,
    IReadOnlyList<string> Log,
    IReadOnlyList<WebAudioResult> Results,
    string Status,
    string? Warning);

/// <param name="Id">A short opaque id for this entry (never the feed's own guid or URL).</param>
public sealed record WebFeedEntry(string Id, string Title, string Source, DateTime? PublishedUtc, bool IsRead, bool HasFullContent, bool HasLink);

public sealed record WebFeedList(
    IReadOnlyList<WebFeedEntry> Entries,
    IReadOnlyList<string> Profiles,
    string DefaultProfile,
    IReadOnlyList<string> Errors);

public sealed record WebActionResult(bool Ok, string Message);

public sealed record WebSummary(bool Ok, string Message, string? Markdown);

/// <summary>
/// The things the web may *do* (as opposed to read): process audios and turn feed entries into PDFs. Everything happens on
/// the PC and in its own folders; the web only starts it and reports how it went. Separate from <see cref="IWebBackend"/>
/// so the server can offer search alone, and so every action can be switched off with one setting.
/// </summary>
public interface IWebActions
{
    Task<WebAudioState> GetAudioAsync();

    /// <returns>A message when the run could not start (nothing selected, tools missing…), otherwise null.</returns>
    Task<string?> StartAudioAsync(IReadOnlyList<string> names);

    Task CancelAudioAsync();

    Task<WebFeedList> GetFeedsAsync(bool refresh, bool includeRead, CancellationToken ct);

    /// <summary>The article as a complete HTML page with the PDF's styling, or null if the id is unknown.</summary>
    Task<string?> GetFeedPreviewAsync(string id, string? profile, CancellationToken ct);

    Task<WebActionResult> GeneratePdfAsync(string id, string? profile, CancellationToken ct);

    /// <summary>Downloads the full article from the original page, or goes back to the feed's own content if it was already downloaded.</summary>
    Task<WebActionResult> ToggleFullContentAsync(string id, CancellationToken ct);

    Task<WebSummary> SummarizeAsync(string id, CancellationToken ct);

    /// <summary>Saves the last summary made for this entry in the summaries folder.</summary>
    Task<WebActionResult> SaveSummaryAsync(string id, CancellationToken ct);
}
