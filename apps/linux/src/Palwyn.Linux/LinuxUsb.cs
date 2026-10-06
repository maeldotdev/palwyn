using System.Diagnostics;
using Palwyn.Core;
using Palwyn.Core.Emergency;
using Palwyn.Core.Link;

namespace Palwyn.Linux;

/// <summary>
/// The phone over a USB cable, as on Windows: with USB debugging on, adb forwards <see cref="CableLink.LocalPort"/> to
/// the phone's link port, so the same pinned TLS link runs over the cable. <c>adb track-devices</c> says when a phone
/// comes or goes (no udev rules of Palwyn's own). Without adb this does nothing.
/// </summary>
public sealed class LinuxUsb : IUsbLink
{
    Process? _track;
    int _scheduled;

    public event Action<bool>? Changed;
    public event Action? DevicesChanged;
    public IReadOnlyList<AdbDevice> Devices { get; private set; } = [];
    public bool Forwarded { get; private set; }
    public int PhonePort { get; set; } = 47800;

    /// <summary>adb bundled with Palwyn (tools/linux/fetch-deps.sh), else the Android SDK's or PATH's.</summary>
    public static string? FindAdb()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "adb", "adb");
        var path = File.Exists(bundled) ? bundled
            : new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") }
                .Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "platform-tools"))
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "adb.exe" : "adb"))
                .FirstOrDefault(File.Exists);
        Log.Info($"USB: adb from {(path == bundled ? "bundled" : path is null ? "nowhere" : "sdk or path")}");
        return path;
    }

    public void Start()
    {
        if (!Adb.Available)
        {
            Log.Info("USB: adb not found, cable connections off");
            return;
        }
        // It prints the list again whenever a phone comes, goes or changes state (asking, allowed); it also starts
        // the adb server.
        _track = Adb.Start("track-devices");
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _track.StandardOutput.ReadLineAsync() is not null) Schedule(500);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        Log.Info("USB: watching for a phone on a cable");
        Schedule(0);
    }

    public void Check() => Schedule(0);

    /// <summary>Coalesces a burst of changes into one check.</summary>
    void Schedule(int ms)
    {
        if (!Adb.Available || Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(ms);
            _scheduled = 0;
            try { await RefreshAsync(); }
            catch (Exception e) { Log.Info($"USB: check failed: {e.GetType().Name}: {e.Message}"); } // never fatal
        });
    }

    async Task RefreshAsync()
    {
        var devices = await Adb.UsbDevicesAsync();
        if (!devices.SequenceEqual(Devices))
        {
            Devices = devices;
            DevicesChanged?.Invoke();
        }
        var phone = devices.FirstOrDefault(d => d.IsReady);
        // ponytail: the first phone on USB only; the link's pinning skips it if it isn't the paired one.
        bool forwarded = phone is not null
            && (await Adb.RunAsync(CancellationToken.None, "-s", phone.Serial, "forward", $"tcp:{CableLink.LocalPort}", $"tcp:{PhonePort}")).Exit == 0;
        Log.Info($"USB: checked, {devices.Count} on USB ({string.Join(", ", devices.Select(d => d.State))}), forwarded: {forwarded}");
        // "unauthorized": the phone is asking "Allow USB debugging?" and only the user can answer; look again soon.
        if (phone is null && devices.Any(d => d.State == "unauthorized")) Schedule(3000);
        if (forwarded == Forwarded) return;
        Forwarded = forwarded;
        Log.Info(forwarded ? "USB: phone forwarded" : "USB: no phone");
        Changed?.Invoke(forwarded);
    }

    public void Dispose()
    {
        try { _track?.Kill(); } catch (InvalidOperationException) { }
        _track?.Dispose();
    }
}
