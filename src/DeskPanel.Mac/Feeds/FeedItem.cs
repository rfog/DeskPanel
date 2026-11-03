using System.ComponentModel;
using DeskPanel.Feeds;

namespace DeskPanel.Mac.Feeds;

/// <summary>One row of the feed list, with the per-session state the actions build on (full article, translation).</summary>
public sealed class FeedItem : INotifyPropertyChanged
{
    private bool _isRead;
    private string? _fullContentHtml;
    private FeedTranslation? _translation;

    public FeedItem(FeedEntry entry, bool isRead)
    {
        Entry = entry;
        _isRead = isRead;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FeedEntry Entry { get; }

    public bool IsRead
    {
        get => _isRead;
        set { _isRead = value; Changed(); }
    }

    /// <summary>The article downloaded from the original page; null while only the feed's own content is used.</summary>
    public string? FullContentHtml
    {
        get => _fullContentHtml;
        set
        {
            _fullContentHtml = value;

            // A translation belongs to the text it was made from; a different text makes it stale.
            _translation = null;
            Changed();
        }
    }

    public FeedTranslation? Translation
    {
        get => _translation;
        set { _translation = value; Changed(); }
    }

    /// <summary>The entry in its original language, with the full article when one was downloaded: what a summary works from.</summary>
    public FeedEntry Effective => _fullContentHtml is null ? Entry : Entry with { ContentHtml = _fullContentHtml };

    /// <summary>What a PDF shows: the translation if there is one, otherwise <see cref="Effective"/>.</summary>
    public FeedEntry ForRender => _translation?.Entry ?? Effective;

    public string Title => ForRender.Title;

    public string Meta
    {
        get
        {
            var date = Entry.Published?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "sin fecha";
            return string.IsNullOrWhiteSpace(Entry.Author)
                ? $"{Entry.SourceName} · {date}"
                : $"{Entry.SourceName} · {Entry.Author} · {date}";
        }
    }

    public string Flags
    {
        get
        {
            var flags = new List<string>(2);
            if (_fullContentHtml is not null)
            {
                flags.Add("completo");
            }

            if (_translation is not null)
            {
                flags.Add("traducido");
            }

            return string.Join(" · ", flags);
        }
    }

    public double TitleOpacity => _isRead ? 0.5 : 1.0;

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
