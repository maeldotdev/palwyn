using System.Text.Json.Nodes;
using Palwyn.Core.Link;
using static Palwyn.Core.Node;

namespace Palwyn.Core;

/// <summary>A photo or video on the phone, from PHOTOS (docs/protocol.md section 6).</summary>
/// <param name="Duration">Videos only.</param>
public sealed record PhonePhoto(string Id, string? Name, DateTimeOffset Date, int Width, int Height, long Size, string Mime,
    bool Video = false, TimeSpan? Duration = null)
{
    public static IReadOnlyList<PhonePhoto> ListFrom(JsonObject payload) =>
        payload["photos"]!.AsArray().Select(n =>
        {
            var f = n!.AsObject();
            return new PhonePhoto(f["id"]!.GetValue<string>(), f["name"]?.GetValue<string>(),
                DateTimeOffset.FromUnixTimeMilliseconds(Long(f["date"])), (int)Long(f["width"]), (int)Long(f["height"]),
                Long(f["size"]), f["mime"]!.GetValue<string>(), f["video"]?.GetValue<bool>() ?? false,
                f["duration"] is { } d ? TimeSpan.FromMilliseconds(Long(d)) : null);
        }).ToList();

    /// <summary>"IMG_2041.jpg", or "IMG_2041 (2).jpg" if that name is already used on disk or in <paramref name="taken"/>.</summary>
    public static string UniquePath(string folder, string name, ISet<string> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (int i = 2; File.Exists(Path.Combine(folder, candidate)) || !taken.Add(candidate); i++)
            candidate = $"{stem} ({i}){ext}";
        return Path.Combine(folder, candidate);
    }

    /// <summary>A file name that is safe on Windows, whatever the phone called it.</summary>
    public static string SafeFileName(string? name, string mime)
    {
        var n = name ?? "";
        n = n[(n.LastIndexOfAny(['/', '\\']) + 1)..]; // no folders; ":" etc. are replaced below
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().TrimEnd('.');
        if (n.Length == 0 || n.Length > 200) n = mime.StartsWith("video/") ? "video.mp4" : "photo" + (mime == "image/png" ? ".png" : ".jpg");
        // Device names Windows reserves ("CON", "nul.txt", "COM1.jpg") would open a device, not a file.
        var stem = n.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && stem[..3] is "COM" or "LPT" && char.IsAsciiDigit(stem[3])))
            n = "_" + n;
        return n;
    }
}

/// <summary>
/// Files over a transfer connection: a second TLS connection to the phone, so a large file never delays events
/// on the session. The phone sends HELLO first; the PC answers with a TRANSFER_* frame instead of its HELLO.
/// </summary>
public static class Transfers
{
    static async Task ReadHello(LinkConnection link, CancellationToken ct)
    {
        var hello = await link.ExpectAsync("HELLO", ct);
        link.Capabilities = hello["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).ToHashSet();
    }

    /// <summary>TRANSFER_PULL (kind "photo", "video" or "drop"), then TRANSFER_START and exactly <c>size</c> raw bytes.</summary>
    public static async Task<(string Name, string Mime, long Size)> PullAsync(
        LinkConnection link, string kind, string id, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        await ReadHello(link, ct);
        await link.SendAsync("TRANSFER_PULL", new JsonObject { ["kind"] = kind, ["id"] = id }, ct: ct);
        var start = await link.ExpectAsync("TRANSFER_START", ct);
        long size = Long(start["size"]);
        await link.CopyRawAsync(destination, size, progress, ct);
        return (start["name"]!.GetValue<string>(), start["mime"]!.GetValue<string>(), size);
    }

    /// <summary>
    /// SCREEN_PULL: the phone then writes screen frames (length-prefixed JPEGs, read with ReadBlobAsync) until sharing
    /// stops. It answers ERROR (a JSON blob) instead when the screen isn't shared with this PC.
    /// </summary>
    public static async Task OpenScreenAsync(LinkConnection link, CancellationToken ct)
    {
        await ReadHello(link, ct);
        await link.SendAsync("SCREEN_PULL", ct: ct);
    }

    /// <summary>
    /// TRANSFER_PUSH, exactly <paramref name="size"/> raw bytes, then the phone's TRANSFER_DONE once it's stored.
    /// Kind "file" goes to Download/Palwyn (inside <paramref name="folder"/> if given); "clipboard" onto the phone's clipboard.
    /// </summary>
    public static async Task PushAsync(LinkConnection link, string name, string mime, Stream source, long size,
        IProgress<long>? progress, CancellationToken ct, string kind = "file", string? folder = null)
    {
        await ReadHello(link, ct);
        var request = new JsonObject { ["kind"] = kind, ["name"] = name, ["size"] = size, ["mime"] = mime };
        if (folder is not null) request["folder"] = folder;
        await link.SendAsync("TRANSFER_PUSH", request, ct: ct);
        await link.WriteRawAsync(source, size, progress, ct);
        await link.ExpectAsync("TRANSFER_DONE", ct);
    }
}

/// <summary>Something shared to Palwyn on the phone, for the PC to pull (DROP_OFFER).</summary>
/// <param name="Purpose">null for a share; "clipboard" (a copied image) or "camera" (a photo the PC asked for).</param>
public sealed record DropOffer(string Id, IReadOnlyList<DropFile> Files, string? Text, string? Purpose = null)
{
    public static DropOffer From(JsonObject p) => new(
        p["dropId"]!.GetValue<string>(),
        p["files"]!.AsArray().Select(n => n!.AsObject()).Select(f => new DropFile(
            (int)Long(f["index"]), f["name"]!.GetValue<string>(), Long(f["size"]), f["mime"]!.GetValue<string>())).ToList(),
        p["text"]?.GetValue<string>(), p["purpose"]?.GetValue<string>());
}

/// <summary>A photo album on the phone (a MediaStore bucket), from ALBUMS.</summary>
public sealed record PhotoAlbum(string Id, string Name, int Count)
{
    public static IReadOnlyList<PhotoAlbum> ListFrom(JsonObject payload) =>
        payload["albums"]!.AsArray().Select(n => new PhotoAlbum(
            n!["id"]!.GetValue<string>(), n["name"]!.GetValue<string>(), (int)Long(n["count"]))).ToList();
}

/// <summary>Where each file of a folder sent to the phone goes, relative to Download/Palwyn.</summary>
public static class DropFolders
{
    /// <summary>"C:\Pics\Trip\Day 1\a.jpg" under folder "C:\Pics\Trip" → "Trip/Day 1".</summary>
    public static string RelativeFolder(string folder, string file)
    {
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? "";
        return Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/');
    }
}

public sealed record DropFile(int Index, string Name, long Size, string Mime);
