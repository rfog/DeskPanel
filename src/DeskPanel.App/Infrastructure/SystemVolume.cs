using System;
using System.Runtime.InteropServices;

namespace DeskPanel.App.Infrastructure;

/// <summary>
/// The default playback device's volume, through the Windows Core Audio API. Steps use the system's own step size
/// (what the keyboard volume keys do), so the result matches the volume OSD, and it does not depend on which modifier
/// keys happen to be held down while a shortcut is pressed (which injected media keys would).
/// </summary>
internal static class SystemVolume
{
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int ClsctxAll = 23;

    public static void StepUp() => With(volume =>
    {
        // Like the keyboard key: raising the volume also ends a mute.
        volume.GetMute(out var muted);
        if (muted)
        {
            var unmute = Guid.Empty;
            volume.SetMute(false, ref unmute);
        }

        var context = Guid.Empty;
        volume.VolumeStepUp(ref context);
    });

    public static void StepDown() => With(volume =>
    {
        var context = Guid.Empty;
        volume.VolumeStepDown(ref context);
    });

    public static void ToggleMute() => With(volume =>
    {
        volume.GetMute(out var muted);
        var context = Guid.Empty;
        volume.SetMute(!muted, ref context);
    });

    /// <summary>Current level (0–1) and mute state; for diagnostics and tests.</summary>
    public static (float Level, bool Muted) Read()
    {
        var level = 0f;
        var muted = false;
        With(volume =>
        {
            volume.GetMasterVolumeLevelScalar(out level);
            volume.GetMute(out muted);
        });
        return (level, muted);
    }

    private static void With(Action<IAudioEndpointVolume> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? endpoint = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out endpoint);
            action((IAudioEndpointVolume)endpoint);
        }
        finally
        {
            Release(endpoint);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        void OpenPropertyStore(int access, out IntPtr properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    // The method order is the COM vtable order; unused methods are kept so that the used ones land on the right slots.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        void GetVolumeStepInfo(out uint step, out uint stepCount);
        void VolumeStepUp(ref Guid context);
        void VolumeStepDown(ref Guid context);
        void QueryHardwareSupport(out uint support);
        void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
