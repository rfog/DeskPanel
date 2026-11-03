using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DeskPanel.App.Infrastructure;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private Shell? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            // A second launch (e.g. from a pinned shortcut) should behave like the hotkey, not start a rival instance.
            SingleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        AppLog.Initialize(Path.Combine(ConfigService.LocalAppDirectory, "logs"));
        AppLog.Info("DeskPanel starting.");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        try
        {
            _shell = new Shell();
            _shell.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed.", ex);
            MessageBox.Show($"No se pudo iniciar DeskPanel:\n\n{ex.Message}\n\nRevisa {ConfigService.MachineConfigPath}.",
                "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _singleInstance.ShowRequested += () => Dispatcher.InvokeAsync(() => _shell?.ShowPanel());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        _singleInstance?.Dispose();
        AppLog.Info("DeskPanel stopped.");
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        MessageBox.Show(e.Exception.Message, "DeskPanel", MessageBoxButton.OK, MessageBoxImage.Error);

        // A resident tray app should survive a failed action rather than disappear from the tray.
        e.Handled = true;
    }
}
