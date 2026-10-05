using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Palwyn.Linux;

/// <param name="PairMode">"code", "qr:&lt;first 8 hex of the PC fingerprint&gt;", or null when not pairing.</param>
public sealed record DiscoveredPhone(string Key, string DeviceId, string? PairMode, string? Name, string Host, int Port);

/// <summary>
/// Finds phones advertising <c>_palwyn._tcp</c> through <c>avahi-browse</c> (package avahi-utils), which talks to the
/// avahi-daemon that most desktops already run.
/// </summary>
// ponytail: parses avahi-browse's output; switch to Avahi's D-Bus API once the app has D-Bus bindings (Phase 4).
public sealed partial class AvahiDiscovery : IDisposable
{
    Process? _browse;

    public event Action<DiscoveredPhone>? Found;

    /// <summary>Starts browsing. Returns why it can't, or null.</summary>
    public string? Start()
    {
        try
        {
            _browse = Process.Start(new ProcessStartInfo("avahi-browse", ["--resolve", "--parsable", "--no-db-lookup", "_palwyn._tcp"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
        }
        catch (Win32Exception)
        {
            return "avahi-browse isn't installed (package avahi-utils).";
        }
        _browse.OutputDataReceived += (_, e) => { if (e.Data is { } line && Parse(line) is { } phone) Found?.Invoke(phone); };
        _browse.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Console.Error.WriteLine($"Discovery: {line}"); };
        _browse.BeginOutputReadLine();
        _browse.BeginErrorReadLine();
        return null;
    }

    /// <summary>
    /// One resolved service from <c>avahi-browse --parsable</c>:
    /// <c>=;wlan0;IPv4;Pixel\0327;_palwyn._tcp;local;pixel.local;192.168.1.23;47800;"id=…" "pair=qr:…" "n=Pixel 7"</c>.
    /// Null for anything else, and for IPv6 addresses (a link-local <c>fe80::</c> one needs an interface to work).
    /// </summary>
    public static DiscoveredPhone? Parse(string line)
    {
        var f = line.Split(';', 10);
        if (f.Length < 10 || f[0] != "=") return null;
        var host = Unescape(f[7]);
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        if (!int.TryParse(f[8], out var port) || port is < 1 or > 65535) return null;
        var txt = new Dictionary<string, string>();
        foreach (Match m in TxtItem().Matches(f[9]))
            if (Unescape(m.Groups[1].Value).Split('=', 2) is [var k, var v]) txt.TryAdd(k, v);
        if (!txt.TryGetValue("id", out var id)) return null;
        return new DiscoveredPhone($"{f[1]};{f[2]};{f[3]}", id, txt.GetValueOrDefault("pair"), txt.GetValueOrDefault("n"), host, port);
    }

    /// <summary>Avahi writes a byte it won't print as <c>\DDD</c> (decimal) and a quote or backslash as <c>\"</c>, <c>\\</c>.</summary>
    static string Unescape(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length
                && char.IsAsciiDigit(s[i + 1]) && char.IsAsciiDigit(s[i + 2]) && char.IsAsciiDigit(s[i + 3]))
            {
                bytes.Add((byte)int.Parse(s.AsSpan(i + 1, 3)));
                i += 3;
            }
            else
            {
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex TxtItem();

    public void Dispose()
    {
        try { _browse?.Kill(); } catch (InvalidOperationException) { }
        _browse?.Dispose();
    }
}
