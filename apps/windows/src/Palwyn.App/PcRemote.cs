using System.Diagnostics;
using System.Text.Json.Nodes;
using Windows.Media.Control;

namespace Palwyn.App;

/// <summary>
/// The phone as a remote for this PC: mouse, keyboard and presenter keys, media and volume, locking, and the commands
/// set up in Settings. The phone learns what's allowed from PC_REMOTE, but every message is checked against the
/// settings again here, so a phone can never do more than the user allowed on this PC.
/// </summary>
static class PcRemote
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly Dictionary<string, string> AppNames = [];
    static GlobalSystemMediaTransportControlsSessionManager? _media;
    static Timer? _poll;
    static string? _sent; // the last PC_REMOTE sent on this connection
    static bool _controlling, _refusedLogged;

    static readonly Dictionary<string, (ushort Vk, bool Extended)> Keys = new()
    {
        ["enter"] = (0x0D, false), ["backspace"] = (0x08, false), ["delete"] = (0x2E, true), ["tab"] = (0x09, false),
        ["escape"] = (0x1B, false), ["left"] = (0x25, true), ["right"] = (0x27, true), ["up"] = (0x26, true),
        ["down"] = (0x28, true), ["home"] = (0x24, true), ["end"] = (0x23, true), ["pageUp"] = (0x21, true),
        ["pageDown"] = (0x22, true), ["f5"] = (0x74, false),
    };

    /// <summary>Call when the connection or a remote setting changes (UI thread).</summary>
    public static void Update()
    {
        var link = App.Current.Link;
        if (!link.IsConnected || !link.Capabilities.Contains("remote"))
        {
            _poll?.Dispose();
            (_poll, _sent, _controlling, _refusedLogged) = (null, null, false, false);
            return;
        }
        // ponytail: what's playing and the volume are polled every 2 s; use the media and endpoint-volume events if lag matters.
        _poll ??= new Timer(_ => _ = PushAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _ = PushAsync();
    }

    /// <summary>Sends PC_REMOTE when it differs from the last one sent.</summary>
    static async Task PushAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var state = await StateAsync();
            var json = state.ToJsonString();
            if (json == _sent) return;
            await App.Current.Link.SendPcRemoteAsync(state);
            _sent = json;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { } // disconnected
        finally
        {
            Gate.Release();
        }
    }

    static async Task<JsonObject> StateAsync()
    {
        var p = new JsonObject
        {
            ["input"] = AppSettings.RemoteInput,
            ["media"] = AppSettings.RemoteMedia,
            ["commands"] = new JsonArray(AppSettings.RemoteCommands.Take(50)
                .Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = Cut(c.Name, 64) }).ToArray()),
        };
        if (!AppSettings.RemoteMedia) return p; // what's playing is shared only while the phone may control it
        if (PcVolume.Get() is { } volume)
        {
            p["volume"] = volume.Level;
            p["muted"] = volume.Muted;
        }
        try
        {
            if ((await Media()).GetCurrentSession() is { } session)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                p["app"] = Cut(AppName(session.SourceAppUserModelId), 128);
                if (!string.IsNullOrEmpty(props.Title)) p["title"] = Cut(props.Title, 500);
                if (!string.IsNullOrEmpty(props.Artist)) p["artist"] = Cut(props.Artist, 500);
                p["playing"] = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
        }
        catch (Exception) { } // a player closing mid-query: send what we have
        return p;
    }

    /// <summary>REMOTE_POINTER, _BUTTON, _SCROLL, _TEXT and _KEY, on the link's thread. No reply: dropped when not allowed.</summary>
    public static void Input(string type, JsonObject p)
    {
        if (!AppSettings.RemoteInput)
        {
            if (!_refusedLogged) Log.Info("Refused mouse and keyboard from the phone: turned off in Settings");
            _refusedLogged = true;
            return;
        }
        if (!_controlling) Log.Info("The phone is controlling the mouse and keyboard"); // once per connection, never what's typed
        _controlling = true;
        switch (type)
        {
            case "REMOTE_POINTER":
                Win32.Mouse(Win32.MOUSEEVENTF_MOVE, p["dx"]!.GetValue<int>(), p["dy"]!.GetValue<int>());
                break;
            case "REMOTE_SCROLL":
                Win32.Mouse(Win32.MOUSEEVENTF_WHEEL, data: p["dy"]!.GetValue<int>());
                break;
            case "REMOTE_BUTTON":
                var (down, up) = p["button"]!.GetValue<string>() switch
                {
                    "right" => (Win32.MOUSEEVENTF_RIGHTDOWN, Win32.MOUSEEVENTF_RIGHTUP),
                    "middle" => (Win32.MOUSEEVENTF_MIDDLEDOWN, Win32.MOUSEEVENTF_MIDDLEUP),
                    _ => (Win32.MOUSEEVENTF_LEFTDOWN, Win32.MOUSEEVENTF_LEFTUP),
                };
                var action = p["action"]!.GetValue<string>();
                if (action != "up") Win32.Mouse(down);
                if (action != "down") Win32.Mouse(up);
                break;
            case "REMOTE_TEXT":
                Win32.Type(p["text"]!.GetValue<string>());
                break;
            case "REMOTE_KEY":
                var (vk, extended) = Keys[p["key"]!.GetValue<string>()];
                Win32.Key(vk, extended);
                break;
        }
    }

    /// <summary>PC_MEDIA, PC_VOLUME, PC_COMMAND and PC_LOCK. Returns an error code for the phone, or null when done.</summary>
    public static async Task<string?> ActAsync(string type, JsonObject p)
    {
        try
        {
            if (type == "PC_COMMAND") return Run(p["id"]!.GetValue<string>());
            if (!AppSettings.RemoteMedia) return "REMOTE_OFF";
            switch (type)
            {
                case "PC_LOCK":
                    if (!Win32.LockWorkStation()) return "FAILED";
                    Log.Info("Locked for the phone");
                    return null;
                case "PC_VOLUME":
                    PcVolume.Set(p["level"]?.GetValue<int>(), p["muted"]?.GetValue<bool>());
                    break;
                case "PC_MEDIA":
                    if ((await Media()).GetCurrentSession() is not { } session) return "NOTHING_PLAYING";
                    bool ok = p["action"]!.GetValue<string>() switch
                    {
                        "next" => await session.TrySkipNextAsync(),
                        "previous" => await session.TrySkipPreviousAsync(),
                        _ => await session.TryTogglePlayPauseAsync(),
                    };
                    if (!ok) return "FAILED";
                    break;
            }
        }
        catch (Exception e) // a player or audio device going away mid-request
        {
            Log.Info($"{type} failed: {e.GetType().Name}");
            return "FAILED";
        }
        _ = Task.Delay(300).ContinueWith(_ => PushAsync()); // let the phone see the result
        return null;
    }

    static string? Run(string id)
    {
        if (AppSettings.RemoteCommands.FirstOrDefault(c => c.Id == id) is not { } command) return "NOT_FOUND";
        // /s /c "..." keeps the command's own quotes intact.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command.Command}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Log.Info($"Ran command \"{command.Name}\" for the phone"); // the name the user gave it, never the command line
        return null;
    }

    static async Task<GlobalSystemMediaTransportControlsSessionManager> Media() =>
        _media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    /// <summary>"Spotify" rather than "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" or "chrome.exe".</summary>
    static string AppName(string aumid)
    {
        lock (AppNames)
            if (AppNames.TryGetValue(aumid, out var known)) return known;
        string name;
        try { name = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid).DisplayInfo.DisplayName; }
        catch (Exception) // not a packaged app
        {
            name = aumid.Split('!')[^1];
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        }
        lock (AppNames) AppNames[aumid] = name;
        return name;
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
