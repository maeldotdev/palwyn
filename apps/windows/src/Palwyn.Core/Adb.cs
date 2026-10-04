namespace Palwyn.Core;

/// <summary>One line of <c>adb devices -l</c>.</summary>
public sealed record AdbDevice(string Serial, string State, string? Model)
{
    /// <summary>On a cable. Windows' adb shows no usb: path, so wireless ones (ip:port or name._adb-tls-connect._tcp)
    /// and emulators are told apart by their serial.</summary>
    public bool IsUsb => !Serial.Contains(':') && !Serial.Contains("._adb-") && !Serial.StartsWith("emulator-");

    /// <summary>Allowed this PC ("unauthorized" means the phone is still asking).</summary>
    public bool IsReady => State == "device";
}

/// <summary>A file on the phone, from <c>stat -c "%s %Y %n"</c>.</summary>
public sealed record RemoteFile(string Path, long Size, long MtimeSeconds);

/// <summary>Parsing adb's output, for the USB link, the emergency screen and Rescue files.</summary>
public static class AdbOutput
{
    public static IReadOnlyList<AdbDevice> ParseDevices(string devicesL) =>
        devicesL.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('*') && !l.StartsWith("List of devices"))
            .Select(l => l.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length > 1)
            .Select(p => new AdbDevice(p[0], p[1], p.Skip(2).FirstOrDefault(t => t.StartsWith("model:"))?[6..]))
            .ToList();

    /// <summary>"size mtime path" per line; the path is the rest of the line, spaces and all.</summary>
    public static IReadOnlyList<RemoteFile> ParseStat(string statOutput)
    {
        var files = new List<RemoteFile>();
        foreach (var line in statOutput.Split('\n'))
        {
            var p = line.TrimEnd('\r').Split(' ', 3);
            if (p.Length == 3 && long.TryParse(p[0], out var size) && long.TryParse(p[1], out var mtime) && p[2].Length > 0)
                files.Add(new RemoteFile(p[2], size, mtime));
        }
        return files;
    }

    /// <summary>A path as one argument for the phone's shell: single-quoted, with ' written as '\''.</summary>
    public static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    /// <summary>Where a rescued /sdcard file goes under <paramref name="root"/>: the same folders, each name made safe
    /// for Windows ("a:b" → "a_b", "CON" → "_CON"), and "." / ".." dropped so nothing lands outside root.</summary>
    public static string LocalPath(string remotePath, string root)
    {
        var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(p => p is "sdcard" or "storage" or "emulated" or "0" or "self" or "primary")
            .Where(p => p is not "." and not "..")
            .Select(p => PhonePhoto.SafeFileName(p, "application/octet-stream"));
        return Path.Combine([root, .. parts]);
    }

    /// <summary>Copy unless a local file of the same size, modified within 2 s of the phone's copy, is there.</summary>
    public static bool ShouldCopy(RemoteFile remote, long? localSize, DateTimeOffset? localMtime) =>
        localSize != remote.Size || localMtime is not { } t || Math.Abs(t.ToUnixTimeSeconds() - remote.MtimeSeconds) > 2;
}
