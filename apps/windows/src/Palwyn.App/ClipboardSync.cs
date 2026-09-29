using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Palwyn.App;

/// <summary>
/// Clipboard between this PC and the phone. PC to phone: every text or image copy, only with the setting on (off
/// by default), skipping anything a password manager or other app marked as private. Phone to PC: whenever the
/// user taps "Send clipboard to PC" on the phone. Images travel as PNG over a transfer connection, up to 20 MB.
/// </summary>
static class ClipboardSync
{
    const int MaxImage = 20 * 1024 * 1024;

    /// <summary>What we last sent or received, so a copy never bounces back to where it came from.</summary>
    static string? _last;
    static string? _lastImage; // SHA-256 of the last PNG sent
    /// <summary>Putting the phone's image on the clipboard fires a change too; it isn't a new copy.</summary>
    static long _quietUntil;

    // Formats apps add to keep a copy out of clipboard history and sync (Windows' documented convention).
    // ponytail: presence alone counts as "private"; an app setting them to 1 ("allowed") would be skipped too.
    static readonly string[] PrivateFormats =
        ["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];

    public static async void OnChanged()
    {
        var link = App.Current.Link;
        if (!AppSettings.ClipboardToPhone || !link.IsConnected || !link.Capabilities.Contains("clipboard")
            || Environment.TickCount64 < _quietUntil) return;
        try
        {
            var view = Clipboard.GetContent();
            if (PrivateFormats.Any(view.Contains)) return;
            if (view.Contains(StandardDataFormats.Text))
            {
                var text = await view.GetTextAsync();
                if (string.IsNullOrEmpty(text) || text.Length > 50_000 || text == _last) return;
                _last = text;
                await link.SendClipboardAsync(text);
                Log.Info($"Clipboard to phone: {text.Length} chars"); // never the text itself
            }
            else if (view.Contains(StandardDataFormats.Bitmap) && await PngAsync(view) is { Length: <= MaxImage } png)
            {
                var hash = Convert.ToHexString(SHA256.HashData(png));
                if (hash == _lastImage) return; // Windows often reports one copy twice
                _lastImage = hash;
                await link.SendClipboardImageAsync(png);
            }
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or IOException or InvalidOperationException
                                      or Core.Link.PhoneErrorException or Core.Protocol.ProtocolException)
        {
            // Another app holds the clipboard, or the phone just dropped: this copy isn't sent.
        }
    }

    /// <summary>"Send clipboard" action: this copy only, whether or not sync is on.</summary>
    public static async Task<string?> SendNowAsync()
    {
        var view = Clipboard.GetContent();
        if (PrivateFormats.Any(view.Contains)) return "That copy is marked private, so it wasn't sent";
        if (!view.Contains(StandardDataFormats.Text))
        {
            if (!view.Contains(StandardDataFormats.Bitmap)) return "Only text and images can be sent. Use Send files for the rest.";
            if (await PngAsync(view) is not { } png) return "Couldn't read the copied image";
            if (png.Length > MaxImage) return "That image is too big to send (over 20 MB)";
            _lastImage = Convert.ToHexString(SHA256.HashData(png));
            await App.Current.Link.SendClipboardImageAsync(png);
            RecentActivity.Add("\uE77F", "Sent a copied image to your phone");
            return "Sent. Paste it on your phone.";
        }
        var text = await view.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return "The clipboard is empty";
        if (text.Length > 50_000) return "That's too long to send (over 50,000 characters)";
        _last = text;
        await App.Current.Link.SendClipboardAsync(text);
        RecentActivity.Add("\uE77F", "Sent the clipboard to your phone");
        return "Sent. Paste it on your phone.";
    }

    /// <summary>The copied image as PNG, whatever format the copying app used (usually a bitmap). Null if unreadable.</summary>
    static async Task<byte[]?> PngAsync(DataPackageView view)
    {
        try
        {
            using var source = await (await view.GetBitmapAsync()).OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            using var bytes = new MemoryStream();
            await output.GetInputStreamAt(0).AsStreamForRead().CopyToAsync(bytes);
            return bytes.ToArray();
        }
        catch (Exception e) when (e is COMException or IOException or ArgumentException)
        {
            Log.Info($"Clipboard image unreadable: {e.GetType().Name}");
            return null;
        }
    }

    public static void Received(string text)
    {
        _last = text;
        var data = new DataPackage();
        data.SetText(text);
        try
        {
            Clipboard.SetContent(data);
            Log.Info($"Clipboard from phone: {text.Length} chars");
            RecentActivity.Add("\uE77F", "Got the clipboard from your phone");
            Toasts.ClipboardFromPhone(App.Current.Status.PhoneName ?? "your phone", text);
        }
        catch (COMException e)
        {
            Log.Info($"Couldn't set the clipboard: {e.Message}");
        }
    }

    /// <summary>An image copied on the phone, already saved at <paramref name="path"/>. True when it's on the clipboard.</summary>
    public static async Task<bool> ReceivedImage(string path)
    {
        var data = new DataPackage();
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(path)));
        try
        {
            _quietUntil = Environment.TickCount64 + 2000;
            Clipboard.SetContent(data);
            Clipboard.Flush(); // keep it after Palwyn exits and the temp file goes
            Log.Info($"Clipboard image from phone: {new FileInfo(path).Length / 1024} KB");
            RecentActivity.Add("\uE77F", "Got a copied image from your phone");
            Toasts.ClipboardFromPhone(App.Current.Status.PhoneName ?? "your phone", "An image");
            return true;
        }
        catch (COMException e)
        {
            Log.Info($"Couldn't set the clipboard: {e.Message}");
            return false;
        }
    }
}
