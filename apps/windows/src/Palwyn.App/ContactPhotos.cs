using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Palwyn.Core;
using Palwyn.Core.Link;
using Windows.Storage.Streams;

namespace Palwyn.App;

/// <summary>
/// Contact pictures from the phone on any PersonPicture: set <c>Number</c> (a caller or sender) or <c>ContactId</c>.
/// Each picture is fetched once per run and kept in memory only; without one, the initials stay.
/// </summary>
static class ContactPhotos
{
    static readonly Dictionary<string, Task<BitmapImage?>> Cache = [];

    public static readonly DependencyProperty NumberProperty = DependencyProperty.RegisterAttached(
        "Number", typeof(string), typeof(ContactPhotos), new PropertyMetadata(null, (d, e) => Show((PersonPicture)d)));
    public static string? GetNumber(DependencyObject o) => (string?)o.GetValue(NumberProperty);
    public static void SetNumber(DependencyObject o, string? value) => o.SetValue(NumberProperty, value);

    public static readonly DependencyProperty ContactIdProperty = DependencyProperty.RegisterAttached(
        "ContactId", typeof(string), typeof(ContactPhotos), new PropertyMetadata(null, (d, e) => Show((PersonPicture)d)));
    public static string? GetContactId(DependencyObject o) => (string?)o.GetValue(ContactIdProperty);
    public static void SetContactId(DependencyObject o, string? value) => o.SetValue(ContactIdProperty, value);

    static async void Show(PersonPicture p)
    {
        p.ProfilePicture = null; // a recycled list row must not keep the last contact's face
        string? number = GetNumber(p), id = GetContactId(p);
        if ((number ?? id) is null || !ContactBook.CanRead) return;
        try
        {
            var contact = number is not null ? await ContactBook.FindAsync(number)
                : (await ContactBook.GetAsync()).FirstOrDefault(c => c.Id == id);
            if (contact is not { HasPhoto: true }) return;
            var image = await Load(contact.Id);
            if (GetNumber(p) == number && GetContactId(p) == id) p.ProfilePicture = image; // still the same row
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            // initials stay
        }
    }

    static Task<BitmapImage?> Load(string contactId)
    {
        var key = App.Current.Link.Paired?.DeviceId + "/" + contactId;
        if (!Cache.TryGetValue(key, out var task)) Cache[key] = task = Fetch(key, contactId);
        return task;
    }

    static async Task<BitmapImage?> Fetch(string key, string contactId)
    {
        try
        {
            if (await App.Current.Link.ContactPhotoAsync(contactId) is not { } jpeg) return null;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(jpeg.AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            Cache.Remove(key); // try again next time it's shown
            return null;
        }
    }
}
