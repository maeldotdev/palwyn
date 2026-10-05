using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.Linux;

/// <summary>Calls, Messages and Notifications: the same hub requests and wording as the Windows pages.</summary>
public sealed partial class MainWindow
{
    static readonly IBrush Mine = new SolidColorBrush(Color.Parse("#4012A67C")); // brand green, see-through
    string? _loadError;

    /// <summary>Shown instead of a page that needs the phone while it isn't connected.</summary>
    static Control? NotConnected(string what) => Link.IsConnected ? null
        : Text(App.Host.Status.State == ConnectionState.NotPaired ? "Pair a phone first (Add a phone)." : $"Connect your phone to see {what}.", opacity: 0.8);

    static string Failure(Exception e) => e switch
    {
        PhoneErrorException { Code: "PERMISSION_DENIED" } => "Palwyn on the phone needs permission for this: open it on the phone.",
        TimeoutException => "The phone didn't answer. Try again.",
        _ => "That didn't work. Try again.",
    };

    // ---- Calls ----

    IReadOnlyList<CallLogEntry>? _calls;

    async Task LoadCallsAsync()
    {
        if (!Link.IsConnected) return;
        try
        {
            _calls = await Link.CallHistoryAsync(100);
            _loadError = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _loadError = Failure(e);
        }
        RefreshIf("calls");
    }

    Control CallsPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(Heading("Calls"));
        if (App.Host.Call is { State: not CallState.Ended } call)
            page.Children.Add(Text($"{(call.State == CallState.Ringing ? call.Incoming ? "Incoming call" : "Calling" : "On a call")}: {call.Title}", 16, FontWeight.SemiBold));
        if (NotConnected("your calls") is { } notice) return Add(page, notice);
        if (_loadError is { } error) page.Children.Add(Text(error, opacity: 0.8));
        if (_calls is null) return Add(page, Text("Loading…", opacity: 0.7));
        if (_calls.Count == 0) page.Children.Add(Text("No calls yet.", opacity: 0.7));
        foreach (var day in _calls.GroupBy(c => c.Date.ToLocalTime().Date))
        {
            page.Children.Add(Text(DayText(day.Key), 13, FontWeight.SemiBold, 0.7));
            foreach (var c in day)
            {
                var kind = c.Kind switch
                {
                    CallKind.In => "Incoming", CallKind.Out => "Outgoing", CallKind.Missed => "Missed",
                    CallKind.Rejected => "Declined", CallKind.Blocked => "Blocked", _ => "Voicemail",
                };
                var length = c.Duration > TimeSpan.Zero ? $" · {(int)c.Duration.TotalMinutes}:{c.Duration.Seconds:00}" : "";
                page.Children.Add(new StackPanel
                {
                    Children = { Text(c.Title, weight: c.Kind == CallKind.Missed ? FontWeight.SemiBold : FontWeight.Normal),
                        Text($"{kind} · {c.Date.ToLocalTime():t}{length}", 12, opacity: 0.7) },
                });
            }
        }
        return page;
    }

    static string DayText(DateTime day) =>
        day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("dddd d MMMM");

    static StackPanel Add(StackPanel page, Control c)
    {
        page.Children.Add(c);
        return page;
    }

    // ---- Messages ----

    IReadOnlyList<SmsThread>? _threads;
    SmsThread? _thread;
    List<SmsMessage>? _messages;
    readonly TextBox _composer = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, PlaceholderText = "Text message" };
    readonly TextBlock _composerInfo = new() { FontSize = 12, Opacity = 0.7 };
    string? _sendError;
    bool _sending;

    async Task LoadThreadsAsync()
    {
        _thread = null;
        if (!Link.IsConnected) return;
        try
        {
            _threads = await Link.SmsThreadsAsync(50);
            _loadError = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _loadError = Failure(e);
        }
        RefreshIf("messages");
    }

    async Task OpenThreadAsync(string threadId)
    {
        _thread = _threads?.FirstOrDefault(t => t.Id == threadId);
        _messages = null;
        UpdateViewing();
        RefreshIf("messages");
        if (!Link.IsConnected) return;
        try
        {
            if (_thread is null) _thread = (await Link.SmsThreadsAsync(50)).FirstOrDefault(t => t.Id == threadId);
            _messages = [.. (await Link.SmsMessagesAsync(threadId, 50)).OrderBy(m => m.Date)];
            _loadError = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _loadError = Failure(e);
        }
        UpdateViewing();
        RefreshIf("messages");
        Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Background); // newest at the bottom
    }

    void OnSmsReceived(SmsMessage m)
    {
        if (_thread?.Id == m.ThreadId && _messages is not null && _messages.All(x => x.Id != m.Id))
        {
            _messages.Add(m);
            RefreshIf("messages");
            Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Background);
        }
        else if (_page == "messages" && _thread is null) _ = LoadThreadsAsync();
    }

    /// <summary>The conversation open in the focused window: its texts need no desktop notification.</summary>
    void UpdateViewing() => App.Host.ViewingThread = IsActive && _page == "messages" ? _thread?.Id : null;

    Control MessagesPage()
    {
        var page = new StackPanel { Spacing = 12 };
        if (_thread is { } thread) return ThreadView(page, thread);
        page.Children.Add(Heading("Messages"));
        if (NotConnected("your messages") is { } notice) return Add(page, notice);
        if (_loadError is { } error) page.Children.Add(Text(error, opacity: 0.8));
        if (_threads is null) return Add(page, Text("Loading…", opacity: 0.7));
        if (_threads.Count == 0) page.Children.Add(Text("No conversations yet.", opacity: 0.7));
        foreach (var t in _threads)
        {
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Children = { Text(t.Title, weight: t.Unread ? FontWeight.SemiBold : FontWeight.Normal),
                        Text($"{t.Date.ToLocalTime():d MMM, t}  {t.Snippet}", 12, opacity: 0.7) },
                },
            };
            b.Click += (_, _) => _ = OpenThreadAsync(t.Id);
            page.Children.Add(b);
        }
        return page;
    }

    Control ThreadView(StackPanel page, SmsThread thread)
    {
        var back = new Button { Content = "‹ Conversations" };
        back.Click += (_, _) => { _thread = null; _messages = null; UpdateViewing(); _ = LoadThreadsAsync(); Refresh(); };
        page.Children.Add(back);
        page.Children.Add(Heading(thread.Title));
        if (NotConnected("this conversation") is { } notice) return Add(page, notice);
        if (_loadError is { } error) page.Children.Add(Text(error, opacity: 0.8));
        if (_messages is null) return Add(page, Text("Loading…", opacity: 0.7));
        foreach (var m in _messages)
        {
            var body = new StackPanel { Spacing = 2 };
            if (thread.IsGroup && !m.Outgoing) body.Children.Add(Text(thread.NameFor(m.Address), 12, FontWeight.SemiBold, 0.8));
            body.Children.Add(Text(m.Preview));
            body.Children.Add(Text(m.Status switch
            {
                SmsStatus.Sending => "Sending…",
                SmsStatus.Failed => "Not sent",
                _ => $"{m.Date.ToLocalTime():d MMM, t}",
            }, 11, opacity: 0.6));
            page.Children.Add(new Border
            {
                Background = m.Outgoing ? Mine : Tile,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8),
                MaxWidth = 440,
                HorizontalAlignment = m.Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = body,
            });
        }

        UpdateComposerInfo();
        page.Children.Add(Keep(_composer));
        var send = new Button { Content = _sending ? "Sending…" : "Send", IsEnabled = !_sending, Classes = { "accent" } };
        send.Click += (_, _) => _ = SendAsync(thread);
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { send, Keep(_composerInfo) } });
        if (_sendError is { } sendError) page.Children.Add(Text(sendError, 12, opacity: 0.8));
        return page;
    }

    void UpdateComposerInfo()
    {
        var text = _composer.Text ?? "";
        var (parts, unicode) = SmsLength.Measure(text);
        // There's no emoji button: the desktop's own picker works in the box (Ctrl+. on GNOME, Meta+. on KDE).
        _composerInfo.Text = text.Length == 0 ? "Ctrl+Enter sends" : $"{text.Length} characters · {parts} SMS{(unicode ? " (Unicode)" : "")}";
    }

    async Task SendAsync(SmsThread thread)
    {
        var body = (_composer.Text ?? "").Trim();
        if (body.Length == 0 || _sending) return;
        _sending = true;
        _sendError = null;
        Refresh();
        try
        {
            if (thread.ReplyAddress is { } address) await Link.SendSmsAsync(address, body);
            else await Link.SendGroupAsync(thread.Addresses, body);
            _composer.Text = ""; // the sent text comes back from the phone as SMS_RECEIVED
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _sendError = $"Not sent. {Failure(e)}";
        }
        _sending = false;
        Refresh();
    }

    // ---- Notifications ----

    string? _replyKey;
    readonly TextBox _reply = new() { PlaceholderText = "Reply", MinWidth = 300 };
    readonly TextBox _search = new() { PlaceholderText = "Search history", MinWidth = 300 };
    string? _actionError;

    Control NotificationsPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(Heading("Notifications"));
        if (_actionError is { } error) page.Children.Add(Text(error, opacity: 0.8));
        var live = App.Host.Notifications.Values.OrderByDescending(n => n.PostedAt).ToList();
        if (NotConnected("your phone's notifications") is { } notice) page.Children.Add(notice);
        else if (live.Count == 0) page.Children.Add(Text("Nothing on your phone right now.", opacity: 0.7));
        foreach (var n in live) page.Children.Add(NotificationView(n));

        if (App.Host.History is { } history)
        {
            page.Children.Add(Text("Earlier", 16, FontWeight.SemiBold));
            page.Children.Add(Keep(_search));
            var shown = history.Search(_search.Text ?? "").Take(100).ToList();
            if (shown.Count == 0) page.Children.Add(Text("No notifications here yet.", 12, opacity: 0.7));
            foreach (var e in shown)
                page.Children.Add(new StackPanel
                {
                    Children = { Text($"{e.AppName} · {e.PostedAt.ToLocalTime():d MMM, t}", 12, opacity: 0.7),
                        Text(e.Title ?? "", weight: FontWeight.SemiBold), Text(e.Text ?? "", 13) },
                });
        }
        return page;
    }

    Control NotificationView(PhoneNotification n)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Text($"{n.AppName} · {n.PostedAt.ToLocalTime():t}", 12, opacity: 0.7));
        if (n.Title is { } title) body.Children.Add(Text(title, weight: FontWeight.SemiBold));
        if (n.Text is { } text) body.Children.Add(Text(text, 13));

        var buttons = new WrapPanel();
        foreach (var a in n.Actions)
        {
            var b = new Button { Content = a.Title, Margin = new Thickness(0, 4, 8, 0) };
            b.Click += (_, _) =>
            {
                if (a.Reply)
                {
                    _replyKey = n.Key;
                    Refresh();
                    _reply.Focus();
                }
                else _ = ActAsync(() => Link.NotificationActionAsync(n.Key, a.Index));
            };
            buttons.Children.Add(b);
        }
        if (n.Clearable)
        {
            var dismiss = new Button { Content = "Dismiss", Margin = new Thickness(0, 4, 8, 0) };
            dismiss.Click += (_, _) => _ = ActAsync(() => Link.DismissNotificationAsync(n.Key));
            buttons.Children.Add(dismiss);
        }
        body.Children.Add(buttons);

        if (_replyKey == n.Key && n.Actions.FirstOrDefault(a => a.Reply) is { } replyAction)
        {
            var send = new Button { Content = "Send", Classes = { "accent" } };
            send.Click += (_, _) => _ = ReplyAsync(n, replyAction);
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Keep(_reply), send } });
        }
        return new Border { Background = Tile, CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Child = body };
    }

    async Task ReplyAsync(PhoneNotification n, NotificationAction action)
    {
        var text = (_reply.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (await ActAsync(() => Link.NotificationActionAsync(n.Key, action.Index, text)))
        {
            _reply.Text = "";
            _replyKey = null;
            Refresh();
        }
    }

    async Task<bool> ActAsync(Func<Task> act)
    {
        try
        {
            await act();
            _actionError = null;
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _actionError = Failure(e);
            RefreshIf("notifications");
            return false;
        }
    }

    /// <summary>Keys for the kept text boxes: Ctrl+Enter sends a text, Enter sends a reply, typing filters history.</summary>
    void HookTextBoxes()
    {
        _composer.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _thread is { } t)
            {
                e.Handled = true;
                _ = SendAsync(t);
            }
        };
        _composer.TextChanged += (_, _) => UpdateComposerInfo();
        _reply.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _replyKey is { } key && App.Host.Notifications.GetValueOrDefault(key) is { } n
                && n.Actions.FirstOrDefault(a => a.Reply) is { } a)
            {
                e.Handled = true;
                _ = ReplyAsync(n, a);
            }
        };
        _search.TextChanged += (_, _) => RefreshIf("notifications");
    }
}
