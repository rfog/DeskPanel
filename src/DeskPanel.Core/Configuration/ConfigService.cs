using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPanel.Core.IO;

namespace DeskPanel.Core.Configuration;

public sealed class ConfigService
{
    public const string AppFolderName = "DeskPanel";
    public const string AppConfigFileName = "deskpanel.json";

    /// <summary>List of folders watched by VerificaFicheros; lives in the config directory so it follows it.</summary>
    public const string MonitoredFoldersFileName = "MonitoredFolders.txt";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Per-machine home of the app. On macOS it is a hidden profile folder in the user's home that holds the whole
    /// configuration (not just the bootstrap): the Mac does not share deskpanel.json with Windows, and cloud
    /// storage there is a File Provider path rather than a drive letter that an alias could abstract.
    /// </summary>
    public static string LocalAppDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + AppFolderName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string MachineConfigPath { get; } = Path.Combine(LocalAppDirectory, "machine.json");

    public MachineConfig Machine { get; private set; } = new();
    public AppConfig App { get; private set; } = new();
    public PathResolver Paths { get; private set; } = new(new Dictionary<string, string>());
    public string ConfigDirectory { get; private set; } = LocalAppDirectory;
    public string AppConfigPath => Path.Combine(ConfigDirectory, AppConfigFileName);

    /// <summary>True when this run created a default config, so the UI can point the user at it.</summary>
    public bool CreatedDefaults { get; private set; }

    public void Load()
    {
        Directory.CreateDirectory(LocalAppDirectory);
        CreatedDefaults = false;

        if (!File.Exists(MachineConfigPath))
        {
            // Until the user points ConfigDirectory at a synced folder, everything lives locally and still works.
            AtomicFile.WriteAllText(MachineConfigPath, JsonSerializer.Serialize(CreateDefaultMachineConfig(), JsonOptions));
            CreatedDefaults = true;
        }

        Machine = Deserialize<MachineConfig>(MachineConfigPath);
        Machine.Roots = new Dictionary<string, string>(Machine.Roots, StringComparer.OrdinalIgnoreCase);
        Paths = new PathResolver(Machine.Roots);
        ConfigDirectory = string.IsNullOrWhiteSpace(Machine.ConfigDirectory)
            ? LocalAppDirectory
            : Paths.Resolve(Machine.ConfigDirectory);
        Directory.CreateDirectory(ConfigDirectory);

        if (!File.Exists(AppConfigPath))
        {
            AtomicFile.WriteAllText(AppConfigPath, JsonSerializer.Serialize(CreateDefaultAppConfig(), JsonOptions));
            CreatedDefaults = true;
        }

        App = Deserialize<AppConfig>(AppConfigPath);
    }

    /// <summary>
    /// Locates the config directory the way <see cref="Load"/> does, but read-only: nothing is created. Lets other
    /// executables of the suite (which are not DeskPanel itself) agree on where shared files live.
    /// </summary>
    public static (string ConfigDirectory, PathResolver Paths) ReadMachineBootstrap()
    {
        if (!File.Exists(MachineConfigPath))
        {
            return (LocalAppDirectory, new PathResolver(new Dictionary<string, string>()));
        }

        var machine = Deserialize<MachineConfig>(MachineConfigPath);
        var paths = new PathResolver(machine.Roots ?? new Dictionary<string, string>());
        var directory = string.IsNullOrWhiteSpace(machine.ConfigDirectory) ? LocalAppDirectory : paths.Resolve(machine.ConfigDirectory);
        return (directory, paths);
    }

    /// <summary>
    /// Replaces this machine's alias → folder map in memory (and rebuilds <see cref="Paths"/> from it). It is not
    /// written until <see cref="SaveMachine"/> or <see cref="RelocateConfigDirectory"/> runs, so a caller that changes
    /// its mind can put the previous map back with another call.
    /// </summary>
    public void ApplyRoots(IReadOnlyDictionary<string, string> roots)
    {
        Machine.Roots = new Dictionary<string, string>(roots, StringComparer.OrdinalIgnoreCase);
        Paths = new PathResolver(Machine.Roots);
    }

    public void SaveMachine() => AtomicFile.WriteAllText(MachineConfigPath, JsonSerializer.Serialize(Machine, JsonOptions));

    public void Save() => SaveAppConfigTo(AppConfigPath);

    public bool HasAppConfigIn(string configuredDirectory) =>
        Paths.TryResolve(configuredDirectory, out var directory)
        && File.Exists(Path.Combine(directory, AppConfigFileName));

    /// <summary>
    /// Points this machine at a different config directory (machine.json only; the caller reloads afterwards).
    /// The current settings are carried over unless the target already has its own deskpanel.json and
    /// <paramref name="overwriteExisting"/> is false, so joining a config synced from another machine is safe.
    /// </summary>
    public void RelocateConfigDirectory(string configuredDirectory, bool overwriteExisting)
    {
        var target = string.IsNullOrWhiteSpace(configuredDirectory) ? LocalAppDirectory : Paths.Resolve(configuredDirectory);
        var isSame = string.Equals(Path.TrimEndingDirectorySeparator(target),
            Path.TrimEndingDirectorySeparator(ConfigDirectory), StringComparison.OrdinalIgnoreCase);

        if (!isSame)
        {
            Directory.CreateDirectory(target);
            var targetConfig = Path.Combine(target, AppConfigFileName);
            if (overwriteExisting || !File.Exists(targetConfig))
            {
                SaveAppConfigTo(targetConfig);
            }

            CopyMissing(Path.Combine(ConfigDirectory, "css"), Path.Combine(target, "css"));
            CopyMissing(Path.Combine(ConfigDirectory, MonitoredFoldersFileName), Path.Combine(target, MonitoredFoldersFileName));
            var stateFile = App.Feeds.ReadStateFile;
            if (!string.IsNullOrWhiteSpace(stateFile) && !Path.IsPathRooted(stateFile)
                && !stateFile.StartsWith('{') && !stateFile.Contains('%'))
            {
                CopyMissing(Path.Combine(ConfigDirectory, stateFile), Path.Combine(target, stateFile));
            }
        }

        Machine.ConfigDirectory = configuredDirectory.Trim();
        AtomicFile.WriteAllText(MachineConfigPath, JsonSerializer.Serialize(Machine, JsonOptions));
    }

    private void SaveAppConfigTo(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(App, JsonOptions));

    private static void CopyMissing(string source, string target)
    {
        if (File.Exists(source))
        {
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                AtomicFile.ReplaceWith(source, target);
            }
        }
        else if (Directory.Exists(source))
        {
            foreach (var file in Directory.EnumerateFiles(source))
            {
                CopyMissing(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }
    }

    public string ResolveConfigRelative(string path) => Paths.ResolveRelativeTo(path, ConfigDirectory);

    /// <summary>
    /// Sync clients never merge JSON; on concurrent edits they leave sibling copies instead.
    /// Surfacing them is cheaper than silently losing one machine's changes.
    /// </summary>
    public IReadOnlyList<string> FindConflictCopies()
    {
        if (!Directory.Exists(ConfigDirectory))
        {
            return Array.Empty<string>();
        }

        var stem = Path.GetFileNameWithoutExtension(AppConfigFileName);
        return Directory.EnumerateFiles(ConfigDirectory, stem + "*.json")
            .Where(f => !string.Equals(Path.GetFileName(f), AppConfigFileName, StringComparison.OrdinalIgnoreCase))
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(" (", StringComparison.Ordinal);
            })
            .ToList();
    }

    private static T Deserialize<T>(string path) where T : new()
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }

    private static MachineConfig CreateDefaultMachineConfig() => new()
    {
        ConfigDirectory = "",
        // macOS uses absolute paths only, so it needs no aliases.
        Roots = OperatingSystem.IsMacOS()
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cloud"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskPanelCloud"),
            },
    };

    private static AppConfig CreateDefaultAppConfig() => OperatingSystem.IsMacOS() ? CreateMacAppConfig() : CreateWindowsAppConfig();

    private static AppConfig CreateMacAppConfig()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var output = Path.Combine(home, "DeskPanel");
        var config = CreateWindowsAppConfig();
        config.Feeds.OutputFolder = Path.Combine(output, "Feeds");
        config.Search.IndexStorageFolder = Path.Combine(LocalAppDirectory, "Indexes");
        config.Audio = new AudioConfig
        {
            InputFolder = Path.Combine(output, "Podcast", "Entrada"),
            CleanAudioFolder = Path.Combine(output, "Podcast", "Audio"),
            TranscriptFolder = Path.Combine(output, "Podcast", "Transcripciones"),
            SummaryFolder = Path.Combine(output, "Podcast", "Resumenes"),
            ToolsFolder = Path.Combine(LocalAppDirectory, "tools"),
            Language = "es",
            CacheCleanAudio = false,
        };
        return config;
    }

    private static AppConfig CreateWindowsAppConfig() => new()
    {
        Feeds = new FeedsConfig
        {
            Sources = { new FeedSource { Name = "Inoreader", Url = "https://www.inoreader.com/stream/user/REPLACE_ME/tag/REPLACE_ME" } },
            Profiles =
            {
                new PdfProfile { Name = "A4", CssFile = "css/default.css" },
                new PdfProfile
                {
                    Name = "A5 lectura", CssFile = "css/reader.css",
                    PageWidthMm = 148, PageHeightMm = 210,
                    MarginTopMm = 12, MarginBottomMm = 12, MarginLeftMm = 12, MarginRightMm = 12,
                },
            },
        },
        Audio = new AudioConfig
        {
            InputFolder = "{Cloud}/Podcast/Entrada",
            CleanAudioFolder = "{Cloud}/Podcast/Audio",
            TranscriptFolder = "{Cloud}/Podcast/Transcripciones",
            SummaryFolder = "{Cloud}/Podcast/Resumenes",
        },
    };
}
