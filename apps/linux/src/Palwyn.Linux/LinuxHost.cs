using System.Text.Json.Nodes;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.Linux;

/// <summary>
/// The link hub's view of the Linux app: its UI thread (or, headless, a serial work queue), log, settings and
/// desktop notifications. Everything the Linux app doesn't do yet is left out of <see cref="PcCapabilities"/>, so
/// the phone never sends it.
/// </summary>
/// <param name="post">Runs work on the UI thread; null for the headless app.</param>
public sealed class LinuxHost(Settings settings, Action<Action>? post = null) : IPcHost
{
    readonly DesktopNotifier _notifier = new();
    readonly Lock _gate = new();
    Task _work = Task.CompletedTask;

    public PhoneStatus Status { get; private set; } = PhoneStatus.NotPaired;
    /// <summary>On the UI thread.</summary>
    public event Action? StatusChanged;
    /// <summary>Newest first, for Home.</summary>
    public List<(DateTimeOffset At, string Text)> Activity { get; } = [];
    public event Action? ActivityChanged;

    /// <summary>Headless: runs work one item at a time, in order, like a UI thread would.</summary>
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
    public IReadOnlyCollection<string> PcCapabilities { get; } = ["device", "notifications.read"];

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

    // Notification popups run as processes: off the UI thread, in order.
    public void OnNotification(PhoneNotification notification)
    {
        if (settings.Notifications) Then(() => _notifier.PostedAsync(notification));
    }

    public void OnNotificationRemoved(string key) => Then(() => _notifier.RemovedAsync(key));

    // Not built on Linux yet (later phases); the phone doesn't send these without the capability.
    public void OnLinkLost() { }
    public void OnPhoneRemoved() { }
    public void OnSms(SmsMessage sms) { }
    public void OnCall(PhoneCall call) { }
    public void OnClipboard(string text) { }
    public void OnDrop(DropOffer offer) { }
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
