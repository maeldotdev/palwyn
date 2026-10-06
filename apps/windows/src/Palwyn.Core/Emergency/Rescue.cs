using Palwyn.Core;

namespace Palwyn.Core.Emergency;

public sealed record RescueResult(int Copied, int Skipped, int Failed);

/// <summary>
/// Rescue files: copies the phone's shared storage to the PC through adb, so it works without the phone's screen and
/// even when Palwyn's phone app is frozen. Other apps' private data isn't reachable (Android). A file already on the PC
/// with the same size and date is skipped, so running it again copies only what's new.
/// </summary>
public static class Rescue
{
    public static readonly string[] Folders = ["DCIM", "Pictures", "Movies", "Music", "Download", "Documents", "Android/media"];

    public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        $"Palwyn rescue {DateTime.Now:yyyy-MM-dd}");

    public static async Task<RescueResult> RunAsync(string serial, string targetDir, IProgress<(int Done, int Total)> progress, CancellationToken ct,
        IReadOnlyList<string>? folders = null)
    {
        var files = new List<RemoteFile>();
        foreach (var folder in folders ?? Folders)
        {
            // toybox find + stat; a folder that doesn't exist just lists nothing
            var (_, output) = await Adb.RunAsync(ct, "-s", serial, "shell",
                $"find {AdbOutput.ShellQuote("/sdcard/" + folder)} -type f -exec stat -c '%s %Y %n' {{}} + 2>/dev/null");
            files.AddRange(AdbOutput.ParseStat(output));
        }

        int copied = 0, skipped = 0, failed = 0;
        progress.Report((0, files.Count));
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var local = AdbOutput.LocalPath(file.Path, targetDir);
            var info = new FileInfo(local);
            if (!AdbOutput.ShouldCopy(file, info.Exists ? info.Length : null, info.Exists ? info.LastWriteTimeUtc : null)) skipped++;
            else if (await PullAsync(serial, file, local, ct)) copied++;
            else failed++;
            progress.Report((copied + skipped + failed, files.Count));
        }
        Adb.Log($"Rescue: copied {copied}, skipped {skipped}, failed {failed}");
        return new RescueResult(copied, skipped, failed);
    }

    static async Task<bool> PullAsync(string serial, RemoteFile file, string local, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            // -a keeps the phone's date, which the skip rule compares next time
            if ((await Adb.RunAsync(ct, "-s", serial, "pull", "-a", file.Path, local)).Exit != 0) throw new IOException("adb pull failed");
            File.SetLastWriteTimeUtc(local, DateTimeOffset.FromUnixTimeSeconds(file.MtimeSeconds).UtcDateTime);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { File.Delete(local); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { } // no half files
            if (e is OperationCanceledException) throw;
            return false;
        }
    }
}
