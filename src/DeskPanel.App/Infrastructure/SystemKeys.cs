using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.App.Infrastructure;

/// <summary>The global shortcuts for volume, mute and turning the screens off (Settings › Atajos).</summary>
internal sealed class SystemKeys : IDisposable
{
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const int MonitorOff = 2;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private readonly HotKeyService _service = new();
    private readonly Dictionary<int, (string Name, Action Run)> _actions = new();

    public SystemKeys() => _service.PressedId += OnPressed;

    /// <summary>Re-registers every enabled shortcut from the configuration; returns what could not be registered.</summary>
    public List<string> Apply(SystemKeysConfig config)
    {
        _service.UnregisterAll();
        _actions.Clear();
        var problems = new List<string>();

        // Volume repeats while the keys are held, like the keyboard's own keys; mute and screen-off must not.
        Register(0x5001, "Subir volumen", config.VolumeUp, repeat: true, SystemVolume.StepUp, problems);
        Register(0x5002, "Bajar volumen", config.VolumeDown, repeat: true, SystemVolume.StepDown, problems);
        Register(0x5003, "Silenciar", config.Mute, repeat: false, SystemVolume.ToggleMute, problems);
        Register(0x5004, "Apagar pantallas", config.ScreenOff, repeat: false, TurnScreensOff, problems);
        return problems;
    }

    private void Register(int id, string name, SystemKeyConfig config, bool repeat, Action run, List<string> problems)
    {
        if (!config.Enabled)
        {
            return;
        }

        if (!Enum.TryParse<ModifierKeys>(config.Modifiers, ignoreCase: true, out var modifiers)
            || !Enum.TryParse<Key>(config.Key, ignoreCase: true, out var key) || key == Key.None || modifiers == ModifierKeys.None)
        {
            problems.Add($"{name}: atajo no válido ({config.Modifiers} + {config.Key}).");
            return;
        }

        if (!_service.TryRegister(id, modifiers, key, repeat, out var error))
        {
            AppLog.Warn($"System shortcut '{name}' ({modifiers}+{key}) not registered: {error}");
            problems.Add($"{name} ({modifiers} + {key}): {error}");
            return;
        }

        _actions[id] = (name, run);
    }

    private void OnPressed(int id)
    {
        if (!_actions.TryGetValue(id, out var action))
        {
            return;
        }

        try
        {
            action.Run();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            AppLog.Warn($"System shortcut '{action.Name}' failed.", ex);
        }
    }

    // Not locking: the screens go to sleep and wake on the next input. A short pause first, because the keys of the
    // chord itself are still being released and that input would wake the screens straight away.
    private static void TurnScreensOff() => _ = Task.Run(async () =>
    {
        await Task.Delay(700);
        SendMessageTimeout(HwndBroadcast, WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(MonitorOff), SmtoAbortIfHung, 2000, out _);
    });

    public void Dispose() => _service.Dispose();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
