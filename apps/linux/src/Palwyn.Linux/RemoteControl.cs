using System.Diagnostics;
using System.Text.Json.Nodes;
using Palwyn.Core.Link;

namespace Palwyn.Linux;

/// <summary>
/// The phone as a remote for this PC, as on Windows: mouse, keyboard and presenter keys (X11), media, volume, locking,
/// and the commands set up in Settings. The phone learns what's allowed from PC_REMOTE, but every message is checked
/// against the settings again here, so a phone can never do more than the user allowed on this PC.
/// </summary>
public sealed class RemoteControl(Settings settings, Func<LinkManager?> link)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    Timer? _poll;
    string? _sent; // the last PC_REMOTE sent on this connection
    bool _controlling, _refusedLogged;
    readonly List<Mpris.Player> _pausedForCall = [];
    bool _inCall;

    /// <summary>Call when the connection or a remote setting changes.</summary>
    public void Update()
    {
        if (link() is not { IsConnected: true } l || !l.Capabilities.Contains("remote"))
        {
            _poll?.Dispose();
            (_poll, _sent, _controlling, _refusedLogged) = (null, null, false, false);
            return;
        }
        // ponytail: what's playing and the volume are polled every 2 s, as on Windows.
        _poll ??= new Timer(_ => _ = PushAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _ = PushAsync();
    }

    async Task PushAsync()
    {
        if (!await _gate.WaitAsync(0)) return; // the last one is still gathering
        try
        {
            var state = await StateAsync();
            var json = state.ToJsonString();
            if (json == _sent || link() is not { } l) return;
            await l.SendPcRemoteAsync(state);
            _sent = json;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { } // disconnected
        finally
        {
            _gate.Release();
        }
    }

    async Task<JsonObject> StateAsync()
    {
        var p = new JsonObject
        {
            ["input"] = settings.RemoteInput && XTest.Available, // honest: false where it can't work
            ["media"] = settings.RemoteMedia,
            ["commands"] = new JsonArray(settings.RemoteCommands.Take(50)
                .Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = Cut(c.Name, 64) }).ToArray()),
        };
        if (!settings.RemoteMedia) return p; // what's playing is shared only while the phone may control it
        if (await Volume.GetAsync() is { } volume)
        {
            p["volume"] = volume.Level;
            p["muted"] = volume.Muted;
        }
        if ((await Mpris.PlayersAsync()).FirstOrDefault() is { } player)
        {
            p["app"] = Cut(player.App, 128);
            if (!string.IsNullOrEmpty(player.Title)) p["title"] = Cut(player.Title, 500);
            if (!string.IsNullOrEmpty(player.Artist)) p["artist"] = Cut(player.Artist, 500);
            p["playing"] = player.Playing;
        }
        return p;
    }

    /// <summary>REMOTE_POINTER, _BUTTON, _SCROLL, _TEXT and _KEY, on the link's thread. No reply: dropped when not allowed.</summary>
    public void Input(string type, JsonObject p)
    {
        if (!settings.RemoteInput || !XTest.Available)
        {
            if (!_refusedLogged) Log.Info($"Refused mouse and keyboard from the phone: {(settings.RemoteInput ? "not possible here" : "turned off in Settings")}");
            _refusedLogged = true;
            return;
        }
        if (!_controlling) Log.Info("The phone is controlling the mouse and keyboard"); // once per connection, never what's typed
        _controlling = true;
        switch (type)
        {
            case "REMOTE_POINTER": XTest.Move(p["dx"]!.GetValue<int>(), p["dy"]!.GetValue<int>()); break;
            case "REMOTE_SCROLL": XTest.Scroll(p["dy"]!.GetValue<int>()); break;
            case "REMOTE_BUTTON": XTest.Button(p["button"]!.GetValue<string>(), p["action"]!.GetValue<string>()); break;
            case "REMOTE_TEXT":
                if (XTest.Type(p["text"]!.GetValue<string>()) is > 0 and var skipped)
                    Log.Info($"{skipped} typed characters aren't on this keyboard layout and were skipped");
                break;
            case "REMOTE_KEY": XTest.Key(p["key"]!.GetValue<string>()); break;
        }
    }

    /// <summary>PC_MEDIA, PC_VOLUME, PC_COMMAND and PC_LOCK. Returns an error code for the phone, or null when done.</summary>
    public async Task<string?> ActAsync(string type, JsonObject p)
    {
        if (type == "PC_COMMAND") return Run(p["id"]!.GetValue<string>());
        if (!settings.RemoteMedia) return "REMOTE_OFF";
        switch (type)
        {
            case "PC_LOCK":
                if (!await Session.LockAsync()) return "FAILED";
                Log.Info("Locked for the phone");
                return null;
            case "PC_VOLUME":
                await Volume.SetAsync(p["level"]?.GetValue<int>(), p["muted"]?.GetValue<bool>());
                break;
            case "PC_MEDIA":
                if ((await Mpris.PlayersAsync()).FirstOrDefault() is not { } player) return "NOTHING_PLAYING";
                var method = p["action"]!.GetValue<string>() switch { "next" => "Next", "previous" => "Previous", _ => "PlayPause" };
                if (!await Mpris.SendAsync(player.BusName, method)) return "FAILED";
                break;
        }
        _ = Task.Delay(300).ContinueWith(_ => PushAsync()); // let the phone see the result
        return null;
    }

    string? Run(string id)
    {
        if (settings.RemoteCommands.FirstOrDefault(c => c.Id == id) is not { } command) return "NOT_FOUND";
        // As this user, no terminal; the command line is the user's own, from Settings.
        Process.Start(new ProcessStartInfo("/bin/sh", ["-c", command.Command]) { UseShellExecute = false })?.Dispose();
        Log.Info($"Ran command \"{command.Name}\" for the phone"); // the name the user gave it, never the command line
        return null;
    }

    /// <summary>Pauses what's playing while a call rings or is on, and resumes it after (the setting, on by default).</summary>
    public async Task CallChangedAsync(bool inCall)
    {
        if (inCall == _inCall) return;
        _inCall = inCall;
        if (inCall)
        {
            if (!settings.PauseMediaDuringCalls) return;
            foreach (var player in (await Mpris.PlayersAsync()).Where(p => p.Playing))
                if (await Mpris.SendAsync(player.BusName, "Pause")) _pausedForCall.Add(player);
            if (_pausedForCall.Count > 0) Log.Info($"Paused {_pausedForCall.Count} player(s) for a call");
        }
        else
        {
            foreach (var player in _pausedForCall) await Mpris.SendAsync(player.BusName, "Play");
            _pausedForCall.Clear();
        }
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
