using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;

namespace Palwyn.App;

/// <summary>
/// Things shared to this PC from the phone ("Share > Palwyn"). The user chose this PC on the phone, so
/// files are saved without asking, to the folder chosen in Settings (Downloads\Palwyn by default), and a
/// Windows notification says where.
/// </summary>
static class Receiving
{
    public static string Folder => AppSettings.FilesFolder;

    public static string FolderFor(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            ? AppSettings.PhotosFolder : AppSettings.FilesFolder;

    public static async void Accept(DropOffer offer)
    {
        var link = App.Current.Link;
        var phone = App.Current.Status.PhoneName ?? "your phone";
        if (offer.Text is { } text) Toasts.TextFromPhone(phone, text);
        if (offer.Files.Count == 0)
        {
            await link.DropResultAsync(offer.Id, true);
            return;
        }

        if (offer.Purpose == "clipboard")
        {
            await ToClipboard(offer);
            return;
        }
        Toasts.Receiving(offer.Id, phone, offer.Files);
        var saved = new List<string>();
        try
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in offer.Files)
            {
                // Photos and videos (shared, or taken for the PC) go with the saved photos; everything else to the files folder.
                var folder = FolderFor(f.Mime);
                Directory.CreateDirectory(folder);
                var path = PhonePhoto.UniquePath(folder, PhonePhoto.SafeFileName(f.Name, f.Mime), taken);
                await link.DownloadAsync("drop", $"{offer.Id}:{f.Index}", path, null, CancellationToken.None);
                saved.Add(path);
            }
            await link.DropResultAsync(offer.Id, true);
            Toasts.Received(offer.Id, phone, saved);
            if (saved.Count > 0)
                RecentActivity.Add("", saved.Count == 1 ? $"Received {Path.GetFileName(saved[0])}" : $"Received {saved.Count} files");
        }
        catch (Exception e) when (e is ProtocolException or TimeoutException or IOException or InvalidOperationException
                                  or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException)
        {
            Log.Info($"Receiving from the phone failed after {saved.Count} of {offer.Files.Count}: {e.GetType().Name}: {e.Message}");
            await link.DropResultAsync(offer.Id, false);
            Toasts.ReceiveFailed(offer.Id, phone, saved);
        }
    }

    /// <summary>An image copied on the phone: pulled to the temp folder, then put on this PC's clipboard.</summary>
    static async Task ToClipboard(DropOffer offer)
    {
        var link = App.Current.Link;
        bool ok = false;
        try
        {
            if (offer.Files is not [var f]) return;
            Directory.CreateDirectory(PhotosPage.OpenFolder);
            var path = Path.Combine(PhotosPage.OpenFolder, "clipboard-" + offer.Id + "-" + PhonePhoto.SafeFileName(f.Name, f.Mime));
            await link.DownloadAsync("drop", $"{offer.Id}:{f.Index}", path, null, CancellationToken.None);
            ok = await ClipboardSync.ReceivedImage(path);
        }
        catch (Exception e) when (e is ProtocolException or TimeoutException or IOException or InvalidOperationException
                                  or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException)
        {
            Log.Info($"Clipboard image from phone failed: {e.GetType().Name}");
        }
        finally
        {
            await link.DropResultAsync(offer.Id, ok);
        }
    }
}
