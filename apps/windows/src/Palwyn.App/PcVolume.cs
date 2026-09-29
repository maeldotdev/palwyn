using System.Runtime.InteropServices;

namespace Palwyn.App;

/// <summary>The master volume of the default speakers (Core Audio), as in Windows' own volume flyout.</summary>
static class PcVolume
{
    static (IAudioEndpointVolume Volume, long At)? _cached;

    /// <summary>0..100 and mute, or null when there's no output device.</summary>
    public static (int Level, bool Muted)? Get()
    {
        try
        {
            // ponytail: the endpoint is reused for 30 s (the phone reads this every 2 s), so a new default device
            // (headphones plugged in) shows up to 30 s late; an IMMNotificationClient would make it instant.
            if (_cached is not { } c || Environment.TickCount64 - c.At > 30_000) _cached = c = (Endpoint(), Environment.TickCount64);
            var v = c.Volume;
            Marshal.ThrowExceptionForHR(v.GetMasterVolumeLevelScalar(out float level));
            Marshal.ThrowExceptionForHR(v.GetMute(out bool muted));
            return ((int)Math.Round(level * 100), muted);
        }
        catch (Exception) // no speakers, or the audio service restarting
        {
            _cached = null;
            return null;
        }
    }

    public static void Set(int? level, bool? muted)
    {
        var v = Endpoint();
        var context = Guid.Empty;
        if (level is int l) Marshal.ThrowExceptionForHR(v.SetMasterVolumeLevelScalar(Math.Clamp(l, 0, 100) / 100f, ref context));
        if (muted is bool m) Marshal.ThrowExceptionForHR(v.SetMute(m, ref context));
    }

    // The default device can change (headphones plugged in): Set looks it up each time, Get every 30 s.
    static IAudioEndpointVolume Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var volume));
        return (IAudioEndpointVolume)volume;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    // Methods in vtable order; only the ones used are called.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
