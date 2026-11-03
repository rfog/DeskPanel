namespace DeskPanel.Feeds;

public sealed record FeedEntry(
    string Id,
    string Title,
    string? Link,
    DateTimeOffset? Published,
    string? Author,
    string ContentHtml,
    string SourceName);
