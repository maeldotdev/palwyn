using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.App;

/// <summary>The phone's recent calls, read from its call log on demand. Nothing is stored on the PC.</summary>
public sealed partial class CallsPage : Page
{
    const int PageSize = 50;
    readonly List<CallLogEntry> _entries = [];
    bool _loading, _loaded, _reload;
    string _query = "";
    int _searchVersion;

    public CallsPage()
    {
        InitializeComponent();
        State.ActionClicked += () => _ = Load();
        Loaded += (_, _) =>
        {
            App.Current.StatusChanged += OnStatus;
            App.Current.CallUpdated += OnCall;
            ContactBook.Changed += OnContactsChanged;
            _ = Load();
        };
        Unloaded += (_, _) =>
        {
            App.Current.StatusChanged -= OnStatus;
            App.Current.CallUpdated -= OnCall;
            ContactBook.Changed -= OnContactsChanged;
        };
    }

    // Status updates arrive for every battery change; only act when the page is waiting to be able to load.
    void OnStatus()
    {
        if (!_loaded && !_loading && CanLoad() is null) _ = Load();
    }

    async void OnCall(PhoneCall call)
    {
        if (call.State != CallState.Ended) return;
        await Task.Delay(2000); // the phone writes the call-log row shortly after the call ends
        await Load();
    }

    static PageState? CanLoad()
    {
        var link = App.Current.Link;
        if (link.Paired is null) return PageState.Empty("", "No phone yet", "Pair your phone to see its recent calls here.");
        if (!link.IsConnected) return PageState.Offline("Recent calls");
        if (!link.Capabilities.Contains("calls.log")) return PageState.NeedsPermission("calls", "Calls on your PC");
        return null;
    }

    async Task Load(bool older = false)
    {
        if (_loading)
        {
            _reload |= !older; // a new search or a contact change: load again once this one finishes
            return;
        }
        if (CanLoad() is { } why)
        {
            _loaded = false;
            ShowMessage(why);
            return;
        }
        _loading = true;
        // First load: a placeholder shaped like the list. Refresh: the thin bar, keeping what's shown.
        if (_entries.Count == 0)
        {
            State.Show(null);
            List.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
        }
        else LoadingBar.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = MoreButton.IsEnabled = false;
        try
        {
            var page = await App.Current.Link.CallHistoryAsync(PageSize, older && _entries.Count > 0 ? _entries[^1].Date : null, _query);
            if (!older) _entries.Clear();
            _entries.AddRange(page);
            _loaded = true;
            MoreButton.Visibility = page.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
            if (_entries.Count == 0 && _query.Length > 0)
                ShowMessage(PageState.Empty("", "No matches", $"No calls with a name or number matching “{_query}”."));
            else if (_entries.Count == 0) ShowMessage(PageState.Empty("", "No calls yet", "Calls to and from your phone will be listed here."));
            else ShowList();
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Call history failed: {e.Message}");
            _loaded = false;
            ShowMessage(e is PhoneErrorException { Code: "PERMISSION_DENIED" }
                ? PageState.NeedsPermission("calls", "Calls on your PC")
                : PageState.Failed("calls"));
        }
        finally
        {
            _loading = false;
            LoadingBar.Visibility = Placeholder.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = MoreButton.IsEnabled = true;
        }
        if (_reload)
        {
            _reload = false;
            await Load();
        }
    }

    void ShowList()
    {
        var today = DateTime.Today;
        var groups = _entries
            .GroupBy(e => e.Date.LocalDateTime.Date)
            .Select(g => new CallGroup(DayLabel(g.Key, today), g.Select(e => new CallRow(e)).ToList()))
            .ToList();
        List.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups, ItemsPath = new PropertyPath("Items") }.View;
        List.Visibility = Visibility.Visible;
        State.Show(null);
    }

    void ShowMessage(PageState state)
    {
        State.Show(state);
        List.Visibility = Visibility.Collapsed;
    }

    static string DayLabel(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.Year == today.Year ? day.ToString("dddd, MMMM d", CultureInfo.CurrentCulture)
        : day.ToString("D", CultureInfo.CurrentCulture);

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Load();
    async void More_Click(object sender, RoutedEventArgs e) => await Load(older: true);

    // Names come from the phone's contacts, so an added or renamed contact shows after a reload.
    void OnContactsChanged() => _ = Load();

    void Contact_Click(object sender, RoutedEventArgs e) =>
        ContactBook.ShowMenu((FrameworkElement)sender, ((CallRow)((FrameworkElement)sender).DataContext).Number);

    /// <summary>Searches on the phone, once typing pauses.</summary>
    async void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        int version = ++_searchVersion;
        await Task.Delay(350);
        if (version != _searchVersion || sender.Text.Trim() == _query) return;
        _query = sender.Text.Trim();
        _entries.Clear(); // a different list: show the placeholder, not the old results
        await Load();
    }
}

public sealed class CallGroup(string key, List<CallRow> items)
{
    public string Key { get; } = key;
    public List<CallRow> Items { get; } = items;
}

public sealed class CallRow(CallLogEntry e)
{
    public string Title => e.Title;
    public string? Number => e.Number;
    /// <summary>PersonPicture shows initials for a name and the generic person glyph for "".</summary>
    public string Initials => e.Name ?? "";
    public bool Missed => e.Kind == CallKind.Missed;
    public bool Normal => !Missed;
    public string Time => e.Date.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);

    public string Detail
    {
        get
        {
            string kind = e.Kind switch
            {
                CallKind.In => "Incoming",
                CallKind.Out => "Outgoing",
                CallKind.Missed => "Missed",
                CallKind.Rejected => "Declined",
                CallKind.Blocked => "Blocked",
                _ => "Voicemail",
            };
            // Show the number under a contact name; answered calls also get their length.
            var parts = new List<string> { kind };
            if (e.Duration > TimeSpan.Zero && e.Kind is CallKind.In or CallKind.Out) parts.Add(Length(e.Duration));
            if (e.Name is not null && e.Number is not null) parts.Add(e.Number);
            return string.Join(", ", parts);
        }
    }

    static string Length(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s"
        : $"{t.Seconds} s";
}
