namespace DeskPanel.Core.Configuration;

/// <summary>
/// Shared configuration stored in the synced config directory. Every path here may use a
/// root alias such as <c>{Cloud}/Podcast</c>, resolved per machine through <see cref="MachineConfig"/>.
/// </summary>
public sealed class AppConfig
{
    public HotKeyConfig HotKey { get; set; } = new();
    public SystemKeysConfig SystemKeys { get; set; } = new();
    public PanelConfig Panel { get; set; } = new();
    public FeedsConfig Feeds { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public SearchConfig Search { get; set; } = new();
    public DevonthinkConfig Devonthink { get; set; } = new();
    public List<QuickFolder> QuickFolders { get; set; } = new();

    /// <summary>
    /// "Caché": a second copy of everything produced or added (feed PDFs, their summaries, the processed audio, its
    /// transcript and summary). Empty means no copies are made.
    /// </summary>
    public string? CacheFolder { get; set; }
}

public sealed class HotKeyConfig
{
    // Stored as WPF enum names so the JSON stays readable and editable by hand.
    public string Modifiers { get; set; } = "Control, Alt";
    public string Key { get; set; } = "Space";
}

/// <summary>One global shortcut: whether it is on, and its chord as WPF enum names (like <see cref="HotKeyConfig"/>).</summary>
public sealed class SystemKeyConfig
{
    public bool Enabled { get; set; } = true;
    public string Modifiers { get; set; } = "Control, Alt";
    public string Key { get; set; } = "";
}

/// <summary>
/// System-wide shortcuts that are not about the panel: volume, mute and turning the screens off. Part of the shared
/// configuration on purpose (the same keys on every PC); a machine that does not want one simply unticks it.
/// </summary>
public sealed class SystemKeysConfig
{
    public SystemKeyConfig VolumeUp { get; set; } = new() { Key = "Up" };
    public SystemKeyConfig VolumeDown { get; set; } = new() { Key = "Down" };
    public SystemKeyConfig Mute { get; set; } = new() { Key = "M" };

    /// <summary>Turns the monitors off without locking the PC.</summary>
    public SystemKeyConfig ScreenOff { get; set; } = new() { Key = "H" };
}

public sealed class PanelConfig
{
    /// <summary>When false the panel behaves like a launcher and closes as soon as it loses focus.</summary>
    public bool KeepOpenOnFocusLoss { get; set; }
}

public sealed class FeedsConfig
{
    public List<FeedSource> Sources { get; set; } = new();
    public string OutputFolder { get; set; } = "{Cloud}/DeskPanel/Feeds";
    public string DefaultProfile { get; set; } = "A4";

    /// <summary>Where the "Archivar PDF" button moves the PDFs, as <c>yyyy\MM</c> subfolders. Empty means not configured.</summary>
    public string? ArchiveFolder { get; set; }

    /// <summary>Where the "Resumir" window saves the Markdown summaries. Empty means not configured.</summary>
    public string? SummaryFolder { get; set; }

    /// <summary>Custom prompt for summarising an entry; null means the built-in default (so improvements to it reach everyone).</summary>
    public string? SummaryPrompt { get; set; }

    /// <summary>Custom prompt for translating an entry to Spanish; null means the built-in default.</summary>
    public string? TranslationPrompt { get; set; }

    /// <summary>Index (defined on the archive folder) that is updated after archiving.</summary>
    public string ArchiveIndexName { get; set; } = "Scrapbook";

    /// <summary>
    /// Extra stylesheet appended after the profile's CSS so it overrides it. Relative to the config directory
    /// (e.g. "css/inoreader.css"); empty means none.
    /// </summary>
    public string? CustomCssFile { get; set; }

    /// <summary>
    /// Folder that holds the CSS files named by <see cref="PdfProfile.CssFile"/> (as <c>css/name.css</c>) and
    /// <see cref="CustomCssFile"/>. Empty means a <c>css</c> folder inside the config directory. Lets the Mac use the
    /// same stylesheets as Windows without the two sharing a whole configuration.
    /// </summary>
    public string? CssFolder { get; set; }

    /// <summary>Chrome executable used to print PDFs on macOS. Empty means the default install location.</summary>
    public string? ChromePath { get; set; }
    public List<PdfProfile> Profiles { get; set; } = new();

    // Relative to the config directory so the read state follows the user across machines.
    public string ReadStateFile { get; set; } = "feeds-read-state.json";
    public int ReadStateRetentionDays { get; set; } = 180;
    public bool OpenPreviewAfterGenerate { get; set; } = true;
}

public sealed class FeedSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class PdfProfile
{
    public string Name { get; set; } = "";

    // Relative to the config directory unless rooted or aliased.
    public string CssFile { get; set; } = "css/default.css";

    // Millimetres are what print dialogs and paper specs use; conversion to inches happens at the WebView2 boundary.
    public double PageWidthMm { get; set; } = 210;
    public double PageHeightMm { get; set; } = 297;
    public double MarginTopMm { get; set; } = 15;
    public double MarginBottomMm { get; set; } = 15;
    public double MarginLeftMm { get; set; } = 15;
    public double MarginRightMm { get; set; } = 15;
    public double Scale { get; set; } = 1.0;
    public bool PrintBackgrounds { get; set; } = true;

    /// <summary>Header with the article URL and footer with creation date and "n de total". Needs top/bottom margins of about 10 mm or more.</summary>
    public bool ShowHeaderFooter { get; set; } = true;
}

public sealed class AudioConfig
{
    // Pipeline not implemented yet; folders are declared now so the quick-folder list can already show them.
    public string? InputFolder { get; set; }
    public string? CleanAudioFolder { get; set; }
    public string? TranscriptFolder { get; set; }
    public string? SummaryFolder { get; set; }

    // Which stages of the pipeline run; all on by default.
    public bool CleanNoise { get; set; } = true;
    public bool Transcribe { get; set; } = true;
    public bool Summarize { get; set; } = true;

    /// <summary>
    /// Whether the cleaned audio is copied to the cache folder too. The transcript and the summary always are. Off on the
    /// Mac, where the cache is the folder DEVONthink imports from and a large m4a is not wanted there.
    /// </summary>
    public bool CacheCleanAudio { get; set; } = true;

    /// <summary>Whisper model file name inside the tools' models folder (or an absolute path).</summary>
    public string WhisperModelFile { get; set; } = "ggml-large-v3-turbo.bin";

    public string Language { get; set; } = "es";

    /// <summary>Custom prompt for the summary; null means the built-in default (so improvements to it reach everyone).</summary>
    public string? SummaryPrompt { get; set; }

    /// <summary>
    /// Where ffmpeg.exe, deep-filter.exe and models\ live. Machine specific, so normally left empty
    /// (= %LOCALAPPDATA%\DeskPanel\tools), which is where Install-AudioTools.ps1 puts them.
    /// </summary>
    public string? ToolsFolder { get; set; }
}

/// <summary>
/// Which language model writes the summaries. API keys are NOT stored here: this file is synced between machines,
/// so keys go to a per-user, DPAPI-protected file in %LOCALAPPDATA% instead.
/// </summary>
public sealed class AiConfig
{
    /// <summary>LmStudio, Gemini or Claude.</summary>
    public string Provider { get; set; } = "LmStudio";

    public string LmStudioUrl { get; set; } = "http://localhost:1234/v1";
    public string LmStudioModel { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-2.5-pro";
    public string ClaudeModel { get; set; } = "claude-sonnet-5-5";
    public double Temperature { get; set; } = 0.4;
}

public sealed class SearchConfig
{
    // Deliberately outside any synced folder: Lucene segment files and write locks do not survive sync clients.
    public string IndexStorageFolder { get; set; } = "%LOCALAPPDATA%/DeskPanel/Indexes";
    public List<IndexDefinition> Indexes { get; set; } = new();
    public int MaxResults { get; set; } = 200;
}

public sealed class IndexDefinition
{
    /// <summary>Also used as the index folder name, so it is what gets copied between machines.</summary>
    public string Name { get; set; } = "";

    /// <summary>Base folder, ideally aliased so the same index maps onto a different local path elsewhere.</summary>
    public string BaseRoot { get; set; } = "";

    /// <summary>Folders relative to <see cref="BaseRoot"/> using '/'. An empty string means the whole base.</summary>
    public List<string> IncludedFolders { get; set; } = new();

    /// <summary>Empty means every type the app can extract; the UI pre-fills new indexes with that full list.</summary>
    public List<string> Extensions { get; set; } = new();
    public bool Recursive { get; set; } = true;

    // Reading a cloud-only placeholder forces a full download; indexing a large drive would pull everything.
    public bool SkipCloudPlaceholders { get; set; } = true;
}

public sealed class QuickFolder
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>
/// How the Mac build talks to DEVONthink's MCP server. The access token is NOT stored here (it goes to the Keychain);
/// Windows ignores this whole section.
/// </summary>
public sealed class DevonthinkConfig
{
    public const string TransportHttp = "Http";
    public const string TransportStdio = "Stdio";

    /// <summary><see cref="TransportHttp"/> = the running MCP server on <see cref="Port"/> with the bearer token; <see cref="TransportStdio"/> = launch DEVONthink's MCP helper as a child process (no port, no token).</summary>
    public string Transport { get; set; } = TransportHttp;

    public int Port { get; set; } = 8420;

    /// <summary>Path of the MCP helper executable for the stdio transport; empty means the one inside DEVONthink.app.</summary>
    public string? StdioCommand { get; set; }

    public int MaxResults { get; set; } = 200;

    /// <summary>The databases found the last time they were read, so searches can choose among them without asking DEVONthink first.</summary>
    public List<DevonthinkDatabase> Databases { get; set; } = new();
}

public sealed class DevonthinkDatabase
{
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
}
