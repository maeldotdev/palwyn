using System.Text.Json;

namespace Palwyn.Core.Link;

/// <param name="Address">Set by the user ("100.64.1.2", "phone.tailnet:47800") for a VPN or a network without
/// discovery: the PC then connects only there. Null = found automatically.</param>
public sealed record PairedPhone(string DeviceId, string Name, string Fingerprint, string? Host, int Port, DateTimeOffset PairedAt,
    string? Address = null)
{
    public byte[] FingerprintBytes => Hex.Parse(Fingerprint);

    /// <summary>Where to connect: the user's address if set, else the last discovered one.</summary>
    public (string Host, int Port) Endpoint =>
        Address is not null && ParseAddress(Address, Port) is { } a ? a : (Host ?? "0.0.0.0", Port);

    /// <summary>"host", "host:port" or an IPv6 address ("[::1]:47800" for one with a port). Null if it isn't one.</summary>
    public static (string Host, int Port)? ParseAddress(string text, int defaultPort)
    {
        text = text.Trim();
        string host = text;
        int port = defaultPort;
        if (text.StartsWith('[') && text.IndexOf(']') is var end and > 0)
        {
            host = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.Length > 0 && !(rest.StartsWith(':') && int.TryParse(rest[1..], out port))) return null;
        }
        else if (text.Count(c => c == ':') == 1)
        {
            host = text[..text.IndexOf(':')];
            if (!int.TryParse(text[(text.IndexOf(':') + 1)..], out port)) return null;
        }
        if (port is < 1 or > 65535 || host.Length == 0 || host.Any(char.IsWhiteSpace)
            || Uri.CheckHostName(host) == UriHostNameType.Unknown) return null;
        return (host, port);
    }
}

/// <summary>
/// Pinned phones, as a small JSON file. Fingerprints are public values; what matters is that only this user's
/// profile can change the file.
/// </summary>
public sealed class PairedPhones(string path)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    readonly Lock _gate = new();

    public IReadOnlyList<PairedPhone> All()
    {
        lock (_gate) return Read();
    }

    List<PairedPhone> Read() =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<PairedPhone>>(File.ReadAllText(path)) ?? [] : [];

    public PairedPhone? Find(byte[] fingerprint) => All().FirstOrDefault(p => p.Fingerprint == Hex.Of(fingerprint));

    public void Save(PairedPhone phone) => Update(list =>
    {
        list.RemoveAll(p => p.DeviceId == phone.DeviceId);
        list.Add(phone);
    });

    public void Remove(string deviceId) => Update(list => list.RemoveAll(p => p.DeviceId == deviceId));

    void Update(Action<List<PairedPhone>> change)
    {
        lock (_gate)
        {
            var list = Read();
            change(list);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
