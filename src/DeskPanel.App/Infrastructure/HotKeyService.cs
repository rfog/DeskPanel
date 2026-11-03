using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeskPanel.App.Infrastructure;

/// <summary>
/// System-wide hotkeys via RegisterHotKey. A hidden message-only window receives WM_HOTKEY so the hotkeys
/// keep working while no DeskPanel window exists. Receiving WM_HOTKEY also grants the process the right
/// to take foreground, which is why the panel can activate itself afterwards.
/// </summary>
internal sealed class HotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;

    /// <summary>The id of the single hotkey the panel uses through <see cref="TryRegister(ModifierKeys, Key, out string?)"/>.</summary>
    public const int PanelId = 0x4450;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    // Without MOD_NOREPEAT, holding the chord fires repeatedly and the panel flickers open/closed.
    private const uint ModNoRepeat = 0x4000;

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = new();

    public HotKeyService()
    {
        var parameters = new HwndSourceParameters("DeskPanel.HotKeySink") { WindowStyle = 0, ParentWindow = HwndMessage };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Raised for the panel's hotkey.</summary>
    public event EventHandler? Pressed;

    /// <summary>Raised for every hotkey, with the id it was registered under.</summary>
    public event Action<int>? PressedId;

    public bool TryRegister(ModifierKeys modifiers, Key key, out string? error) =>
        TryRegister(PanelId, modifiers, key, repeat: false, out error);

    /// <param name="repeat">True lets the chord auto-repeat while held (wanted for volume, not for a toggle).</param>
    public bool TryRegister(int id, ModifierKeys modifiers, Key key, bool repeat, out string? error)
    {
        Unregister(id);

        uint native = repeat ? 0 : ModNoRepeat;
        if (modifiers.HasFlag(ModifierKeys.Alt)) native |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) native |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) native |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) native |= ModWin;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_source.Handle, id, native, virtualKey))
        {
            // Typically ERROR_HOTKEY_ALREADY_REGISTERED: another app (or the shell for many Win+ chords) owns it.
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        _registered.Add(id);
        error = null;
        return true;
    }

    public void Unregister(int id = PanelId)
    {
        if (_registered.Remove(id))
        {
            UnregisterHotKey(_source.Handle, id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.ToArray())
        {
            Unregister(id);
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && _registered.Contains(wParam.ToInt32()))
        {
            handled = true;
            var id = wParam.ToInt32();
            if (id == PanelId)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }

            PressedId?.Invoke(id);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
