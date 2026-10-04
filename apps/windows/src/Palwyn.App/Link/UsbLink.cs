using System.Diagnostics;
using Palwyn.Core;
using Windows.Devices.Enumeration;

namespace Palwyn.App.Link;

/// <summary>
/// The phone over a USB cable. With USB debugging on, adb forwards a local port to the phone's link port, so the
/// same pinned TLS link runs over the cable instead of Wi-Fi. Needs adb (Android SDK platform-tools), which isn't
/// bundled; without it this does nothing. Checks run only when a USB device comes or goes, not on a timer.
/// </summary>
public sealed class UsbLink : IDisposable
{
    public const string Host = "127.0.0.1";
    public const int LocalPort = 47801;
    /// <summary>The cable's address; another 127.0.0.1 port is something else (an emulator, the user's own tunnel).</summary>
    public static bool Is(string host, int port) => host == Host && port == LocalPort;
    // GUID_DEVINTERFACE_USB_DEVICE: any USB device plugged in or out. Without the Enabled filter, a device Windows has
    // seen before comes and goes as Updated, not Added/Removed.
    const string AnyUsbDevice = "System.Devices.InterfaceClassGuid:=\"{A5DCBF10-6530-11D2-901F-00C04FB951ED}\" AND " +
                                "System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

    readonly string? _adb = FindAdb();
    readonly Lock _gate = new();
    DeviceWatcher? _watcher;
    int _scheduled;
    bool _serverStarted;

    /// <summary>True once the phone's port is forwarded over USB, false when the cable is gone. Not on the UI thread.</summary>
    public event Action<bool>? Changed;
    public bool Forwarded { get; private set; }
    /// <summary>The phone's link port to forward to (the phone falls back to another if 47800 is taken).</summary>
    public int PhonePort { get; set; } = 47800;

    public void Start()
    {
        if (_adb is null)
        {
            Log.Info("USB: adb not found, cable connections off");
            return;
        }
        _watcher = DeviceInformation.CreateWatcher(AnyUsbDevice);
        _watcher.Added += (_, _) => Schedule(1500);
        _watcher.Removed += (_, _) => Schedule(500);
        _watcher.Updated += (_, _) => Schedule(1500); // Windows also needs this handler to keep raising Added
        Log.Info("USB: watching for a phone on a cable");
        _watcher.Start(); // enumerating the devices already there raises Added, so a phone plugged in now is found too
    }

    /// <summary>Looks again now, e.g. when the link over the cable failed.</summary>
    public void Check()
    {
        if (_watcher is not null) Schedule(0);
    }

    /// <summary>Coalesces a burst of device events into one check.</summary>
    void Schedule(int ms)
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        _ = Task.Delay(ms).ContinueWith(_ =>
        {
            _scheduled = 0;
            try { Refresh(); }
            catch (Exception e) { Log.Info($"USB: check failed: {e.GetType().Name}: {e.Message}"); } // never kill the process over it
        });
    }

    void Refresh()
    {
        bool forwarded, waiting = false;
        lock (_gate)
        {
            if (!_serverStarted) _serverStarted = Adb("start-server", read: false) is not null; // see Adb
            var lines = AdbOutput.ParseDevices(Adb("devices -l") ?? "").Where(d => d.IsUsb).ToList();
            var phone = lines.FirstOrDefault(d => d.IsReady);
            // ponytail: the first phone on USB only; the link's pinning skips it if it isn't the paired one.
            forwarded = phone is not null && Adb($"-s {phone.Serial} forward tcp:{LocalPort} tcp:{PhonePort}") is not null;
            // "unauthorized": the phone is asking "Allow USB debugging?" and only the user can answer; look again soon.
            waiting = phone is null && lines.Count > 0;
            Log.Info($"USB: checked, {lines.Count} on USB ({string.Join(", ", lines.Select(d => d.State))}), forwarded: {forwarded}");
        }
        if (waiting) Schedule(3000);
        if (forwarded == Forwarded) return;
        Forwarded = forwarded;
        Log.Info(forwarded ? "USB: phone forwarded" : "USB: no phone");
        Changed?.Invoke(forwarded);
    }

    /// <summary>Runs adb; its output, or null if it failed. <paramref name="read"/> false for start-server: the
    /// server it launches keeps inherited pipes open, so reading its output would never end.</summary>
    string? Adb(string args, bool read = true)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_adb!, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = read,
            })!;
            var output = read ? p.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            if (!p.WaitForExit(15_000))
            {
                p.Kill();
                return null;
            }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Info($"USB: adb failed: {e.Message}");
            return null;
        }
    }

    /// <summary>adb from the Android SDK (where Android Studio puts it) or PATH.</summary>
    static string? FindAdb() =>
        new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk") }
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "platform-tools"))
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(d => Path.Combine(d, "adb.exe"))
            .FirstOrDefault(File.Exists);

    public void Dispose()
    {
        if (_watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
        _watcher = null;
    }
}
