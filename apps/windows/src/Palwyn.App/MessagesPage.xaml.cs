using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Palwyn.Core;
using Palwyn.Core.Link;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Palwyn.App;

/// <summary>
/// Conversations and one open conversation, read live from the phone's SMS store. Nothing is stored on the PC.
/// Navigation parameter: a thread id to open (from a notification).
/// </summary>
public sealed partial class MessagesPage : Page
{
    const int ThreadPageSize = 50, MessagePageSize = 50;
    /// <summary>Conversations read on this PC since Palwyn started. The phone's read flag can't be changed
    /// by a non-default SMS app, so without this they would stay bold until read on the phone.</summary>
    static readonly Dictionary<string, DateTimeOffset> SeenHere = [];

    readonly List<SmsThread> _threads = [];
    readonly ObservableCollection<BubbleRow> _bubbles = [];
    SmsThread? _open;
    bool _new; // writing a new message: the To box replaces the header
    string? _openNumber; // open this number's conversation once the list has it (after a new message, or from a contact)
    string? _openWhenLoaded;
    bool _loadingThreads, _threadsDirty, _threadsLoaded, _loadingMessages, _rendering;
    int _localIds, _searchVersion;
    string _query = "";

    public MessagesPage()
    {
        InitializeComponent();
        ThreadsState.ActionClicked += () => _ = LoadThreads();
        Bubbles.ItemsSource = _bubbles;
        EmojiGrid.ItemsSource = Emoji;
        Loaded += (_, _) =>
        {
            App.Current.StatusChanged += OnStatus;
            App.Current.SmsReceived += OnSms;
            ContactBook.Changed += OnContactsChanged;
            _ = LoadThreads();
        };
        Unloaded += (_, _) =>
        {
            App.Current.StatusChanged -= OnStatus;
            App.Current.SmsReceived -= OnSms;
            ContactBook.Changed -= OnContactsChanged;
            App.Current.ViewingThreadId = null;
        };
    }

    /// <summary>Parameter: a thread id to open, or "to:" and a number to start a new message to.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string p && p.StartsWith("to:", StringComparison.Ordinal)) Loaded += (_, _) => StartNew(p[3..]);
        else _openWhenLoaded = e.Parameter as string;
    }

    static PageState? Blocked()
    {
        var link = App.Current.Link;
        if (link.Paired is null) return PageState.Empty("", "No phone yet", "Pair your phone to see its text messages here.");
        if (!link.IsConnected) return PageState.Offline("Text messages");
        if (!link.Capabilities.Contains("sms.read")) return PageState.NeedsPermission("messages", "Messages on your PC");
        return null;
    }

    void OnStatus()
    {
        if (!_threadsLoaded && !_loadingThreads && Blocked() is null) _ = LoadThreads();
        UpdateComposer();
    }

    // ---- Conversations ----

    async Task LoadThreads(bool older = false)
    {
        if (_loadingThreads)
        {
            _threadsDirty = !older; // reload once the current load finishes
            return;
        }
        if (Blocked() is { } why)
        {
            _threadsLoaded = false;
            ShowThreadsMessage(why);
            return;
        }
        _loadingThreads = true;
        if (_threads.Count == 0)
        {
            ThreadsState.Show(null);
            Threads.Visibility = Visibility.Collapsed;
            ThreadsPlaceholder.Visibility = Visibility.Visible;
        }
        else ThreadsLoading.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = MoreThreadsButton.IsEnabled = false;
        try
        {
            var page = await App.Current.Link.SmsThreadsAsync(ThreadPageSize, older && _threads.Count > 0 ? _threads[^1].Date : null, _query);
            if (!older) _threads.Clear();
            _threads.AddRange(page);
            _threadsLoaded = true;
            MoreThreadsButton.Visibility = page.Count == ThreadPageSize ? Visibility.Visible : Visibility.Collapsed;
            // Names may have changed (a contact added or edited): keep the open conversation's header current.
            if (_open is not null && _threads.Find(t => t.Id == _open.Id) is { } fresh) ShowHeader(_open = fresh);
            RenderThreads();
            if (_openWhenLoaded is { } id)
            {
                _openWhenLoaded = null;
                Threads.SelectedIndex = _threads.FindIndex(t => t.Id == id); // opens it through SelectionChanged
            }
            if (_new && _openNumber is { } number && OpenExisting(number)) _openNumber = null;
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Conversations failed: {e.Message}");
            _threadsLoaded = false;
            ShowThreadsMessage(e is PhoneErrorException { Code: "PERMISSION_DENIED" }
                ? PageState.NeedsPermission("messages", "Messages on your PC")
                : PageState.Failed("conversations"));
        }
        finally
        {
            _loadingThreads = false;
            ThreadsLoading.Visibility = ThreadsPlaceholder.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = MoreThreadsButton.IsEnabled = true;
        }
        if (_threadsDirty)
        {
            _threadsDirty = false;
            await LoadThreads();
        }
    }

    void RenderThreads()
    {
        if (_threads.Count == 0 && _query.Length > 0)
        {
            ShowThreadsMessage(PageState.Empty("", "No matches", $"No conversation or text matches “{_query}”."));
            return;
        }
        if (_threads.Count == 0)
        {
            ShowThreadsMessage(PageState.Empty("", "No text messages yet", "Texts to and from your phone will be listed here."));
            return;
        }
        _rendering = true;
        Threads.ItemsSource = _threads.Select(t => new ThreadRow(t, t.Unread && !(SeenHere.TryGetValue(t.Id, out var at) && at >= t.Date))).ToList();
        Threads.SelectedIndex = _open is null ? -1 : _threads.FindIndex(t => t.Id == _open.Id);
        _rendering = false;
        Threads.Visibility = Visibility.Visible;
        ThreadsState.Show(null);
    }

    void ShowThreadsMessage(PageState state)
    {
        ThreadsState.Show(state);
        Threads.Visibility = Visibility.Collapsed;
    }

    void Threads_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || Threads.SelectedItem is not ThreadRow row) return;
        _ = OpenThread(row.Thread);
    }

    // ---- One conversation ----

    /// <summary>Opens the conversation with this number, if it's in the list. Selecting it opens it.</summary>
    bool OpenExisting(string number)
    {
        int i = _threads.FindIndex(t => t.ReplyAddress is { } a && PhoneContact.SameNumber(a, number));
        if (i >= 0) Threads.SelectedIndex = i;
        return i >= 0;
    }

    async Task OpenThread(SmsThread t)
    {
        _open = t;
        SeenHere[t.Id] = DateTimeOffset.Now;
        App.Current.ViewingThreadId = t.Id;
        Toasts.Clear(t.Id);
        RenderThreads(); // un-bold it

        _new = false;
        NewHeader.Visibility = Visibility.Collapsed;
        ThreadHeader.Visibility = Visibility.Visible;
        ShowHeader(t);
        NoSelection.Visibility = Visibility.Collapsed;
        Conversation.Visibility = Visibility.Visible;
        _bubbles.Clear();
        MoreMessagesButton.Visibility = Visibility.Collapsed;
        UpdateComposer();
        await LoadMessages();
    }

    void ShowHeader(SmsThread t)
    {
        HeaderTitle.Text = t.Title;
        HeaderSubtitle.Text = t.Addresses.Count == 1 && t.Names.Count > 0 && t.Names[0].Length > 0 ? t.Addresses[0] : "";
        HeaderSubtitle.Visibility = HeaderSubtitle.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderPicture.DisplayName = new ThreadRow(t, false).Initials;
        ContactPhotos.SetNumber(HeaderPicture, t.ReplyAddress);
        ContactButton.Visibility = t.ReplyAddress is null ? Visibility.Collapsed : Visibility.Visible; // groups: several contacts
    }

    void Contact_Click(object sender, RoutedEventArgs e) => ContactBook.ShowMenu(ContactButton, _open?.ReplyAddress, message: false);

    void OnContactsChanged() => _ = LoadThreads();

    /// <summary>Searches on the phone, once typing pauses.</summary>
    async void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        int version = ++_searchVersion;
        await Task.Delay(350);
        if (version != _searchVersion || sender.Text.Trim() == _query) return;
        _query = sender.Text.Trim();
        _threads.Clear(); // a different list: show the placeholder, not the old results
        await LoadThreads();
    }

    async Task LoadMessages(bool older = false)
    {
        if (_open is not { } t || _loadingMessages) return;
        _loadingMessages = true;
        MessagesLoading.Visibility = Visibility.Visible;
        MessagesError.Visibility = Visibility.Collapsed;
        MoreMessagesButton.IsEnabled = false;
        try
        {
            var oldest = _bubbles.FirstOrDefault(b => !b.IsLocal)?.Date;
            var page = await App.Current.Link.SmsMessagesAsync(t.Id, MessagePageSize, older ? oldest : null);
            if (_open?.Id != t.Id) return; // the user moved on while this loaded
            var rows = page.Reverse().Select(m => BubbleRow.From(m, t)).ToList(); // the phone sends newest first
            if (older) for (int i = 0; i < rows.Count; i++) _bubbles.Insert(i, rows[i]);
            else
            {
                _bubbles.Clear();
                foreach (var r in rows) _bubbles.Add(r);
                ScrollToEnd();
            }
            MoreMessagesButton.Visibility = page.Count == MessagePageSize ? Visibility.Visible : Visibility.Collapsed;
            if (_bubbles.Count == 0)
                ShowMessagesError("No messages here. Chat (RCS) messages aren't stored where Palwyn can read them.");
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Conversation failed: {e.Message}");
            if (_open?.Id == t.Id) ShowMessagesError("Couldn't load this conversation from your phone.");
        }
        finally
        {
            _loadingMessages = false;
            MessagesLoading.Visibility = Visibility.Collapsed;
            MoreMessagesButton.IsEnabled = true;
        }
    }

    void ShowMessagesError(string text)
    {
        MessagesError.Text = text;
        MessagesError.Visibility = Visibility.Visible;
    }

    void ScrollToEnd() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        if (_bubbles.Count > 0) Bubbles.ScrollIntoView(_bubbles[^1]);
    });

    void OnSms(SmsMessage m)
    {
        _ = LoadThreads(); // the conversation moves to the top with a new snippet
        if (_open?.Id != m.ThreadId || _bubbles.Any(b => b.Id == m.Id)) return;
        if (m.Outgoing)
        {
            // Our own send, now stored by the phone: swap the local placeholder for the real message.
            int i = IndexOfLocal(m.Body);
            if (i >= 0)
            {
                _bubbles[i] = BubbleRow.From(m, _open);
                return;
            }
        }
        SeenHere[m.ThreadId] = DateTimeOffset.Now;
        MessagesError.Visibility = Visibility.Collapsed;
        _bubbles.Add(BubbleRow.From(m, _open));
        ScrollToEnd();
    }

    int IndexOfLocal(string body)
    {
        for (int i = 0; i < _bubbles.Count; i++)
            if (_bubbles[i].IsLocal && _bubbles[i].Status != SmsStatus.Failed && _bubbles[i].Body == body) return i;
        return -1;
    }

    // ---- New message ----

    void New_Click(object sender, RoutedEventArgs e) => StartNew();

    /// <summary>An empty conversation with a To box. With a number that already has a conversation, opens that one.</summary>
    void StartNew(string? number = null)
    {
        _open = null;
        _new = true;
        _openNumber = number;
        App.Current.ViewingThreadId = null;
        _rendering = true;
        Threads.SelectedIndex = -1;
        _rendering = false;
        NoSelection.Visibility = ThreadHeader.Visibility = MoreMessagesButton.Visibility = MessagesError.Visibility = Visibility.Collapsed;
        Conversation.Visibility = NewHeader.Visibility = Visibility.Visible;
        _bubbles.Clear();
        ToBox.Text = number ?? "";
        if (number is not null && OpenExisting(number)) return;
        UpdateComposer();
        if (number is null) ToBox.Focus(FocusState.Programmatic);
        else Composer.Focus(FocusState.Programmatic);
    }

    /// <summary>What was typed in To, if it can be a phone number (3+ digits, only number punctuation).</summary>
    static string? AsNumber(string text)
    {
        text = text.Trim();
        return text.Count(char.IsAsciiDigit) >= 3 && text.All(ch => char.IsAsciiDigit(ch) || " +-().".Contains(ch)) ? text : null;
    }

    async void To_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        UpdateComposer();
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var query = sender.Text.Trim();
        if (query.Length == 0 || !ContactBook.CanRead)
        {
            sender.ItemsSource = null;
            return;
        }
        try
        {
            var contacts = await ContactBook.GetAsync();
            if (sender.Text.Trim() != query) return; // typed on meanwhile
            sender.ItemsSource = contacts.Where(c => c.Matches(query))
                .SelectMany(c => c.Numbers.Select(n => new Recipient(c.Name, n.Number, n.Label)))
                .Take(8).ToList();
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Contact suggestions failed: {e.Message}");
        }
    }

    void To_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is Recipient r) sender.Text = r.Number;
    }

    void To_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var number = (args.ChosenSuggestion as Recipient)?.Number ?? AsNumber(sender.Text);
        if (number is null) return;
        if (!OpenExisting(number)) Composer.Focus(FocusState.Programmatic);
    }

    // ---- Attachments ----

    async void Attachment_Click(object sender, RoutedEventArgs e) =>
        await ((AttachmentRow)((FrameworkElement)sender).DataContext).OpenAsync();

    // ---- Composer ----

    void UpdateComposer()
    {
        if (_open is null && !_new) return;
        var link = App.Current.Link;
        bool connected = link.IsConnected, canSend = connected && link.Capabilities.Contains("sms.send");
        bool haveTo = !_new || AsNumber(ToBox.Text) is not null;
        Composer.IsEnabled = EmojiButton.IsEnabled = canSend;
        SendButton.IsEnabled = canSend && haveTo && Composer.Text.Trim().Length > 0;
        ComposerHint.Text = !connected ? "Your phone isn't connected."
            : !canSend ? "To send from this PC, open Palwyn on your phone and allow Messages."
            : _open is { IsGroup: true } ? "Goes to everyone as one group message (MMS). Your carrier's MMS rate applies."
            : LengthHint(Composer.Text.Trim());
        ComposerHint.Visibility = ComposerHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Composer_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposer();

    /// <summary>SMS costs are per text, so say when a message won't fit in one (emoji shrink a text to 70 characters).</summary>
    static string LengthHint(string body)
    {
        var (parts, unicode) = SmsLength.Measure(body);
        if (parts <= 1) return "";
        return $"Sent as {parts} texts" + (unicode ? ". Emoji and some symbols fit 70 characters per text instead of 160." : ".");
    }

    /// <summary>
    /// Palwyn's own picker, not the Windows emoji panel: when Palwyn runs as administrator, Windows
    /// blocks the panel (a normal-level process) from typing into it, and the panel freezes.
    /// </summary>
    static readonly string[] Emoji =
        ("😀 😃 😄 😁 😆 😅 🤣 😂 🙂 😉 😊 😇 🥰 😍 🤩 😘 😋 😜 🤪 😎 🤗 🤭 🤔 🤐 😐 😏 😒 🙄 😬 😌 😔 😴 " +
         "😷 🤒 🥵 🥶 😵 🤯 🥳 😕 😟 😮 😲 🥺 😢 😭 😱 😖 😣 😞 😓 😩 😫 😤 😡 😠 🤬 😈 💀 💩 🤡 👻 " +
         "👍 👎 👌 ✌️ 🤞 🤟 🤙 👋 👏 🙌 🙏 💪 👀 🫶 ❤️ 🧡 💛 💚 💙 💜 🖤 🤍 💔 💕 💯 ✨ 🔥 🎉 🎂 🎁 " +
         "✅ ❌ ❓ ❗ ⭐ ☀️ 🌙 ☔ 🌸 🌹 🍀 🐶 🐱 🍕 🍔 🍟 🍜 🍰 ☕ 🍺 ⚽ 🏀 🎮 🎵 📞 📷 💰 🏠 🚗 ✈️ ⏰ 📍")
        .Split(' ');

    void Emoji_ItemClick(object sender, ItemClickEventArgs e)
    {
        var emoji = (string)e.ClickedItem;
        int at = Composer.SelectionStart;
        Composer.Text = Composer.Text.Remove(at, Composer.SelectionLength).Insert(at, emoji);
        Composer.SelectionStart = at + emoji.Length; // stays open for more; the next one goes after this
    }

    void EmojiFlyout_Closed(object sender, object e) => Composer.Focus(FocusState.Programmatic);

    void Composer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (shift) return; // Shift+Enter: new line
        e.Handled = true;
        _ = Send();
    }

    async Task Send()
    {
        var thread = _open;
        var to = _new ? AsNumber(ToBox.Text) : thread?.ReplyAddress;
        var body = Composer.Text.Trim();
        if ((to is null && thread is not { IsGroup: true }) || body.Length == 0 || !SendButton.IsEnabled) return;
        Composer.Text = "";
        var local = new BubbleRow("local-" + ++_localIds, body, DateTimeOffset.Now, true, SmsStatus.Sending);
        _bubbles.Add(local);
        MessagesError.Visibility = Visibility.Collapsed;
        ScrollToEnd();
        try
        {
            if (thread is { IsGroup: true }) await App.Current.Link.SendGroupAsync(thread.Addresses, body);
            else await App.Current.Link.SendSmsAsync(to!, body);
            Replace(local.Id, local with { Status = SmsStatus.Done });
            if (_new)
            {
                _openNumber = to; // its conversation now exists on the phone
                await LoadThreads();
            }
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Send failed: {e.Message}");
            Replace(local.Id, local with { Status = SmsStatus.Failed });
            if (Composer.Text.Length == 0) Composer.Text = body; // ready to try again
            if (thread is { IsGroup: true })
                ShowMessagesError("Group messages go as MMS, which needs your phone's mobile data. Check it's on and try again.");
        }
    }

    void Replace(string id, BubbleRow row)
    {
        for (int i = 0; i < _bubbles.Count; i++)
            if (_bubbles[i].Id == id) { _bubbles[i] = row; return; }
    }

    async void Send_Click(object sender, RoutedEventArgs e) => await Send();
    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadThreads();
    async void MoreThreads_Click(object sender, RoutedEventArgs e) => await LoadThreads(older: true);
    async void MoreMessages_Click(object sender, RoutedEventArgs e) => await LoadMessages(older: true);

    internal static string When(DateTimeOffset date, bool compact)
    {
        var d = date.LocalDateTime;
        var today = DateTime.Today;
        var c = CultureInfo.CurrentCulture;
        if (d.Date == today) return d.ToString("t", c);
        if (compact)
            return d.Date > today.AddDays(-7) ? d.ToString("ddd", c)
                : d.Year == today.Year ? d.ToString("MMM d", c) : d.ToString("d", c);
        return (d.Year == today.Year ? d.ToString("MMM d", c) : d.ToString("d", c)) + ", " + d.ToString("t", c);
    }
}

public sealed class ThreadRow(SmsThread t, bool unread)
{
    public SmsThread Thread => t;
    public string Title => t.Title;
    /// <summary>Initials for a single known contact; the generic person glyph otherwise.</summary>
    public string Initials => t.Addresses.Count == 1 && t.Names.Count > 0 ? t.Names[0] : "";
    public string? Number => t.ReplyAddress;
    public string Snippet => t.Snippet.ReplaceLineEndings(" ");
    public string When => MessagesPage.When(t.Date, compact: true);
    public Windows.UI.Text.FontWeight TitleWeight => unread ? FontWeights.SemiBold : FontWeights.Normal;
}

/// <summary>A To suggestion: one number of one contact.</summary>
public sealed record Recipient(string Name, string Number, string? Label)
{
    public string Display => Label is { Length: > 0 } ? $"{Name}  ·  {Number} ({Label})" : $"{Name}  ·  {Number}";
}

/// <param name="Sender">Who sent it, in group conversations.</param>
public sealed record BubbleRow(string Id, string Body, DateTimeOffset Date, bool Outgoing, SmsStatus Status,
    string? Sender = null, IReadOnlyList<AttachmentRow>? Parts = null)
{
    public static BubbleRow From(SmsMessage m, SmsThread? thread) => new(m.Id, m.Body, m.Date, m.Outgoing, m.Status,
        thread is { IsGroup: true } && !m.Outgoing ? thread.NameFor(m.Address) : null,
        m.Attachments.Select(p => new AttachmentRow(p, m.Outgoing)).ToList());

    public bool IsLocal => Id.StartsWith("local-");
    public IReadOnlyList<AttachmentRow> Attachments => Parts ?? [];
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasSender => Sender is not null;
    public bool OutgoingText => Outgoing && Body.Length > 0;
    public bool IncomingText => !Outgoing && Body.Length > 0;
    public HorizontalAlignment Align => Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public bool Failed => Status == SmsStatus.Failed;
    public bool NotFailed => !Failed;
    public string Meta => Status switch
    {
        SmsStatus.Sending => "Sending…",
        SmsStatus.Failed => "Not sent",
        _ => MessagesPage.When(Date, compact: false),
    };
}

/// <summary>
/// An MMS attachment in a bubble. Pictures are copied to the "Open" folder (emptied at every start) and shown;
/// other kinds are copied when clicked.
/// </summary>
public sealed partial class AttachmentRow : INotifyPropertyChanged
{
    static readonly Dictionary<string, Task<string?>> Files = []; // one copy per part, shared by every bubble showing it
    readonly MmsPart _part;

    public AttachmentRow(MmsPart part, bool outgoing)
    {
        _part = part;
        Align = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        if (part.IsImage) _ = ShowAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public HorizontalAlignment Align { get; }
    public BitmapImage? Image { get; private set; }
    public bool ShowLabel => Image is null;
    public string Label => _part.IsImage ? "Picture" : _part.Mime.StartsWith("video/") ? "Video"
        : _part.Mime.StartsWith("audio/") ? "Audio" : _part.Name ?? "Attachment";
    public string Glyph => _part.IsImage ? "" : _part.Mime.StartsWith("video/") ? ""
        : _part.Mime.StartsWith("audio/") ? "" : "";

    Task<string?> CopyAsync()
    {
        var phone = App.Current.Link.Paired?.DeviceId ?? "";
        var key = phone + "/" + _part.Id;
        if (!Files.TryGetValue(key, out var task) || (task.IsCompleted && task.Result is null))
            Files[key] = task = Copy(Path.Combine(PhotosPage.OpenFolder,
                $"mms-{phone[..Math.Min(8, phone.Length)]}-{_part.Id}-{PhonePhoto.SafeFileName(_part.Name, _part.Mime)}"), _part.Id);
        return task;
    }

    static async Task<string?> Copy(string path, string partId)
    {
        if (File.Exists(path)) return path;
        try
        {
            Directory.CreateDirectory(PhotosPage.OpenFolder);
            await App.Current.Link.DownloadAsync("mms", partId, path, null, CancellationToken.None);
            return path;
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            Log.Info($"MMS attachment failed: {e.Message}");
            return null;
        }
    }

    async Task ShowAsync()
    {
        if (await CopyAsync() is not { } path) return;
        var image = new BitmapImage { DecodePixelWidth = 560 }; // 280 px on screen, sharp at 200% scaling
        try
        {
            using var file = File.OpenRead(path);
            await image.SetSourceAsync(file.AsRandomAccessStream());
        }
        catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException)
        {
            return; // not a picture Windows can show: the label stays, and clicking opens it
        }
        Image = image;
        PropertyChanged?.Invoke(this, new(nameof(Image)));
        PropertyChanged?.Invoke(this, new(nameof(ShowLabel)));
    }

    public async Task OpenAsync()
    {
        if (await CopyAsync() is { } path) await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
    }
}
