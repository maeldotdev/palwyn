using System.Text.Json.Nodes;

namespace Palwyn.Core.Link;

/// <summary>
/// What <see cref="LinkManager"/> needs from the PC app it runs in (WinUI on Windows, the Linux app): its UI thread,
/// its log and settings, and where phone events go. Callbacks named On… run on the UI thread (the manager posts
/// them), except <see cref="OnRemoteInput"/> and <see cref="OnRemoteActAsync"/>, which run on the link's thread.
/// </summary>
public interface IPcHost
{
    /// <summary>Runs <paramref name="action"/> on the UI thread, later.</summary>
    void Post(Action action);
    void Log(string line);
    /// <summary>"windows" or "linux" (HELLO).</summary>
    string Platform { get; }
    string AppVersion { get; }
    /// <summary>The capabilities this PC app implements (HELLO).</summary>
    IReadOnlyCollection<string> PcCapabilities { get; }
    /// <summary>The paired phone the user picked; null = the first paired.</summary>
    string? ActivePhone { get; set; }

    void SetStatus(PhoneStatus status);
    /// <summary>The link dropped: live state from the phone is unknown until it reconnects.</summary>
    void OnLinkLost();
    /// <summary>A phone was removed: data kept for it (notification history) goes.</summary>
    void OnPhoneRemoved();
    /// <summary>Something for the recent-activity list ("Phone connected").</summary>
    void OnActivity(string glyph, string text);
    void OnSms(SmsMessage sms);
    void OnNotification(PhoneNotification notification);
    void OnNotificationRemoved(string key);
    void OnCall(PhoneCall call);
    void OnClipboard(string text);
    void OnDrop(DropOffer offer);
    void OnScreenState(string state);
    /// <summary>The paired phone hasn't answered for a while, but an allowed phone is on the USB cable.</summary>
    void OnEmergencyHint(AdbDevice device);
    /// <summary>REMOTE_POINTER, REMOTE_BUTTON, REMOTE_SCROLL, REMOTE_TEXT, REMOTE_KEY. No reply.</summary>
    void OnRemoteInput(string type, JsonObject payload);
    /// <summary>PC_MEDIA, PC_VOLUME, PC_COMMAND, PC_LOCK: null when done, else the ERROR code to answer.</summary>
    Task<string?> OnRemoteActAsync(string type, JsonObject payload);
}

/// <param name="PairMode">"code", "qr:&lt;first 8 hex of the PC fingerprint&gt;", or null when not pairing.</param>
public sealed record DiscoveredPhone(string Key, string DeviceId, string? PairMode, string? Name, string Host, int Port);

/// <summary>Finds phones advertising <c>_palwyn._tcp</c> on the network. Events may come on any thread.</summary>
public interface IPhoneDiscovery : IDisposable
{
    event Action<DiscoveredPhone>? Found;
    event Action<string>? Lost;
    IReadOnlyList<DiscoveredPhone> Current { get; }
    void Start();
}

/// <summary>The phone over a USB cable: adb forwards <see cref="CableLink.LocalPort"/> to the phone's link port.</summary>
public interface IUsbLink : IDisposable
{
    /// <summary>True once the phone's port is forwarded over USB, false when the cable is gone. Not on the UI thread.</summary>
    event Action<bool>? Changed;
    /// <summary>Phones on a cable at the last check, allowed or not.</summary>
    IReadOnlyList<AdbDevice> Devices { get; }
    event Action? DevicesChanged;
    bool Forwarded { get; }
    /// <summary>The phone's link port to forward to (the phone falls back to another if 47800 is taken).</summary>
    int PhonePort { get; set; }
    void Start();
    /// <summary>Looks again now, e.g. when the link over the cable failed.</summary>
    void Check();
}

public static class CableLink
{
    public const string Host = "127.0.0.1";
    public const int LocalPort = 47801;
    /// <summary>The cable's address; another 127.0.0.1 port is something else (an emulator, the user's own tunnel).</summary>
    public static bool Is(string host, int port) => host == Host && port == LocalPort;
}
