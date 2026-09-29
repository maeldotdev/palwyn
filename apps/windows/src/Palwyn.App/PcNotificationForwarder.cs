using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Palwyn.Core.Link;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Package = Windows.ApplicationModel.Package;

namespace Palwyn.App;

/// <summary>
/// Shows this PC's Windows notifications on the phone while the setting is on and the phone is connected.
/// Reads them with UserNotificationListener (Windows asks the user once), polling every 2 s: its change event
/// isn't available to desktop apps. Notifications already there when it starts are not sent, and
/// Palwyn's own (mirrored from the phone) never are.
/// </summary>
static class PcNotificationForwarder
{
    static readonly HashSet<uint> Seen = [], Shown = [];
    static DispatcherQueueTimer? _timer;
    static bool _busy;

    static UserNotificationListener Listener => UserNotificationListener.Current;
    public static bool Allowed => Listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed;

    /// <summary>Windows shows a consent prompt the first time. True when access is allowed.</summary>
    public static async Task<bool> RequestAccessAsync() =>
        await Listener.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed;

    /// <summary>Starts or stops forwarding to match the setting and the link. Call on the UI thread.</summary>
    public static void Update()
    {
        var link = App.Current.Link;
        bool want = AppSettings.PcNotificationsToPhone && link.IsConnected && link.Capabilities.Contains("pc.notifications") && Allowed;
        if (want && _timer is null)
        {
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += (_, _) => _ = Poll(baseline: false);
            _timer.Start();
            _ = Poll(baseline: true);
            Log.Info("PC notifications to phone: on");
        }
        else if (!want && _timer is not null)
        {
            _timer.Stop();
            _timer = null;
            Seen.Clear();
            Shown.Clear(); // the phone clears them itself when the session ends
            Log.Info("PC notifications to phone: off");
        }
    }

    static async Task Poll(bool baseline)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var current = await Listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = new HashSet<uint>();
            foreach (var n in current)
            {
                ids.Add(n.Id);
                if (!Seen.Add(n.Id) || baseline || IsOurs(n)) continue;
                var (app, title, text) = Describe(n);
                if (title is null && text is null) continue;
                await App.Current.Link.SendPcNotificationAsync(n.Id.ToString(), app, title, text);
                Shown.Add(n.Id);
            }
            Seen.IntersectWith(ids);
            foreach (var gone in Shown.Where(id => !ids.Contains(id)).ToList())
            {
                Shown.Remove(gone);
                await App.Current.Link.RemovePcNotificationAsync(gone.ToString());
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or COMException
                                      or PhoneErrorException)
        {
            Log.Info($"PC notifications: {e.GetType().Name}");
        }
        finally
        {
            _busy = false;
        }
    }

    static bool IsOurs(UserNotification n)
    {
        try { return n.AppInfo.PackageFamilyName == Package.Current.Id.FamilyName; }
        catch (COMException) { return false; }
    }

    /// <summary>App name, then the toast's first text line as the title and the rest as the text.</summary>
    static (string App, string? Title, string? Text) Describe(UserNotification n)
    {
        string app;
        try { app = n.AppInfo.DisplayInfo.DisplayName; }
        catch (COMException) { app = "Windows"; }
        var lines = n.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
            .Select(t => t.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Cast<string>().ToList() ?? [];
        var title = lines.FirstOrDefault();
        var text = lines.Count > 1 ? string.Join("\n", lines.Skip(1)) : null;
        return (Clip(app, 128) ?? "Windows", Clip(title, 500), Clip(text, 4000));
    }

    static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}
