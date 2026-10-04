#if DEBUG
using Palwyn.Core;

namespace Palwyn.App;

/// <summary>
/// Debug-only launch switches for checking UI states before the phone link exists:
/// <c>--demo=connected|connecting|disconnected</c>, <c>--flyout</c> (open the tray popup), <c>--settings</c>, <c>--calls</c>, <c>--messages</c>, <c>--notifications</c>, <c>--photos</c>, <c>--drop</c> (Quick Drop), <c>--call=ringing|active|ended</c> (demo call card).
/// </summary>
static class DevArgs
{
    /// <returns>true when the switches already decided what to show.</returns>
    public static bool Apply(string? arguments, App app, TrayFlyout flyout)
    {
        var args = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var a in args)
        {
            if (!a.StartsWith("--demo=")) continue;
            app.SetStatus(a[7..] switch
            {
                "connected" => new PhoneStatus(ConnectionState.Connected, "narzo 50", 72, false),
                "charging" => new PhoneStatus(ConnectionState.Connected, "narzo 50", 48, true),
                "connecting" => new PhoneStatus(ConnectionState.Connecting, "narzo 50"),
                "disconnected" => new PhoneStatus(ConnectionState.Disconnected, "narzo 50"),
                _ => PhoneStatus.NotPaired,
            });
        }
        if (args.FirstOrDefault(a => a.StartsWith("--call=")) is { } c)
        {
            var state = Enum.Parse<CallState>(c[7..], ignoreCase: true);
            var start = DateTimeOffset.Now.AddSeconds(-42);
            app.OnCall(new PhoneCall("demo", CallState.Ringing, true, "+63 917 123 4567", "Mika Santos", start));
            if (state != CallState.Ringing) app.OnCall(new PhoneCall("demo", state, true, "+63 917 123 4567", "Mika Santos", start));
            return true;
        }
        if (args.Contains("--rescue-test"))
        {
            _ = RescueTestAsync();
            return true;
        }
        if (args.Contains("--emergency"))
        {
            _ = EmergencyTestAsync();
            return true;
        }
        if (args.FirstOrDefault(a => a.StartsWith("--pair-dev=")) is { } pd)
        {
            _ = PairDevAsync(pd[11..]);
            return true;
        }
        if (args.Contains("--calls")) { app.ShowMain("calls"); return true; }
        if (args.Contains("--contacts")) { app.ShowMain("contacts"); return true; }
        if (args.Contains("--messages")) { app.ShowMain("messages"); return true; }
        if (args.Contains("--notifications")) { app.ShowMain("notifications"); return true; }
        if (args.Contains("--photos")) { app.ShowMain("photos"); return true; }
        if (args.Contains("--drop")) { app.ShowQuickDrop(); return true; }
        if (args.Contains("--settings")) { app.ShowMain("settings"); return true; }
        if (args.Contains("--flyout"))
        {
            // Anchor at the bottom-right corner, where the tray usually is.
            var (work, _) = Win32.MonitorFor(new PixelRect(0, 0, 1, 1));
            flyout.ShowAt(new PixelRect(work.Right - 40, work.Bottom + 8, 24, 24));
            return true;
        }
        return false;
    }

    /// <summary>
    /// <c>--pair-dev=127.0.0.1:47802</c>: pairs a phone that discovery can't see, such as an emulator behind
    /// <c>adb forward tcp:47802 tcp:47800</c>. The QR link goes to LocalState\dev-pair-uri.txt for
    /// <c>adb shell am start -d</c>; the PC keeps trying the address until the phone has accepted the invite.
    /// </summary>
    static async Task PairDevAsync(string address)
    {
        var link = App.Current.Link;
        if (Palwyn.Core.Link.PairedPhone.ParseAddress(address, 47800) is not { } at) return;
        var invite = link.NewInvite();
        var expires = DateTimeOffset.UtcNow + Palwyn.Core.Link.PcPairing.InviteLifetime;
        var file = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "dev-pair-uri.txt");
        await File.WriteAllTextAsync(file, invite.ToUri());
        var phone = new Link.DiscoveredPhone("dev", "", link.PairingTag, "Emulator", at.Host, at.Port);
        try
        {
            while (DateTimeOffset.UtcNow < expires)
            {
                try
                {
                    var paired = await link.PairWithQrAsync(phone, invite, expires, CancellationToken.None);
                    await link.SetAddressAsync(paired.DeviceId, address); // discovery can't see it later either
                    Log.Info("Dev pairing done");
                    return;
                }
                catch (Exception e) { Log.Info($"Dev pairing: not yet ({e.GetType().Name})"); }
                await Task.Delay(3000);
            }
        }
        finally { File.Delete(file); } // it holds the invite's secret
    }

    /// <summary>--emergency: an emergency session with the first allowed phone (also wireless, for testing), logs the
    /// first frames' sizes, then closes it. Temporary, until the entry points exist.</summary>
    static async Task EmergencyTestAsync()
    {
        try
        {
            var (_, output) = await Emergency.Adb.RunAsync(default, "devices", "-l");
            var device = AdbOutput.ParseDevices(output).FirstOrDefault(d => d.IsReady);
            if (device is null) { Log.Info("Emergency test: no phone"); return; }
            ScreenWindow.OpenEmergency(device); // the await above resumed on the UI thread
        }
        catch (Exception e) { Log.Info($"Emergency test failed: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>--rescue-test: Rescue limited to Download/palwyn-rescue-test (pushed by the tester) into
    /// %TEMP%\palwyn-rescue-test, twice, so the second run shows the skips.</summary>
    static async Task RescueTestAsync()
    {
        try
        {
            var (_, output) = await Emergency.Adb.RunAsync(default, "devices", "-l");
            var device = AdbOutput.ParseDevices(output).First(d => d.IsReady);
            var target = Path.Combine(Path.GetTempPath(), "palwyn-rescue-test");
            for (int run = 1; run <= 2; run++)
            {
                var r = await Emergency.Rescue.RunAsync(device.Serial, target, new Progress<(int, int)>(), default, ["Download/palwyn-rescue-test"]);
                Log.Info($"Rescue test run {run}: copied {r.Copied}, skipped {r.Skipped}, failed {r.Failed}");
            }
        }
        catch (Exception e) { Log.Info($"Rescue test failed: {e.GetType().Name}: {e.Message}"); }
    }
}
#endif
