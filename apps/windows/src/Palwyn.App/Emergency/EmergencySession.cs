using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Palwyn.Core;

namespace Palwyn.App.Emergency;

/// <summary>A message for the user, from the spec's error table.</summary>
public sealed class EmergencyException(string message) : Exception(message)
{
    public const string NoPhone = "Plug in your phone with a USB cable. It needs USB debugging on and must have allowed this PC before.";
    public const string NotAllowed = "This phone hasn't allowed this PC. With a broken screen that can't be done anymore.";
    public const string Failed = "Couldn't start the emergency screen on this phone.";
}

/// <summary>
/// The phone's screen without asking the phone: pushes the emergency helper (apps/android/emergency) through adb, runs it
/// as the shell user, and talks to it over an adb-forwarded abstract socket guarded by a one-time token
/// (docs/protocol.md, Emergency channel). Frames are Palwyn's usual u32 length + JPEG; controls are JSON lines.
/// </summary>
public sealed class EmergencySession : IAsyncDisposable
{
    const string Remote = "/data/local/tmp/palwyn-emergency.jar";
    const int MaxFrame = 1 << 20;
    static readonly string Jar = Path.Combine(AppContext.BaseDirectory, "Assets", "Emergency", "palwyn-emergency.jar");
    static readonly HashSet<EmergencySession> Open = [];

    readonly string _serial, _socket;
    readonly Process _helper;
    readonly SemaphoreSlim _write = new(1, 1);
    int _port, _disposed;
    TcpClient? _client;
    NetworkStream? _stream;
    byte[]? _first;

    public string Model { get; }

    EmergencySession(AdbDevice device, string socket, Process helper)
    {
        _serial = device.Serial;
        _socket = socket;
        _helper = helper;
        Model = device.Model?.Replace('_', ' ') ?? "phone";
    }

    /// <summary>Starts the helper on <paramref name="device"/> and waits for its first frame.</summary>
    public static async Task<EmergencySession> StartAsync(AdbDevice device, CancellationToken ct)
    {
        if (!device.IsReady) throw new EmergencyException(EmergencyException.NotAllowed);
        if (!File.Exists(Jar)) throw new EmergencyException(EmergencyException.Failed);
        if ((await Adb.RunAsync(ct, "-s", device.Serial, "push", Jar, Remote)).Exit != 0) throw new EmergencyException(EmergencyException.NoPhone);

        var token = RandomNumberGenerator.GetBytes(32);
        var socket = "palwyn-emergency-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var helper = Adb.Start("-s", device.Serial, "shell",
            $"CLASSPATH={Remote} app_process / dev.palwyn.emergency.Main {socket} {Convert.ToHexStringLower(token)}");
        var session = new EmergencySession(device, socket, helper);
        var errors = helper.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = helper.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            var (exit, port) = await Adb.RunAsync(ct, "-s", device.Serial, "forward", "tcp:0", "localabstract:" + socket);
            if (exit != 0 || !int.TryParse(port.Trim(), out session._port)) throw new EmergencyException(EmergencyException.Failed);
            await session.ConnectAsync(token, ct);
            lock (Open) Open.Add(session);
            Log.Info($"Emergency screen opened ({session.Model})");
            return session;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await session.DisposeAsync();
            if (helper.HasExited) Log.Info($"Emergency helper exited: {(await errors).Trim()}");
            throw e as EmergencyException ?? new EmergencyException(EmergencyException.Failed);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>The helper needs a second or two to start (app_process); until it listens, adb accepts the TCP
    /// connection and closes it at once. So: connect, send the token, read the first frame, and retry for up to 15 s.</summary>
    async Task ConnectAsync(byte[] token, CancellationToken ct)
    {
        var until = Environment.TickCount64 + 15_000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync("127.0.0.1", _port, ct);
                var stream = client.GetStream();
                await stream.WriteAsync(token, ct);
                using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
                first.CancelAfter(5000);
                if (await ReadFrameAsync(stream, first.Token) is { } frame)
                {
                    (_client, _stream, _first) = (client, stream, frame);
                    return;
                }
            }
            catch (Exception e) when (e is IOException or SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested)) { }
            client.Dispose();
            if (_helper.HasExited || Environment.TickCount64 > until) throw new EmergencyException(EmergencyException.Failed);
            await Task.Delay(300, ct);
        }
    }

    /// <summary>Frames until the helper or the cable goes away; a frame over 1 MiB ends the stream.</summary>
    public async IAsyncEnumerable<byte[]> FramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _first, null) is { } first) yield return first;
        while (_stream is { } stream && await ReadFrameAsync(stream, ct) is { } frame) yield return frame;
    }

    static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, ct)) return null;
        int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (length <= 0 || length > MaxFrame) return null;
        var frame = new byte[length];
        return await FillAsync(stream, frame, ct) ? frame : null;
    }

    static async Task<bool> FillAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }

    /// <summary>A SCREEN_TOUCH, SCREEN_KEY or SCREEN_TEXT, with the same payload as over the link.</summary>
    public async Task SendAsync(string type, JsonObject payload)
    {
        if (_stream is not { } stream) return;
        var line = Encoding.UTF8.GetBytes(new JsonObject { ["type"] = type, ["payload"] = payload.DeepClone() }.ToJsonString() + "\n");
        await _write.WaitAsync();
        try { await stream.WriteAsync(line); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { } // the frame reader reports the disconnect
        finally { _write.Release(); }
    }

    /// <summary>Stops the helper, removes the forward and deletes the helper from the phone. Safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (Open) Open.Remove(this);
        _client?.Dispose(); // the helper sees the socket close and exits
        try { if (!_helper.HasExited) _helper.Kill(); } catch (InvalidOperationException) { }
        _helper.Dispose();
        using var cts = new CancellationTokenSource(5000);
        try
        {
            if (_port > 0) await Adb.RunAsync(cts.Token, "-s", _serial, "forward", "--remove", $"tcp:{_port}");
            // rm first: pkill -f matches this shell command too (it contains the socket name) and ends it
            await Adb.RunAsync(cts.Token, "-s", _serial, "shell", $"rm -f {Remote}; pkill -f {_socket}");
        }
        catch (Exception e) when (e is OperationCanceledException or EmergencyException) { } // the phone is gone: nothing left to clean
        if (_client is not null) Log.Info($"Emergency screen closed ({Model})");
    }

    /// <summary>On quit: no helper may outlive Palwyn.</summary>
    public static void CloseAll()
    {
        EmergencySession[] open;
        lock (Open) open = [.. Open];
        foreach (var s in open) s.DisposeAsync().AsTask().Wait(6000);
    }
}
