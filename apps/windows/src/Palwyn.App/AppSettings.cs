using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Palwyn.App;

/// <param name="Command">Run by cmd.exe, as this PC's user.</param>
public sealed record RemoteCommand(string Id, string Name, string Command);

static class AppSettings
{
    static IPropertySet Values => ApplicationData.Current.LocalSettings.Values;

    public static event Action? ThemeChanged;

    /// <summary>Dark by default; <see cref="ElementTheme.Default"/> means follow Windows.</summary>
    public static ElementTheme Theme
    {
        get => Values["Theme"] is int v ? (ElementTheme)v : ElementTheme.Dark;
        set
        {
            Values["Theme"] = (int)value;
            ThemeChanged?.Invoke();
        }
    }

    public static event Action? TransparencyChanged;

    /// <summary>0 = the normal Windows material (Mica), 1..100 = acrylic glass, clearer as it rises.</summary>
    public static int Transparency
    {
        get => Values["Transparency"] is int v ? Math.Clamp(v, 0, 100) : 0;
        set
        {
            if (value == Transparency) return;
            Values["Transparency"] = Math.Clamp(value, 0, 100);
            TransparencyChanged?.Invoke();
        }
    }

    /// <summary>Send every text copied on this PC to the phone. Off by default: the clipboard often holds private things.</summary>
    public static bool ClipboardToPhone
    {
        get => Values["ClipboardToPhone"] as bool? ?? false;
        set => Values["ClipboardToPhone"] = value;
    }

    /// <summary>Pause music and videos on this PC while the phone rings or a call is on. On by default.</summary>
    public static bool PauseMediaDuringCalls
    {
        get => Values["PauseMediaDuringCalls"] as bool? ?? true;
        set => Values["PauseMediaDuringCalls"] = value;
    }

    /// <summary>No sleep, screen-off or idle lock while the phone is connected. Off by default: the PC won't lock itself.</summary>
    public static bool KeepPcAwake
    {
        get => Values["KeepPcAwake"] as bool? ?? false;
        set => Values["KeepPcAwake"] = value;
    }

    /// <summary>The paired phone in use (device id); null = the first paired one.</summary>
    public static string? ActivePhone
    {
        get => Values["ActivePhone"] as string;
        set => Values["ActivePhone"] = value;
    }

    /// <summary>Where files shared from the phone are saved. Default: Downloads\Palwyn.</summary>
    public static string FilesFolder
    {
        get => Values["FilesFolder"] as string ?? Path.Combine(Win32.DownloadsFolder(), "Palwyn");
        set => Values["FilesFolder"] = value;
    }

    /// <summary>Where photos saved from the Photos page (and Latest photo) go. Default: Pictures\Palwyn.</summary>
    public static string PhotosFolder
    {
        get => Values["PhotosFolder"] as string
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Palwyn");
        set => Values["PhotosFolder"] = value;
    }

    /// <summary>"Downloads > Palwyn" style: parent and folder name, or the full path at a drive root.</summary>
    public static string Describe(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        var parent = Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd('\\')) ?? "");
        return name.Length == 0 || parent.Length == 0 ? folder : $"{parent} > {name}";
    }

    public static event Action? NotificationSettingsChanged;

    /// <summary>Show new phone notifications as Windows notifications (the Notifications page lists them either way).</summary>
    public static bool NotificationToasts
    {
        get => Values["NotificationToasts"] as bool? ?? true;
        set
        {
            Values["NotificationToasts"] = value;
            NotificationSettingsChanged?.Invoke();
        }
    }

    /// <summary>Keep the phone's last 500 notifications on this PC (Notifications > History). On by default.</summary>
    public static bool NotificationHistory
    {
        get => Values["NotificationHistory"] as bool? ?? true;
        set => Values["NotificationHistory"] = value;
    }

    /// <summary>Show this PC's Windows notifications on the phone. Off by default: they can hold private things.</summary>
    public static bool PcNotificationsToPhone
    {
        get => Values["PcNotificationsToPhone"] as bool? ?? false;
        set => Values["PcNotificationsToPhone"] = value;
    }

    /// <summary>What the phone's remote may do changed (these settings or the command list).</summary>
    public static event Action? RemoteChanged;

    /// <summary>The phone may move the mouse, click and type on this PC. Off by default: it's full control of the PC.</summary>
    public static bool RemoteInput
    {
        get => Values["RemoteInput"] as bool? ?? false;
        set
        {
            Values["RemoteInput"] = value;
            RemoteChanged?.Invoke();
        }
    }

    /// <summary>The phone may play/pause/skip this PC's media, set its volume and lock it. On by default: nothing it can't undo.</summary>
    public static bool RemoteMedia
    {
        get => Values["RemoteMedia"] as bool? ?? true;
        set
        {
            Values["RemoteMedia"] = value;
            RemoteChanged?.Invoke();
        }
    }

    /// <summary>Commands the phone can run on this PC. Only their names ever reach the phone.</summary>
    public static IReadOnlyList<RemoteCommand> RemoteCommands =>
        Values["RemoteCommands"] is string json ? JsonSerializer.Deserialize<List<RemoteCommand>>(json) ?? [] : [];

    public static void SetRemoteCommands(IEnumerable<RemoteCommand> commands)
    {
        Values["RemoteCommands"] = JsonSerializer.Serialize(commands.ToList());
        RemoteChanged?.Invoke();
    }

    /// <summary>Phone apps whose notifications are hidden on this PC: package → app name, for the Settings list.</summary>
    public static IReadOnlyDictionary<string, string> MutedApps =>
        Values["MutedApps"] is string json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [];

    public static bool IsMuted(string package) => MutedApps.ContainsKey(package);

    public static void SetMuted(string package, string appName, bool muted)
    {
        var apps = new Dictionary<string, string>(MutedApps);
        if (muted) apps[package] = appName;
        else apps.Remove(package);
        Values["MutedApps"] = JsonSerializer.Serialize(apps);
        NotificationSettingsChanged?.Invoke();
    }
}
