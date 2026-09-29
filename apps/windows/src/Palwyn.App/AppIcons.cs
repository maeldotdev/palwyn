using System.Text.RegularExpressions;
using Windows.Storage;

namespace Palwyn.App;

/// <summary>
/// Phone app icons for notifications, fetched once per app and kept as PNGs in the package's local folder,
/// where both XAML and Windows notifications (ms-appdata:) can load them.
/// </summary>
static partial class AppIcons
{
    static readonly string Dir = Path.Combine(ApplicationData.Current.LocalFolder.Path, "icons");
    static readonly HashSet<string> Requested = [];

    /// <summary>Raised on the UI thread when an app's icon becomes available.</summary>
    public static event Action<string>? Arrived;

    // The package name becomes a file name, so accept only what Android allows in one.
    [GeneratedRegex("^[A-Za-z0-9_.]{1,128}$")]
    private static partial Regex PackageName();

    public static bool Has(string package) => PackageName().IsMatch(package) && File.Exists(Path.Combine(Dir, package + ".png"));

    public static Uri UriFor(string package) => new($"ms-appdata:///local/icons/{package}.png");

    // ponytail: never refreshed, so an app that changes its icon keeps the old one until the icons folder is cleared.
    public static async void Ensure(string package)
    {
        if (!PackageName().IsMatch(package) || Has(package) || !Requested.Add(package)) return;
        try
        {
            var png = await App.Current.Link.AppIconAsync(package);
            Directory.CreateDirectory(Dir);
            await File.WriteAllBytesAsync(Path.Combine(Dir, package + ".png"), png);
            Arrived?.Invoke(package);
        }
        catch (Exception e) when (e is Core.Link.PhoneErrorException or FormatException)
        {
            // The phone has no icon for it (not a launcher app): don't ask again this session.
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
        {
            Requested.Remove(package); // link trouble: try again with its next notification
        }
    }
}
