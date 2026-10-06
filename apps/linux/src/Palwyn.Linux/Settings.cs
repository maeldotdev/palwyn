using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Palwyn.Linux;

/// <summary>Where the Linux app keeps things (XDG base directories).</summary>
public static class Paths
{
    static string Xdg(string variable, params string[] fallback) =>
        Path.Combine(Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir && Path.IsPathRooted(dir)
            ? dir : Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. fallback]), "palwyn");

    /// <summary>Identity and paired phones.</summary>
    public static string Data => Xdg("XDG_DATA_HOME", ".local", "share");
    public static string Config => Xdg("XDG_CONFIG_HOME", ".config");
    public static string Logs => Path.Combine(Xdg("XDG_STATE_HOME", ".local", "state"), "logs");
    public static string Autostart => Path.Combine(Path.GetDirectoryName(Config)!, "autostart", "dev.palwyn.Palwyn.desktop");

    static string? _pictures, _downloads;
    /// <summary>Saved and received photos and videos: Pictures/Palwyn, in the user's language.</summary>
    public static string Pictures => _pictures ??= Path.Combine(UserDir("PICTURES", "Pictures"), "Palwyn");
    /// <summary>Other received files: Downloads/Palwyn.</summary>
    public static string Downloads => _downloads ??= Path.Combine(UserDir("DOWNLOAD", "Downloads"), "Palwyn");

    /// <summary>The desktop's own folder names (xdg-user-dir: "Bilder", "Imágenes"...), else the English default.</summary>
    static string UserDir(string name, string fallback)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("xdg-user-dir", [name]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var dir = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (dir.Length > 0 && dir != home) return dir; // it answers the home folder when the folder isn't set
        }
        catch (Win32Exception) { }
        return Path.Combine(home, fallback);
    }

    /// <summary>Opens a folder in the file manager.</summary>
    public static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        try { Process.Start(new ProcessStartInfo("xdg-open", [folder]) { UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception) { Log.Info("xdg-open isn't installed"); }
    }
}

/// <summary>A command the phone can run on this PC (Settings > Phone as a remote).</summary>
public sealed record RemoteCommand(string Id, string Name, string Command);

/// <summary>The user's choices, as a small JSON file in the config folder. Same names as the Windows app's settings.</summary>
public sealed class Settings
{
    public string? ActivePhone { get; set; }
    /// <summary>Show phone notifications on this desktop.</summary>
    public bool Notifications { get; set; } = true;
    /// <summary>Keep the phone's notifications in a list on this PC after they leave the phone.</summary>
    public bool NotificationHistory { get; set; } = true;
    /// <summary>Send every text copied on this PC to the phone. Off by default, as on Windows.</summary>
    public bool ClipboardToPhone { get; set; }
    /// <summary>The phone may use this PC's mouse and keyboard. Off by default, as on Windows.</summary>
    public bool RemoteInput { get; set; }
    /// <summary>The phone may control music, volume and locking. On by default, as on Windows.</summary>
    public bool RemoteMedia { get; set; } = true;
    /// <summary>Commands the phone may run on this PC, set up by the user.</summary>
    public List<RemoteCommand> RemoteCommands { get; set; } = [];
    /// <summary>Hold off sleep while the phone is connected.</summary>
    public bool KeepPcAwake { get; set; }
    public bool PauseMediaDuringCalls { get; set; } = true;
    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; set; } = "system";

    static string FilePath => Path.Combine(Paths.Config, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Config);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }

    /// <summary>Start with the desktop session: an XDG autostart entry, started in the tray.</summary>
    public static bool Autostart
    {
        get => File.Exists(Paths.Autostart);
        set
        {
            if (!value)
            {
                File.Delete(Paths.Autostart);
                return;
            }
            // Inside an AppImage, ProcessPath is a temporary mount: the AppImage file itself is what lasts.
            var exe = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath!;
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.Autostart)!);
            File.WriteAllText(Paths.Autostart, $"""
                [Desktop Entry]
                Type=Application
                Name=Palwyn
                Comment=Your Android phone on this PC
                Exec="{exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$")}" --hidden
                X-GNOME-Autostart-enabled=true

                """);
        }
    }
}

/// <summary>The log: the console and a file per day, kept 7 days. Never message text, numbers or file names.</summary>
public static class Log
{
    static readonly Lock Gate = new();

    public static void Info(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss} {line}";
        Console.WriteLine(text);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Logs);
                File.AppendAllText(Path.Combine(Paths.Logs, $"palwyn-{DateTime.Now:yyyyMMdd}.log"), text + "\n");
            }
        }
        catch (IOException) { } // a full disk mustn't stop the app
    }

    public static void Prune()
    {
        if (!Directory.Exists(Paths.Logs)) return;
        foreach (var f in Directory.GetFiles(Paths.Logs, "palwyn-*.log"))
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
    }
}
