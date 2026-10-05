using System.ComponentModel;
using System.Diagnostics;
using Palwyn.Core;

namespace Palwyn.Linux;

/// <summary>
/// Desktop notifications (the freedesktop notification spec, which GNOME, KDE and the others implement) through
/// <c>notify-send</c> (libnotify) and <c>gdbus</c> (GLib). Buttons need libnotify 0.7.10 or later (Ubuntu 22.10+,
/// Fedora 36+); with an older one the notification shows without them. Call from one thread at a time.
/// </summary>
// ponytail: one process per notification, and one waiting per notification with buttons; switch to D-Bus bindings
// if that ever costs too much.
public sealed class DesktopNotifier
{
    sealed record Shown(uint Id, Process? Waiting);

    readonly Dictionary<string, Shown> _shown = [];
    readonly Dictionary<string, PhoneNotification> _phone = [];
    bool _missing, _noButtons;

    /// <summary>
    /// Shows, or replaces, the notification for <paramref name="key"/>. <paramref name="onAction"/> gets the id of
    /// the button the user picked ("default" when they click the notification itself), on another thread.
    /// </summary>
    public async Task ShowAsync(string key, string app, string title, string body,
        IReadOnlyList<(string Id, string Label)> actions, Action<string>? onAction)
    {
        if (_missing) return;
        _shown.TryGetValue(key, out var old);
        Stop(old);
        bool buttons = actions.Count > 0 && onAction is not null && !_noButtons;
        List<string> args = ["--app-name=" + app];
        if (!_noButtons) args.Add("--print-id");
        if (!_noButtons && old is { Id: > 0 }) args.Add($"--replace-id={old.Id}");
        if (buttons) args.AddRange(actions.Select(a => $"--action={a.Id}={a.Label}"));
        // The summary is plain text; the body may be rendered as markup, so it's escaped.
        args.AddRange(["--", title, NotifyText.Escape(body)]);

        Process p;
        try
        {
            p = Process.Start(new ProcessStartInfo("notify-send", args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        }
        catch (Win32Exception)
        {
            _missing = true;
            Log.Info("Desktop notifications off: notify-send isn't installed (package libnotify-bin or libnotify)");
            return;
        }
        var first = await p.StandardOutput.ReadLineAsync();
        if (first is null && !_noButtons)
        {
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) // an older notify-send without --print-id, --replace-id and --action
            {
                p.Dispose();
                _noButtons = true;
                Log.Info("notify-send is older than libnotify 0.7.10: notifications without buttons");
                await ShowAsync(key, app, title, body, actions, onAction);
                return;
            }
        }
        var shown = new Shown(uint.TryParse(first, out var id) ? id : 0, buttons ? p : null);
        _shown[key] = shown;
        if (!buttons)
        {
            p.Dispose();
            return;
        }
        // With buttons notify-send waits, then prints the chosen one (nothing if the notification is just closed).
        _ = Task.Run(async () =>
        {
            try
            {
                if (await p.StandardOutput.ReadLineAsync() is { Length: > 0 } action) onAction!(action);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }

    /// <summary>Closes the notification for <paramref name="key"/> on the desktop, if it's still there.</summary>
    public async Task CloseAsync(string key)
    {
        if (!_shown.Remove(key, out var shown)) return;
        Stop(shown);
        if (shown.Id == 0) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gdbus", ["call", "--session", "--dest", "org.freedesktop.Notifications",
                "--object-path", "/org/freedesktop/Notifications",
                "--method", "org.freedesktop.Notifications.CloseNotification", shown.Id.ToString()])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            await p.WaitForExitAsync();
        }
        catch (Win32Exception) { } // no gdbus: it stays until the user closes it
    }

    /// <summary>The waiting notify-send for a replaced or closed notification: its buttons now belong to the new one.</summary>
    static void Stop(Shown? shown)
    {
        if (shown?.Waiting is not { } p) return;
        try { p.Kill(); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    /// <summary>NOTIFICATION_POSTED. Ones already on the phone when it connected, and reposts that change nothing
    /// the user reads, don't pop up.</summary>
    public Task PostedAsync(PhoneNotification n, Action<string>? onAction = null)
    {
        _phone.TryGetValue(n.Key, out var before);
        _phone[n.Key] = n;
        if (!n.AlertsOver(before)) return Task.CompletedTask;
        return ShowAsync("n:" + n.Key, n.AppName, n.Title ?? n.AppName, n.Text ?? "", ButtonsFor(n), onAction);
    }

    /// <summary>The phone notification's own actions ("a<index>"; a reply one ends in …, as it opens a reply box),
    /// and "default" for a click on the notification itself.</summary>
    public static List<(string Id, string Label)> ButtonsFor(PhoneNotification n) =>
        [.. n.Actions.Select(a => ($"a{a.Index}", a.Reply ? $"{a.Title}…" : a.Title)), ("default", "Open")];

    /// <summary>The action a button id from <see cref="ButtonsFor"/> stands for; null for "default" or anything else.</summary>
    public static NotificationAction? Chosen(PhoneNotification n, string id) =>
        id.StartsWith('a') && int.TryParse(id.AsSpan(1), out var i) ? n.Actions.FirstOrDefault(a => a.Index == i) : null;

    /// <summary>NOTIFICATION_REMOVED: closes it on the desktop too.</summary>
    public Task RemovedAsync(string key)
    {
        _phone.Remove(key);
        return CloseAsync("n:" + key);
    }
}

public static class NotifyText
{
    /// <summary>Notification servers render a subset of markup in the body: the phone's text must stay text, never
    /// bold or a link.</summary>
    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
