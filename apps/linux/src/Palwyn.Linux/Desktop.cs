using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Palwyn.Linux;

/// <summary>Runs a desktop tool and returns its exit code and output. No shell: arguments go as they are.</summary>
static class Tool
{
    /// <returns>Exit code (-1 when the tool isn't installed) and standard output.</returns>
    public static async Task<(int Code, string Output)> RunAsync(string file, params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                p.Kill();
                return (-1, "");
            }
            return (p.ExitCode, await output);
        }
        catch (Win32Exception)
        {
            return (-1, "");
        }
    }
}

/// <summary>
/// Media players through MPRIS (org.mpris.MediaPlayer2), which Spotify, Firefox, Chrome, VLC, mpv (with its plugin)
/// and most Linux players publish on the session bus. Called through gdbus, so nothing extra to install.
/// </summary>
public static partial class Mpris
{
    public sealed record Player(string BusName, string App, string? Title, string? Artist, bool Playing);

    static Task<(int Code, string Output)> Call(string dest, string path, string method, params string[] args) =>
        Tool.RunAsync("gdbus", ["call", "--session", "--dest", dest, "--object-path", path, "--method", method, .. args]);

    static Task<(int, string)> Get(string dest, string iface, string property) =>
        Call(dest, "/org/mpris/MediaPlayer2", "org.freedesktop.DBus.Properties.Get", iface, property);

    /// <summary>Every player on the session bus, the playing ones first.</summary>
    public static async Task<List<Player>> PlayersAsync()
    {
        var (code, names) = await Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus.ListNames");
        if (code != 0) return [];
        var players = new List<Player>();
        foreach (var name in BusNames(names))
        {
            var (_, status) = await Get(name, "org.mpris.MediaPlayer2.Player", "PlaybackStatus");
            var (_, identity) = await Get(name, "org.mpris.MediaPlayer2", "Identity");
            var (_, metadata) = await Get(name, "org.mpris.MediaPlayer2.Player", "Metadata");
            var (title, artist) = TitleAndArtist(metadata);
            players.Add(new Player(name, FirstString(identity) ?? name["org.mpris.MediaPlayer2.".Length..].Split('.')[0],
                title, artist, FirstString(status) == "Playing"));
        }
        return [.. players.OrderByDescending(p => p.Playing)];
    }

    /// <summary>PlayPause, Next, Previous, Pause or Play on one player.</summary>
    public static async Task<bool> SendAsync(string busName, string method) =>
        (await Call(busName, "/org/mpris/MediaPlayer2", $"org.mpris.MediaPlayer2.Player.{method}")).Code == 0;

    // ---- gdbus prints GVariant text; these read the few shapes MPRIS uses ----

    /// <summary>The MPRIS players in ListNames' answer: <c>(['org.freedesktop.DBus', 'org.mpris.MediaPlayer2.spotify'],)</c>.</summary>
    public static IEnumerable<string> BusNames(string listNames) =>
        BusName().Matches(listNames).Select(m => m.Groups[1].Value).Distinct();

    /// <summary>The first string in the answer: <c>(&lt;'Playing'&gt;,)</c> gives Playing.</summary>
    public static string? FirstString(string variant) => StringLiteral().Match(variant) is { Success: true } m ? Unescape(m) : null;

    /// <summary>xesam:title and the first xesam:artist from the Metadata answer.</summary>
    public static (string? Title, string? Artist) TitleAndArtist(string metadata)
    {
        var title = Regex.Match(metadata, @"'xesam:title': <" + Literal);
        var artist = Regex.Match(metadata, @"'xesam:artist': <\[" + Literal);
        return (title.Success ? Unescape(title) : null, artist.Success ? Unescape(artist) : null);
    }

    const string Literal = """(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")""";

    static string Unescape(Match m)
    {
        var s = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length)
            {
                sb.Append(s[i]);
                continue;
            }
            char c = s[++i];
            if (c == 'u' && i + 4 < s.Length && int.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var u))
            {
                sb.Append((char)u);
                i += 4;
            }
            else sb.Append(c switch { 'n' => '\n', 't' => '\t', _ => c });
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"'(org\.mpris\.MediaPlayer2\.[^']+)'")]
    private static partial Regex BusName();

    [GeneratedRegex(Literal)]
    private static partial Regex StringLiteral();
}

/// <summary>The default output's volume through pactl, which PulseAudio and PipeWire (pipewire-pulse) both answer.</summary>
public static partial class Volume
{
    public static async Task<(int Level, bool Muted)?> GetAsync()
    {
        var (code, volume) = await Tool.RunAsync("pactl", "get-sink-volume", "@DEFAULT_SINK@");
        var (_, mute) = await Tool.RunAsync("pactl", "get-sink-mute", "@DEFAULT_SINK@");
        return code == 0 && Parse(volume, mute) is { } v ? v : null;
    }

    /// <summary><c>Volume: front-left: 26214 /  40% / -23.87 dB, ...</c> and <c>Mute: no</c>. Over 100% shows as 100.</summary>
    public static (int Level, bool Muted)? Parse(string volume, string mute) =>
        Percent().Match(volume) is { Success: true } m ? (Math.Min(100, int.Parse(m.Groups[1].Value)), mute.Contains("yes")) : null;

    public static async Task SetAsync(int? level, bool? muted)
    {
        if (level is int l) await Tool.RunAsync("pactl", "set-sink-volume", "@DEFAULT_SINK@", $"{Math.Clamp(l, 0, 100)}%");
        if (muted is bool m) await Tool.RunAsync("pactl", "set-sink-mute", "@DEFAULT_SINK@", m ? "1" : "0");
    }

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex Percent();
}

/// <summary>Locking and staying awake, through systemd-logind (every systemd desktop).</summary>
public static class Session
{
    static Process? _inhibit;

    public static async Task<bool> LockAsync() => (await Tool.RunAsync("loginctl", "lock-session")).Code == 0;

    /// <summary>
    /// Holds a sleep inhibitor while <paramref name="on"/> (the phone connected and the setting on). The holder
    /// watches Palwyn's own process, so the inhibitor ends with Palwyn even if it crashes.
    /// </summary>
    public static void KeepAwake(bool on)
    {
        if (on == (_inhibit is { HasExited: false })) return;
        if (!on)
        {
            try { _inhibit?.Kill(); } catch (InvalidOperationException) { }
            _inhibit?.Dispose();
            _inhibit = null;
            return;
        }
        try
        {
            _inhibit = Process.Start(new ProcessStartInfo("systemd-inhibit", ["--what=sleep", "--who=Palwyn",
                "--why=Your phone is connected", "--mode=block", "tail", $"--pid={Environment.ProcessId}", "-f", "/dev/null"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            Log.Info("Keeping this PC awake while the phone is connected");
        }
        catch (Win32Exception)
        {
            Log.Info("Can't keep the PC awake: systemd-inhibit isn't available");
        }
    }
}
