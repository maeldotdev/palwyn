using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.App;

/// <summary>The phone's contacts, read when the page opens and searched here. Changes are saved on the phone.</summary>
public sealed partial class ContactsPage : Page
{
    IReadOnlyList<PhoneContact> _all = [];
    PhoneContact? _open;
    string? _openId; // survives a reload, which replaces the contact objects
    bool _loading, _loaded, _rendering;

    public ContactsPage()
    {
        InitializeComponent();
        State.ActionClicked += () => _ = Load(refresh: true);
        Loaded += (_, _) =>
        {
            App.Current.StatusChanged += OnStatus;
            ContactBook.Changed += OnChanged;
            _ = Load(refresh: true);
        };
        Unloaded += (_, _) =>
        {
            App.Current.StatusChanged -= OnStatus;
            ContactBook.Changed -= OnChanged;
        };
    }

    void OnStatus()
    {
        if (!_loaded && !_loading && Blocked() is null) _ = Load(refresh: true);
        UpdateButtons();
    }

    void OnChanged() => _ = Load();

    static PageState? Blocked()
    {
        var link = App.Current.Link;
        if (link.Paired is null) return PageState.Empty("", "No phone yet", "Pair your phone to see its contacts here.");
        if (!link.IsConnected) return PageState.Offline("Contacts");
        if (!link.Capabilities.Contains("contacts.read")) return PageState.NeedsPermission("contacts", "Contacts on your PC");
        return null;
    }

    async Task Load(bool refresh = false)
    {
        if (_loading) return;
        if (Blocked() is { } why)
        {
            _loaded = false;
            ShowMessage(why);
            UpdateButtons();
            return;
        }
        _loading = true;
        if (_all.Count == 0)
        {
            State.Show(null);
            Placeholder.Visibility = Visibility.Visible;
        }
        else LoadingBar.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        try
        {
            _all = await ContactBook.GetAsync(refresh);
            _loaded = true;
            // Keep the open contact in step with the phone: new details after an edit, closed after a delete.
            if (_openId is not null) Open(_all.FirstOrDefault(c => c.Id == _openId));
            Render();
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Contacts failed: {e.Message}");
            _loaded = false;
            ShowMessage(e is PhoneErrorException { Code: "PERMISSION_DENIED" }
                ? PageState.NeedsPermission("contacts", "Contacts on your PC")
                : PageState.Failed("contacts"));
        }
        finally
        {
            _loading = false;
            LoadingBar.Visibility = Placeholder.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    void Render()
    {
        var query = Search.Text;
        var shown = _all.Where(c => c.Matches(query)).ToList();
        if (shown.Count == 0)
        {
            ShowMessage(_all.Count == 0
                ? PageState.Empty("", "No contacts yet", "Contacts with a phone number or email show up here. Add one with +.")
                : PageState.Empty("", "No matches", $"No contact matches “{query.Trim()}”."));
            return;
        }
        var groups = shown
            .GroupBy(c => char.IsLetter(c.Name.FirstOrDefault()) ? char.ToUpper(c.Name[0], CultureInfo.CurrentCulture).ToString() : "#")
            .Select(g => new ContactGroup(g.Key, g.Select(c => new ContactRow(c)).ToList()))
            .ToList();
        _rendering = true;
        List.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups, ItemsPath = new PropertyPath("Items") }.View;
        List.SelectedItem = groups.SelectMany(g => g.Items).FirstOrDefault(r => r.Contact.Id == _openId);
        _rendering = false;
        List.Visibility = Visibility.Visible;
        State.Show(null);
    }

    void ShowMessage(PageState state)
    {
        State.Show(state);
        List.Visibility = Visibility.Collapsed;
    }

    void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_loaded) Render();
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rendering && List.SelectedItem is ContactRow row) Open(row.Contact);
    }

    void Open(PhoneContact? c)
    {
        _open = c;
        _openId = c?.Id;
        NoSelection.Visibility = c is null ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = c is null ? Visibility.Collapsed : Visibility.Visible;
        if (c is null) return;
        DetailName.Text = c.Name;
        DetailPicture.DisplayName = c.Name;
        ContactPhotos.SetContactId(DetailPicture, c.Id);
        Fields.ItemsSource = c.Numbers.Select(n => new ContactField("", n.Number, n.Label ?? "Phone", IsPhone: true))
            .Concat(c.Emails.Select(e => new ContactField("", e, "Email")))
            .ToList();
    }

    void UpdateButtons()
    {
        bool write = ContactBook.CanWrite;
        NewButton.IsEnabled = EditButton.IsEnabled = DeleteButton.IsEnabled = write;
        WriteHint.Visibility = write || !App.Current.Link.IsConnected ? Visibility.Collapsed : Visibility.Visible;
    }

    async void New_Click(object sender, RoutedEventArgs e) => Show(await ContactEditor.ShowAsync(XamlRoot, null));

    async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_open is { } c) Show(await ContactEditor.ShowAsync(XamlRoot, c));
    }

    /// <summary>Opens the contact just saved: now if the list is already read again, else when that finishes.</summary>
    void Show(string? id)
    {
        if (id is null) return;
        _openId = id;
        if (_loading) return;
        Open(_all.FirstOrDefault(c => c.Id == id));
        Render();
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_open is { } c) await ContactBook.DeleteAsync(XamlRoot, c);
    }

    void Copy_Click(object sender, RoutedEventArgs e) => ContactBook.Copy(((ContactField)((FrameworkElement)sender).DataContext).Value);

    void Message_Click(object sender, RoutedEventArgs e) =>
        App.Current.ShowMain("messages:to:" + ((ContactField)((FrameworkElement)sender).DataContext).Value);

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Load(refresh: true);
}

public sealed class ContactGroup(string key, List<ContactRow> items)
{
    public string Key { get; } = key;
    public List<ContactRow> Items { get; } = items;
}

public sealed class ContactRow(PhoneContact c)
{
    public PhoneContact Contact => c;
    public string Name => c.Name;
    public string Detail => c.Numbers.FirstOrDefault()?.Number ?? c.Emails.FirstOrDefault() ?? "";
}

public sealed record ContactField(string Glyph, string Value, string Label, bool IsPhone = false);
