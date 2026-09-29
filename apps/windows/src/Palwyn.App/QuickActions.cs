using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Palwyn.App;

/// <summary>
/// One thing the user can make the phone do. Surfaces (tray flyout, Home) render <see cref="QuickActions.Available"/>;
/// adding an action is one entry in <see cref="QuickActions.All"/>, no UI change.
/// </summary>
/// <param name="Capability">Shown only when the phone advertises it; null = always.</param>
/// <param name="Run">Returns a short result for the user, or null to say nothing.</param>
/// <param name="Offline">Also offered while the phone is away (e.g. Quick Drop queues until it's back).</param>
/// <param name="Hint">Tooltip saying what the button does, when the title is short.</param>
public sealed record QuickAction(string Id, Func<string> Title, string Glyph, string? Capability, Func<Task<string?>> Run, bool Offline = false, string? Hint = null);

public static class QuickActions
{
    public static readonly IReadOnlyList<QuickAction> All =
    [
        new("ring", () => _ringing ? "Stop ringing" : "Ring phone", "", "ring", RingAsync),
        new("clipboard", () => "Clipboard", "", "clipboard", ClipboardSync.SendNowAsync, Hint: "Send what you copied on this PC to your phone"),
        new("files", () => "Send files", "", "drop", () =>
        {
            App.Current.ShowQuickDrop();
            return Task.FromResult<string?>(null);
        }, Offline: true),
        new("screen", () => "Phone screen", "", "screen", () =>
        {
            ScreenWindow.Open();
            return Task.FromResult<string?>(null);
        }, Hint: "See your phone's screen here, and control it if allowed on the phone"),
        new("photo", () => "Latest photo", "", "photos.read", LatestPhotoAsync),
        new("folder", () => "Saved files", "", null, async () =>
        {
            Directory.CreateDirectory(AppSettings.FilesFolder); // nothing received yet
            await Launcher.LaunchFolderPathAsync(AppSettings.FilesFolder);
            return null;
        }, Offline: true),
    ];

    public static IEnumerable<QuickAction> Available()
    {
        var link = App.Current.Link;
        if (link.Paired is null) return [];
        // While the phone is away its capabilities are unknown; offline actions are offered anyway.
        return All.Where(a => (link.IsConnected || a.Offline) && (a.Capability is null || link.Capabilities.Contains(a.Capability) || !link.IsConnected));
    }

    /// <summary>Titles changed (e.g. ringing started or stopped).</summary>
    public static event Action? Changed;

    /// <summary>Runs an action, turning link failures into a message.</summary>
    public static async Task<string?> RunAsync(QuickAction action)
    {
        try
        {
            return await action.Run();
        }
        catch (Exception e) when (e is PhoneErrorException or ProtocolException or TimeoutException or IOException
                                  or InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            Log.Info($"Action {action.Id} failed: {e.GetType().Name}: {e.Message}");
            return "That didn't work. Check that your phone is connected.";
        }
    }

    // ---- Ring ----

    static bool _ringing;
    static CancellationTokenSource? _ringTimeout;

    static async Task<string?> RingAsync()
    {
        bool on = !_ringing;
        await App.Current.Link.RingAsync(on);
        SetRinging(on);
        if (on) RecentActivity.Add("", "Rang your phone");
        return null;
    }

    static async void SetRinging(bool on)
    {
        _ringing = on;
        _ringTimeout?.Cancel();
        Changed?.Invoke();
        if (!on) return;
        // The phone stops by itself after a minute (or when someone taps Stop there).
        var cts = _ringTimeout = new CancellationTokenSource();
        try { await Task.Delay(TimeSpan.FromSeconds(60), cts.Token); }
        catch (OperationCanceledException) { return; }
        _ringing = false;
        Changed?.Invoke();
    }

    // ---- Latest photo ----

    static async Task<string?> LatestPhotoAsync()
    {
        var link = App.Current.Link;
        if ((await link.PhotosAsync(1)).FirstOrDefault() is not { } photo) return "No photos on your phone";
        var folder = AppSettings.PhotosFolder;
        Directory.CreateDirectory(folder);
        var path = PhonePhoto.UniquePath(folder, PhonePhoto.SafeFileName(photo.Name, photo.Mime), new HashSet<string>());
        await link.DownloadAsync(photo.Video ? "video" : "photo", photo.Id, path, null, CancellationToken.None);
        RecentActivity.Add("", $"Saved {Path.GetFileName(path)}");
        await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
        return null;
    }
}

public sealed record ActivityItem(DateTimeOffset At, string Glyph, string Text)
{
    public string Time => At.ToLocalTime().ToString("t");
}

/// <summary>What Palwyn did lately, newest first. Memory only; gone when Palwyn closes.</summary>
public static class RecentActivity
{
    const int Max = 30;
    public static ObservableCollection<ActivityItem> Items { get; } = [];

    /// <summary>UI thread only.</summary>
    public static void Add(string glyph, string text)
    {
        Items.Insert(0, new(DateTimeOffset.Now, glyph, text));
        while (Items.Count > Max) Items.RemoveAt(Max);
    }
}
