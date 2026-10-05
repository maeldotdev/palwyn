using System.Text.Json.Nodes;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.Linux;

/// <summary>
/// The link hub's view of the headless Linux app: a serial work queue instead of a UI thread, the console as its log,
/// and desktop notifications. Everything the Linux app doesn't do yet is left out of <see cref="PcCapabilities"/>,
/// so the phone never sends it.
/// </summary>
public sealed class LinuxHost(DesktopNotifier notifier) : IPcHost
{
    readonly Lock _gate = new();
    Task _work = Task.CompletedTask;

    /// <summary>Raised when the phone can't be used without the user (it no longer trusts this PC, or versions differ).</summary>
    public event Action<string>? Blocked;

    /// <summary>Runs work one item at a time, in order, like a UI thread would.</summary>
    void Then(Func<Task> work)
    {
        lock (_gate)
            _work = _work.ContinueWith(async _ =>
            {
                try { await work(); }
                catch (Exception e) { Log($"Failed: {e.GetType().Name}: {e.Message}"); }
            }, TaskScheduler.Default).Unwrap();
    }

    public void Post(Action action) => Then(() => { action(); return Task.CompletedTask; });
    public void Log(string line) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
    public string Platform => "linux";
    public string AppVersion => typeof(LinuxHost).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    public IReadOnlyCollection<string> PcCapabilities { get; } = ["notifications.read"];
    // ponytail: one phone at a time and no settings file yet, so the first paired phone is always the one in use.
    public string? ActivePhone { get => null; set { } }

    public void SetStatus(PhoneStatus status)
    {
        if (status is { State: ConnectionState.Blocked, Detail: { } why }) Blocked?.Invoke(why);
    }

    public void OnNotification(PhoneNotification notification) => Then(() => notifier.PostedAsync(notification));
    public void OnNotificationRemoved(string key) => Then(() => notifier.RemovedAsync(key));

    // Not built on Linux yet (later phases); the phone doesn't send these without the capability.
    public void OnLinkLost() { }
    public void OnPhoneRemoved() { }
    public void OnActivity(string glyph, string text) { }
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
