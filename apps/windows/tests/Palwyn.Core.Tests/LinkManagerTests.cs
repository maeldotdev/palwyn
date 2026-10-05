using System.Text.Json.Nodes;
using Palwyn.Core;
using Palwyn.Core.Link;
using static EngineTests;

/// <summary>The shared link hub against a fake phone and a fake PC app (what Windows and Linux each provide).</summary>
public class LinkManagerTests
{
    sealed class FakeHost : IPcHost
    {
        public readonly List<PhoneStatus> Statuses = [];
        public readonly List<PhoneNotification> Notifications = [];
        public int LinkLost;
        public void Post(Action action) { lock (this) action(); }
        public void Log(string line) { }
        public string Platform => "linux";
        public string AppVersion => "0.15.0";
        public IReadOnlyCollection<string> PcCapabilities => ["notifications.read"];
        public string? ActivePhone { get; set; }
        public void SetStatus(PhoneStatus status) => Statuses.Add(status);
        public void OnLinkLost() => LinkLost++;
        public void OnPhoneRemoved() { }
        public void OnActivity(string glyph, string text) { }
        public void OnSms(SmsMessage sms) { }
        public void OnNotification(PhoneNotification notification) => Notifications.Add(notification);
        public void OnNotificationRemoved(string key) { }
        public void OnCall(PhoneCall call) { }
        public void OnClipboard(string text) { }
        public void OnDrop(DropOffer offer) { }
        public void OnScreenState(string state) { }
        public void OnEmergencyHint(AdbDevice device) { }
        public void OnRemoteInput(string type, JsonObject payload) { }
        public Task<string?> OnRemoteActAsync(string type, JsonObject payload) => Task.FromResult<string?>(null);
    }

    sealed class NoDiscovery : IPhoneDiscovery
    {
        public event Action<DiscoveredPhone>? Found { add { } remove { } }
        public event Action<string>? Lost { add { } remove { } }
        public IReadOnlyList<DiscoveredPhone> Current => [];
        public void Start() { }
        public void Dispose() { }
    }

    sealed class NoUsb : IUsbLink
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

    /// <summary>A phone that says HELLO (with notifications), runs <paramref name="then"/>, then answers PINGs.</summary>
    static FakePhone Phone(Func<Stream, CancellationToken, Task> then, List<string> pcPlatforms) => new(async (_, s, _, ct) =>
    {
        var pcHello = await FakePhone.Read(s, ct);
        lock (pcPlatforms) pcPlatforms.Add(pcHello!["payload"]!["platform"]!.GetValue<string>());
        var hello = new Hello(Fingerprint.DeviceId(Fingerprint.Of(PhoneId)), "narzo 50", "android", "0.15.0",
            ["device", "notifications.read"], 3).ToPayload();
        await FakePhone.Send(s, "HELLO", hello, ct);
        await then(s, ct);
        while (await FakePhone.Read(s, ct) is { } m)
            if (m["type"]!.GetValue<string>() == "PING") await FakePhone.Send(s, "PONG", null, ct, m["id"]!.GetValue<string>());
    });

    static (LinkManager, FakeHost, string Dir) Hub(FakePhone phone)
    {
        var dir = Path.Combine(Path.GetTempPath(), "palwyn-hub-" + Guid.NewGuid().ToString("N"));
        var fp = Fingerprint.Of(PhoneId);
        new PairedPhones(Path.Combine(dir, "paired-phones.json")).Save(new PairedPhone(
            Fingerprint.DeviceId(fp), "narzo 50", Hex.Of(fp), "127.0.0.1", phone.Port, DateTimeOffset.UtcNow));
        var host = new FakeHost();
        return (new LinkManager(host, PcId, dir, new NoDiscovery(), new NoUsb()), host, dir);
    }

    [Fact]
    public async Task Connects_with_the_hosts_platform_and_hands_it_phone_notifications()
    {
        var platforms = new List<string>();
        await using var phone = Phone((s, ct) => FakePhone.Send(s, "NOTIFICATION_POSTED", new JsonObject
        {
            ["key"] = "0|com.whatsapp|1|null|10123", ["package"] = "com.whatsapp", ["appName"] = "WhatsApp",
            ["title"] = "Mika", ["text"] = "See you at 7", ["postedAt"] = 1790000000000, ["clearable"] = true,
            ["actions"] = new JsonArray(),
        }, ct), platforms);
        var (hub, host, dir) = Hub(phone);
        try
        {
            hub.Start();
            await Until(() => { lock (host) return host.Notifications.Count == 1; });
            lock (host)
            {
                Assert.Equal("Mika", host.Notifications[0].Title);
                Assert.Contains(host.Statuses, s => s.State == ConnectionState.Connected);
            }
            Assert.True(hub.IsConnected);
            lock (platforms) Assert.Equal("linux", platforms[0]);
        }
        finally
        {
            hub.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_phone_that_unpairs_is_forgotten()
    {
        await using var phone = Phone((s, ct) => FakePhone.Send(s, "UNPAIR", null, ct), []);
        var (hub, host, dir) = Hub(phone);
        try
        {
            hub.Start();
            await Until(() => { lock (host) return host.Statuses.LastOrDefault()?.State == ConnectionState.NotPaired && host.Statuses.Count > 1; });
            Assert.Empty(hub.Phones.All());
            Assert.Null(hub.Paired);
        }
        finally
        {
            hub.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
