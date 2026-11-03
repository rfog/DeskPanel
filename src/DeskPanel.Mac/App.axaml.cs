using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Core.IO;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac;

public partial class App : Application
{
    public ConfigService Config { get; } = new();

    public MacServices Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // Same rule as the Windows app: templates are copied only when missing so hand edits survive upgrades.
    private void SeedCssTemplates()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "assets", "css");
        if (!Directory.Exists(source))
        {
            return;
        }

        var target = Path.Combine(Config.ConfigDirectory, "css");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*.css"))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                AtomicFile.ReplaceWith(file, destination);
            }
        }
    }

    // The shared libraries log through AppLog, which writes nowhere until it is pointed at a folder.
    private static void StartLogging()
    {
        var folder = Path.Combine(ConfigService.LocalAppDirectory, "logs");
        AppLog.Initialize(folder);
        AppLog.PruneOlderThan(TimeSpan.FromDays(30));
        AppLog.Info("DeskPanel (macOS) started.");

        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled exception.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Unobserved task exception.", e.Exception);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartLogging();
            Config.Load();
            SeedCssTemplates();
            Services = new MacServices(Config);
            desktop.MainWindow = new MainWindow(Services);
            desktop.Exit += (_, _) => Services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
