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
}

/// <summary>The user's choices, as a small JSON file in the config folder. Same names as the Windows app's settings.</summary>
public sealed class Settings
{
    public string? ActivePhone { get; set; }
    /// <summary>Show phone notifications on this desktop.</summary>
    public bool Notifications { get; set; } = true;
    /// <summary>Keep the phone's notifications in a list on this PC after they leave the phone.</summary>
    public bool NotificationHistory { get; set; } = true;
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
