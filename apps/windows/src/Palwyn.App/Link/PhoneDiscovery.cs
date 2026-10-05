using Palwyn.Core.Link;
using Windows.Devices.Enumeration;

namespace Palwyn.App.Link;

/// <summary>Finds phones advertising <c>_palwyn._tcp</c> using Windows' built-in DNS-SD (no extra dependency).</summary>
public sealed class PhoneDiscovery : IPhoneDiscovery
{
    const string Query =
        "System.Devices.AepService.ProtocolId:=\"{4526e8c1-8aac-4153-9b16-55e86ada0e54}\" AND " +
        "System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"_palwyn._tcp\"";

    static readonly string[] Properties =
    [
        "System.Devices.Dnssd.HostName", "System.Devices.Dnssd.PortNumber",
        "System.Devices.Dnssd.TextAttributes", "System.Devices.IpAddress",
    ];

    readonly Dictionary<string, DeviceInformation> _seen = [];
    readonly Lock _gate = new();
    DeviceWatcher? _watcher;

    public event Action<DiscoveredPhone>? Found;
    public event Action<string>? Lost;

    public IReadOnlyList<DiscoveredPhone> Current
    {
        get { lock (_gate) return _seen.Values.Select(Parse).OfType<DiscoveredPhone>().ToList(); }
    }

    public void Start()
    {
        _watcher = DeviceInformation.CreateWatcher(Query, Properties, DeviceInformationKind.AssociationEndpointService);
        _watcher.Added += (_, d) => Upsert(d);
        _watcher.Updated += (_, u) =>
        {
            DeviceInformation? d;
            lock (_gate) if (_seen.TryGetValue(u.Id, out d)) d.Update(u);
            if (d is not null) Upsert(d);
        };
        _watcher.Removed += (_, u) =>
        {
            lock (_gate) _seen.Remove(u.Id);
            Lost?.Invoke(u.Id);
        };
        // The watcher stops after errors or network changes; keep it running.
        _watcher.Stopped += (_, _) => { if (_watcher is not null) Restart(); };
        _watcher.Start();
        Log.Info("Phone discovery started");
    }

    void Restart()
    {
        Log.Info("Phone discovery restarting");
        lock (_gate) _seen.Clear();
        try { _watcher!.Start(); } catch (InvalidOperationException) { }
    }

    void Upsert(DeviceInformation d)
    {
        if (Parse(d) is not { } phone) return;
        bool fresh;
        lock (_gate)
        {
            _seen[d.Id] = d;
            fresh = _logged.Add(phone with { Key = "" });
        }
        if (fresh) Log.Info($"Discovered phone {Core.Fingerprint.Display(phone.DeviceId)} (pairing: {phone.PairMode ?? "no"})");
        Found?.Invoke(phone);
    }

    readonly HashSet<DiscoveredPhone> _logged = [];

    static DiscoveredPhone? Parse(DeviceInformation d)
    {
        var txt = (d.Properties["System.Devices.Dnssd.TextAttributes"] as string[] ?? [])
            .Select(a => a.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
        var ips = d.Properties["System.Devices.IpAddress"] as string[] ?? [];
        var host = ips.FirstOrDefault(ip => ip.Contains('.')) ?? d.Properties["System.Devices.Dnssd.HostName"] as string;
        var port = d.Properties["System.Devices.Dnssd.PortNumber"] is { } p ? Convert.ToInt32(p) : 0;
        if (host is null || port == 0 || !txt.TryGetValue("id", out var id)) return null;
        return new DiscoveredPhone(d.Id, id, txt.GetValueOrDefault("pair"), txt.GetValueOrDefault("n"), host, port);
    }

    public void Dispose()
    {
        var w = _watcher;
        _watcher = null;
        if (w?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
    }
}
