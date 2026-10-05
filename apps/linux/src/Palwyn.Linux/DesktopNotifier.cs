using System.ComponentModel;
using System.Diagnostics;
using Palwyn.Core;

namespace Palwyn.Linux;

/// <summary>
/// Phone notifications as desktop notifications (the freedesktop notification spec, which GNOME, KDE and the
/// others implement) through <c>notify-send</c> (libnotify) and <c>gdbus</c> (GLib).
/// </summary>
// ponytail: one process per notification; switch to D-Bus bindings once the app has them (Phase 4).
public sealed class DesktopNotifier
{
    readonly Dictionary<string, (uint Id, PhoneNotification Last)> _shown = [];
    bool _warned;

    /// <summary>NOTIFICATION_POSTED. Ones already on the phone when it connected, and reposts that change nothing
    /// the user reads, don't pop up.</summary>
    public async Task PostedAsync(PhoneNotification n)
    {
        _shown.TryGetValue(n.Key, out var shown);
        if (!n.AlertsOver(shown.Last))
        {
            if (shown.Last is not null) _shown[n.Key] = shown with { Last = n };
            return;
        }
        List<string> args = ["--app-name=" + n.AppName, "--print-id"];
        if (shown.Id != 0) args.Add("--replace-id=" + shown.Id);
        // The summary is plain text; the body may be rendered as markup, so it's escaped.
        args.AddRange(["--", n.Title ?? n.AppName, NotifyText.Escape(n.Text ?? "")]);
        var id = await RunAsync("notify-send", args);
        _shown[n.Key] = (uint.TryParse(id, out var i) ? i : 0, n);
    }

    /// <summary>NOTIFICATION_REMOVED: closes it on the desktop too.</summary>
    public async Task RemovedAsync(string key)
    {
        if (!_shown.Remove(key, out var shown) || shown.Id == 0) return;
        await RunAsync("gdbus", ["call", "--session", "--dest", "org.freedesktop.Notifications",
            "--object-path", "/org/freedesktop/Notifications",
            "--method", "org.freedesktop.Notifications.CloseNotification", shown.Id.ToString()]);
    }

    async Task<string> RunAsync(string file, List<string> args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output.Trim();
        }
        catch (Win32Exception)
        {
            if (!_warned) Console.Error.WriteLine($"Can't show desktop notifications: {file} isn't installed (packages libnotify-bin, libglib2.0-bin).");
            _warned = true;
            return "";
        }
    }
}

public static class NotifyText
{
    /// <summary>Notification servers render a subset of markup in the body: the phone's text must stay text, never
    /// bold or a link.</summary>
    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
