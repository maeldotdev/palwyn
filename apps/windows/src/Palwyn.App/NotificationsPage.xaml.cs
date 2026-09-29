using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Palwyn.Core;
using Palwyn.Core.Link;
using Windows.System;

namespace Palwyn.App;

/// <summary>A live mirror of the phone's notification shade, minus apps turned off on this PC.</summary>
public sealed partial class NotificationsPage : Page
{
    readonly ObservableCollection<NotificationRow> _rows = [];
    bool _showHistory;
    string _query = "";

    public NotificationsPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            var app = App.Current;
            app.NotificationPosted += OnPosted;
            app.NotificationRemoved += OnRemoved;
            app.NotificationsCleared += Rebuild;
            app.HistoryChanged += OnHistoryChanged;
            app.StatusChanged += UpdateState;
            AppIcons.Arrived += OnIcon;
            AppSettings.NotificationSettingsChanged += Rebuild;
            app.ViewingNotifications = true;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            var app = App.Current;
            app.NotificationPosted -= OnPosted;
            app.NotificationRemoved -= OnRemoved;
            app.NotificationsCleared -= Rebuild;
            app.HistoryChanged -= OnHistoryChanged;
            app.StatusChanged -= UpdateState;
            AppIcons.Arrived -= OnIcon;
            AppSettings.NotificationSettingsChanged -= Rebuild;
            app.ViewingNotifications = false;
        };
    }

    static bool CanAct => App.Current.Link.Capabilities.Contains("notifications.act");

    void Rebuild()
    {
        _rows.Clear();
        var rows = _showHistory
            ? (App.Current.History?.Search(_query) ?? []).Where(e => !AppSettings.IsMuted(e.Package))
                .Select(e => new NotificationRow(e.ToNotification(), false))
            : App.Current.PhoneNotifications.Values.Where(n => !AppSettings.IsMuted(n.Package) && n.Matches(_query))
                .OrderByDescending(n => n.PostedAt).Select(n => new NotificationRow(n, CanAct));
        foreach (var r in rows) _rows.Add(r);
        UpdateState();
    }

    void OnHistoryChanged()
    {
        if (_showHistory) Rebuild();
    }

    void Views_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _showHistory = sender.SelectedItem == HistoryItem;
        Rebuild();
    }

    void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _query = sender.Text.Trim();
        Rebuild();
    }

    async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Clear notification history?",
            Content = "The history on this PC is deleted. Nothing changes on your phone.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary) App.Current.ClearHistory();
    }

    int IndexOf(string key)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Notification.Key == key) return i;
        return -1;
    }

    void OnPosted(PhoneNotification n)
    {
        if (_showHistory) return; // HistoryChanged covers it
        if (_query.Length > 0)
        {
            Rebuild();
            return;
        }
        int i = IndexOf(n.Key);
        if (AppSettings.IsMuted(n.Package)) { if (i >= 0) _rows.RemoveAt(i); }
        else if (i >= 0) _rows[i] = new NotificationRow(n, CanAct); // updated in place: a reply box elsewhere keeps its text
        else _rows.Insert(0, new NotificationRow(n, CanAct));
        UpdateState();
    }

    void OnRemoved(string key)
    {
        if (_showHistory) return; // history keeps it
        int i = IndexOf(key);
        if (i >= 0) _rows.RemoveAt(i);
        UpdateState();
    }

    void OnIcon(string package)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Notification.Package == package) _rows[i] = new NotificationRow(_rows[i].Notification, CanAct);
    }

    void UpdateState()
    {
        var link = App.Current.Link;
        var noMatches = PageState.Empty("", "No matches", $"No notification matches “{_query}”.");
        PageState? message =
            link.Paired is null ? PageState.Empty("", "No phone yet", "Pair your phone to see its notifications here.")
            : _showHistory
                ? App.Current.History is null ? PageState.Empty("", "History is off", "Turn on notification history in Settings to keep your phone's notifications here.")
                : _rows.Count > 0 ? null
                : _query.Length > 0 ? noMatches
                : PageState.Empty("", "No history yet", "Your phone's last 500 notifications are kept here, even after they're cleared on the phone.")
            : !link.IsConnected ? PageState.Offline("Your phone's notifications")
            : !link.Capabilities.Contains("notifications.read")
                ? new("", "Turn on notification access", "Open Palwyn on your phone and tap Allow next to Phone notifications on your PC. Android opens a settings page for it.")
            : _rows.Count == 0 && _query.Length > 0 ? noMatches
            : _rows.Count == 0
                ? PageState.Empty("", "You're all caught up",
                    "No notifications on your phone right now." +
                    (AppSettings.MutedApps.Count > 0 ? " Apps you turned off are hidden; change that in Settings." : ""))
            : null;
        State.Show(message);
        List.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
        ClearAllButton.Visibility = !_showHistory && message is null && CanAct && _rows.Any(r => r.Notification.Clearable)
            ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = _showHistory && App.Current.History?.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Runs a command on the phone; a failure shows in the bar at the top.</summary>
    async Task<bool> Act(string key, Func<Task> command)
    {
        App.Current.QuietFor(key); // the app will likely update this notification because of us
        try
        {
            await command();
            Problem.IsOpen = false;
            return true;
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Notification command failed: {e.Message}");
            Problem.Title = e is PhoneErrorException { Code: "FAILED" } ? "That notification changed on your phone" : "Couldn't reach your phone";
            Problem.Message = e is PhoneErrorException { Code: "FAILED" } ? "It may be gone, or the app no longer allows that action." : "";
            Problem.IsOpen = true;
            return false;
        }
    }

    static NotificationRow RowOf(object sender) => (NotificationRow)((FrameworkElement)sender).DataContext;

    async void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        var n = RowOf(sender).Notification;
        await Act(n.Key, () => App.Current.Link.DismissNotificationAsync(n.Key));
    }

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        var a = (ActionRow)((FrameworkElement)sender).DataContext;
        await Act(a.Key, () => App.Current.Link.NotificationActionAsync(a.Key, a.Index));
    }

    async void Reply_Click(object sender, RoutedEventArgs e) =>
        await Reply((TextBox)((Grid)((FrameworkElement)sender).Parent).Children[0]);

    async void Reply_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await Reply((TextBox)sender);
    }

    async Task Reply(TextBox box)
    {
        var row = RowOf(box);
        var text = box.Text.Trim();
        if (text.Length == 0 || row.ReplyAction is not { } reply) return;
        box.IsEnabled = false;
        if (await Act(row.Notification.Key, () => App.Current.Link.NotificationActionAsync(row.Notification.Key, reply.Index, text)))
            box.Text = "";
        box.IsEnabled = true;
    }

    async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        ClearAllButton.IsEnabled = false;
        foreach (var n in _rows.Select(r => r.Notification).Where(n => n.Clearable).ToList())
            if (!await Act(n.Key, () => App.Current.Link.DismissNotificationAsync(n.Key))) break;
        ClearAllButton.IsEnabled = true;
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        var n = RowOf(sender).Notification;
        AppSettings.SetMuted(n.Package, n.AppName, true);
    }
}

public sealed record ActionRow(string Key, int Index, string Title);

public sealed class NotificationRow
{
    public NotificationRow(PhoneNotification n, bool canAct)
    {
        Notification = n;
        Icon = AppIcons.Has(n.Package) ? new BitmapImage(AppIcons.UriFor(n.Package)) : null;
        ReplyAction = canAct ? n.Actions.FirstOrDefault(a => a.Reply) : null;
        Buttons = canAct ? n.Actions.Where(a => !a.Reply).Select(a => new ActionRow(n.Key, a.Index, a.Title)).ToList() : [];
        CanDismiss = canAct && n.Clearable;
    }

    public PhoneNotification Notification { get; }
    public ImageSource? Icon { get; }
    public bool NoIcon => Icon is null;
    public string Letter => Notification.AppName.Length > 0 ? Notification.AppName[..1].ToUpperInvariant() : "?";
    public string Source => $"{Notification.AppName}, {MessagesPage.When(Notification.PostedAt, compact: true)}";
    public string Title => Notification.Title ?? "";
    public bool HasTitle => Notification.Title is not null;
    public string Text => Notification.Text ?? "";
    public bool HasText => Notification.Text is not null;
    public List<ActionRow> Buttons { get; }
    public bool HasButtons => Buttons.Count > 0;
    public NotificationAction? ReplyAction { get; }
    public bool HasReply => ReplyAction is not null;
    public string ReplyLabel => ReplyAction?.Title ?? "Reply";
    public bool CanDismiss { get; }
    public string MuteLabel => $"Turn off notifications from {Notification.AppName}";
}
