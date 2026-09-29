using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Palwyn.App.Link;
using Palwyn.Core;
using Palwyn.Core.Link;
using Windows.ApplicationModel.DataTransfer;

namespace Palwyn.App;

/// <summary>
/// The phone's contacts, read from the phone and kept in memory only (never on disk), so Calls and Messages
/// can tell whether a number is saved. Every change is made on the phone; the list is then read again.
/// </summary>
static class ContactBook
{
    const int PageSize = 500, MaxPages = 20; // ponytail: stops at 10 000 contacts; page on scroll if anyone has more
    static IReadOnlyList<PhoneContact>? _all;
    static string? _owner; // the phone the list came from
    static Task<IReadOnlyList<PhoneContact>>? _loading;

    /// <summary>A contact was added, edited or deleted from this PC.</summary>
    public static event Action? Changed;

    static LinkManager Link => App.Current.Link;
    public static bool CanRead => Link.IsConnected && Link.Capabilities.Contains("contacts.read");
    public static bool CanWrite => Link.IsConnected && Link.Capabilities.Contains("contacts.write");

    public static Task<IReadOnlyList<PhoneContact>> GetAsync(bool refresh = false)
    {
        if (refresh || _owner != Link.Paired?.DeviceId) _all = null;
        if (_all is { } all) return Task.FromResult(all);
        return _loading ??= LoadAsync();
    }

    static async Task<IReadOnlyList<PhoneContact>> LoadAsync()
    {
        try
        {
            var owner = Link.Paired?.DeviceId;
            var all = new List<PhoneContact>();
            for (int i = 0; i < MaxPages; i++)
            {
                var (page, more) = await Link.ContactsAsync(PageSize, all.Count);
                all.AddRange(page);
                if (!more || page.Count == 0) break;
            }
            _owner = owner;
            return _all = all;
        }
        finally
        {
            _loading = null;
        }
    }

    public static async Task<PhoneContact?> FindAsync(string number) => (await GetAsync()).FirstOrDefault(c => c.HasNumber(number));

    /// <summary>Returns the saved contact's id.</summary>
    public static async Task<string> SaveAsync(PhoneContact contact)
    {
        var id = await Link.SaveContactAsync(contact);
        _all = null;
        Changed?.Invoke();
        return id;
    }

    /// <summary>Asks first. True when the contact was deleted.</summary>
    public static async Task<bool> DeleteAsync(XamlRoot root, PhoneContact contact)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = $"Delete {contact.Name}?",
            Content = "The contact is deleted from your phone, and from any account it syncs to, such as Google.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return false;
        try
        {
            await Link.DeleteContactAsync(contact.Id);
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Contact delete failed: {e.Message}");
            await new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = confirm.RequestedTheme,
                Title = "Couldn't delete the contact",
                Content = Problem(e),
                CloseButtonText = "OK",
            }.ShowAsync();
            return false;
        }
        _all = null;
        Changed?.Invoke();
        return true;
    }

    public static string Problem(Exception e) => e is PhoneErrorException { Code: "PERMISSION_DENIED" or "NOT_CAPABLE" }
        ? "Open Palwyn on your phone and tap Allow next to Contacts on your PC."
        : "Your phone didn't answer. Check that it's connected and try again.";

    public static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    /// <summary>The contact menu for a number on the Calls and Messages pages: add it, or edit or delete its contact.</summary>
    /// <param name="message">Offer "Send message" (not on the Messages page, where the conversation is already open).</param>
    public static async void ShowMenu(FrameworkElement anchor, string? number, bool message = true)
    {
        PhoneContact? contact = null;
        if (number is not null && CanRead)
        {
            try { contact = await FindAsync(number); }
            catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
            {
                Log.Info($"Contact lookup failed: {e.Message}");
            }
        }
        var root = anchor.XamlRoot;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, Action click, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }
        if (contact is null)
            Item("Add to contacts", "", () => _ = ContactEditor.ShowAsync(root, null, number), CanWrite && number is not null);
        else
        {
            Item("Edit contact", "", () => _ = ContactEditor.ShowAsync(root, contact), CanWrite);
            Item("Delete contact", "", () => _ = DeleteAsync(root, contact), CanWrite);
        }
        if (number is not null && message) Item("Send message", "", () => App.Current.ShowMain("messages:to:" + number));
        if (number is not null) Item("Copy number", "", () => Copy(number));
        if (!CanWrite)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Item(Link.IsConnected ? "Allow Contacts on your phone to change them" : "Connect your phone to change contacts", "", () => { }, false);
        }
        menu.ShowAt(anchor);
    }
}
