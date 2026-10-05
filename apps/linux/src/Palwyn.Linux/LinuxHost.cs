using System.Text.Json.Nodes;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.Linux;

/// <summary>
/// The link hub's view of the Linux app: its UI thread (or, headless, a serial work queue), log, settings, the
/// phone's live state (notifications, call) and desktop notifications. What the Linux app doesn't do yet is left out
/// of <see cref="PcCapabilities"/>, so the phone never sends it.
/// </summary>
/// <param name="post">Runs work on the UI thread; null for the headless app.</param>
public sealed class LinuxHost(Settings settings, Action<Action>? post = null) : IPcHost
{
    readonly DesktopNotifier _notifier = new();
    readonly Lock _gate = new();
    Task _work = Task.CompletedTask;
    NotificationHistory? _history;
    string? _lastCallLogged;

    /// <summary>Set once the hub exists: the buttons on desktop notifications act through it.</summary>
    public LinkManager? Link { get; set; }
    /// <summary>The window, when there is one: opens a page ("notifications", "messages", "calls") at an item.</summary>
    public Action<string, string?>? Open { get; set; }
    /// <summary>The window's clipboard, when there is one.</summary>
    public ClipboardSync? Clipboard { get; set; }
    /// <summary>The conversation the user is reading in the focused window: its texts need no desktop notification.</summary>
    public string? ViewingThread { get; set; }

    public PhoneStatus Status { get; private set; } = PhoneStatus.NotPaired;
    public event Action? StatusChanged;
    /// <summary>Newest first, for Home.</summary>
    public List<(DateTimeOffset At, string Text)> Activity { get; } = [];
    public event Action? ActivityChanged;
    /// <summary>What's in the phone's notification shade, by key.</summary>
    public Dictionary<string, PhoneNotification> Notifications { get; } = [];
    public event Action? NotificationsChanged;
    /// <summary>The phone's current or last call; null when the link dropped.</summary>
    public PhoneCall? Call { get; private set; }
    public event Action? CallChanged;
    public event Action<SmsMessage>? SmsReceived;

    /// <summary>Headless: runs work one item at a time, in order, like a UI thread would. Also keeps the notifier
    /// (which runs processes) off the UI thread and in order.</summary>
    void Then(Func<Task> work)
    {
        lock (_gate)
            _work = _work.ContinueWith(async _ =>
            {
                try { await work(); }
                catch (Exception e) { Log($"Failed: {e.GetType().Name}: {e.Message}"); }
            }, TaskScheduler.Default).Unwrap();
    }

    public void Post(Action action)
    {
        if (post is not null) post(action);
        else Then(() => { action(); return Task.CompletedTask; });
    }

    public void Log(string line) => Linux.Log.Info(line);
    public string Platform => "linux";
    public string AppVersion => typeof(LinuxHost).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    public IReadOnlyCollection<string> PcCapabilities { get; } =
        ["device", "calls.state", "calls.control", "calls.log", "sms.read", "sms.send", "notifications.read", "notifications.act",
            "photos.read", "drop", "clipboard", "contacts.read", "contacts.write", "camera"];

    public string? ActivePhone
    {
        get => settings.ActivePhone;
        set
        {
            settings.ActivePhone = value;
            settings.Save();
        }
    }

    public void SetStatus(PhoneStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    public void OnActivity(string glyph, string text)
    {
        Activity.Insert(0, (DateTimeOffset.Now, text));
        if (Activity.Count > 20) Activity.RemoveAt(20);
        ActivityChanged?.Invoke();
    }

    // ---- Notifications ----

    /// <summary>The notification history of the phone in use; null with no phone or with the setting off.</summary>
    public NotificationHistory? History
    {
        get
        {
            if (!settings.NotificationHistory || Link?.Paired is not { } phone) return _history = null;
            var path = HistoryPath(phone.DeviceId);
            if (_history?.Path != path)
            {
                _history = new NotificationHistory(path);
                _history.Load();
            }
            return _history;
        }
    }

    static string HistoryPath(string deviceId) => Path.Combine(Paths.Data, "history", $"notifications-{deviceId}.json");

    public void OnNotification(PhoneNotification n)
    {
        Notifications[n.Key] = n;
        if (History is { } history && history.Add(n))
        {
            try { history.Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"History not saved: {e.GetType().Name}"); }
        }
        NotificationsChanged?.Invoke();
        if (settings.Notifications) Then(() => _notifier.PostedAsync(n, action => OnNotificationAction(n, action)));
    }

    void OnNotificationAction(PhoneNotification n, string action)
    {
        var chosen = DesktopNotifier.Chosen(n, action);
        if (chosen is null || chosen.Reply)
        {
            Post(() => Open?.Invoke("notifications", n.Key)); // replies are typed in the window
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await Link!.NotificationActionAsync(n.Key, chosen.Index); }
            catch (Exception e) { Log($"Notification action failed: {e.GetType().Name}"); }
        });
    }

    public void OnNotificationRemoved(string key)
    {
        if (Notifications.Remove(key)) NotificationsChanged?.Invoke();
        Then(() => _notifier.RemovedAsync(key));
    }

    public void OnPhoneRemoved()
    {
        // History goes with the phone (docs/security.md section 7).
        var keep = Link?.Phones.All().Select(p => HistoryPath(p.DeviceId)).ToHashSet() ?? [];
        var dir = Path.Combine(Paths.Data, "history");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "notifications-*.json").Where(f => !keep.Contains(f))) File.Delete(f);
        _history = null;
    }

    public void ClearHistory()
    {
        History?.Clear();
        NotificationsChanged?.Invoke();
    }

    public void OnLinkLost()
    {
        Notifications.Clear();
        NotificationsChanged?.Invoke();
        Call = null;
        CallChanged?.Invoke();
        Then(() => _notifier.CloseAsync("call"));
    }

    // ---- Calls and texts ----

    public void OnCall(PhoneCall call)
    {
        Call = call;
        CallChanged?.Invoke();
        if (call.State == CallState.Ringing && call.Id != _lastCallLogged)
        {
            _lastCallLogged = call.Id;
            OnActivity("", $"Call from {call.Name ?? call.Number ?? "an unknown number"}");
            if (call.Incoming)
                Then(() => _notifier.ShowAsync("call", "Palwyn", $"Call from {call.Title}", "Answer here; you talk on the phone.",
                    [("answer", "Answer"), ("decline", "Decline")], action => OnCallAction(call, action)));
        }
        else if (call.State != CallState.Ringing) Then(() => _notifier.CloseAsync("call"));
    }

    void OnCallAction(PhoneCall call, string action)
    {
        if (action == "default")
        {
            Post(() => Open?.Invoke("calls", null));
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await Link!.CallCommandAsync(action == "answer" ? "CALL_ANSWER" : "CALL_DECLINE", call.Id); }
            catch (Exception e) { Log($"Call {action} failed: {e.GetType().Name}"); }
        });
    }

    public void OnSms(SmsMessage m)
    {
        OnActivity("", m.Outgoing ? $"Texted {m.Name ?? m.Address}" : $"Text from {m.Name ?? m.Address}");
        SmsReceived?.Invoke(m);
        if (!m.Outgoing && settings.Notifications && ViewingThread != m.ThreadId)
            Then(() => _notifier.ShowAsync("sms:" + m.ThreadId, "Messages", m.Name ?? m.Address, m.Preview,
                [("default", "Open")], _ => Post(() => Open?.Invoke("messages", m.ThreadId))));
    }

    // ---- From the phone: clipboard and shares ----

    public void OnClipboard(string text)
    {
        if (Clipboard is not { } clipboard) return;
        _ = clipboard.ReceivedAsync(text);
        OnActivity("", "Copied from your phone");
    }

    /// <summary>
    /// Something shared to this PC from the phone ("Share > Palwyn"), a copied image or a photo taken for the PC. The
    /// user chose this PC on the phone, so files are saved without asking: photos and videos to Pictures/Palwyn, the
    /// rest to Downloads/Palwyn; a desktop notification says where.
    /// </summary>
    public void OnDrop(DropOffer offer) => _ = ReceiveAsync(offer);

    async Task ReceiveAsync(DropOffer offer)
    {
        var link = Link!;
        var phone = Status.PhoneName ?? "your phone";
        if (offer.Text is { } text)
            Then(() => _notifier.ShowAsync("drop:" + offer.Id, "Palwyn", $"From {phone}", text,
                [("copy", "Copy")], _ => Post(() => Clipboard?.ReceivedAsync(text))));
        if (offer.Files.Count == 0)
        {
            await link.DropResultAsync(offer.Id, true);
            return;
        }
        var saved = new List<string>();
        try
        {
            if (offer.Purpose == "clipboard" && offer.Files is [var image])
            {
                var temp = Path.Combine(Path.GetTempPath(), $"palwyn-clipboard-{offer.Id}-{PhonePhoto.SafeFileName(image.Name, image.Mime)}");
                await link.DownloadAsync("drop", $"{offer.Id}:{image.Index}", temp, null, CancellationToken.None);
                bool ok = Clipboard is { } clipboard && await clipboard.ReceivedImageAsync(temp);
                await link.DropResultAsync(offer.Id, ok);
                return;
            }
            var taken = new HashSet<string>();
            foreach (var f in offer.Files)
            {
                var folder = f.Mime.StartsWith("image/") || f.Mime.StartsWith("video/") ? Paths.Pictures : Paths.Downloads;
                Directory.CreateDirectory(folder);
                var path = PhonePhoto.UniquePath(folder, PhonePhoto.SafeFileName(f.Name, f.Mime), taken);
                await link.DownloadAsync("drop", $"{offer.Id}:{f.Index}", path, null, CancellationToken.None);
                saved.Add(path);
            }
            await link.DropResultAsync(offer.Id, true);
            var where = Path.GetDirectoryName(saved[^1])!;
            Then(() => _notifier.ShowAsync("drop:" + offer.Id, "Palwyn",
                saved.Count == 1 ? $"Received {Path.GetFileName(saved[0])}" : $"Received {saved.Count} files",
                $"From {phone}, in {where}", [("default", "Open folder")], _ => Paths.Open(where)));
            OnActivity("", saved.Count == 1 ? $"Received {Path.GetFileName(saved[0])}" : $"Received {saved.Count} files");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log($"Receiving from the phone failed after {saved.Count} of {offer.Files.Count}: {e.GetType().Name}: {e.Message}");
            try { await link.DropResultAsync(offer.Id, false); } catch (Exception) { }
            Then(() => _notifier.ShowAsync("drop:" + offer.Id, "Palwyn", "Not everything arrived",
                $"{saved.Count} of {offer.Files.Count} files from {phone} were saved. Share them again.", [], null));
        }
    }

    // Not built on Linux yet (later phases); the phone doesn't send these without the capability.
    public void OnScreenState(string state) { }
    public void OnEmergencyHint(AdbDevice device) { }
    public void OnRemoteInput(string type, JsonObject payload) { }
    public Task<string?> OnRemoteActAsync(string type, JsonObject payload) => Task.FromResult<string?>("NOT_CAPABLE");
}

/// <summary>No USB cable link on Linux yet (Phase 8).</summary>
public sealed class NoUsb : IUsbLink
{
    public event Action<bool>? Changed { add { } remove { } }
    public event Action? DevicesChanged { add { } remove { } }
    public IReadOnlyList<AdbDevice> Devices => [];
    public bool Forwarded => false;
    public int PhonePort { get; set; }
    public void Start() { }
    public void Check() { }
    public void Dispose() { }
}
