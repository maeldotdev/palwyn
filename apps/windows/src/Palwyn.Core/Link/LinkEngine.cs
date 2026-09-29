using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using Palwyn.Core.Protocol;

namespace Palwyn.Core.Link;

/// <summary>The phone answered a command with ERROR (docs/protocol.md section 5).</summary>
public sealed class PhoneErrorException(string code) : Exception($"The phone reported {code}.")
{
    public string Code { get; } = code;
}

public enum EngineState { Idle, Connecting, Connected, Waiting, Unauthorized, Incompatible, Stopped }

public sealed record EngineOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan DefaultKeepAlive { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Tests only: overrides the phone's requested keep-alive.</summary>
    public TimeSpan? KeepAliveOverride { get; init; }
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);
    // A retry is one TCP SYN on the LAN, so a short cap costs little and bounds reconnect time after a phone restart.
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Consecutive "TLS completed, then dropped before HELLO" results that mean the phone no longer trusts us.</summary>
    public int RejectionsBeforeUnauthorized { get; init; } = 3;
}

public static class Backoff
{
    /// <summary>min·2^attempt capped at max, scaled by ±20% jitter (<paramref name="jitter01"/> in [0,1)).</summary>
    public static TimeSpan Delay(int attempt, TimeSpan min, TimeSpan max, double jitter01)
    {
        double ms = Math.Min(max.TotalMilliseconds, min.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 30)));
        return TimeSpan.FromMilliseconds(ms * (0.8 + 0.4 * jitter01));
    }
}

/// <summary>
/// Keeps one paired phone connected (docs/architecture.md section 4): connect, HELLO, heartbeat with a
/// silence watchdog, exponential backoff with jitter, immediate retry on <see cref="Kick"/>, and terminal
/// states for problems retrying can't fix.
/// </summary>
public sealed class LinkEngine : IAsyncDisposable
{
    public delegate Task<LinkConnection> Connector(string host, int port, CancellationToken ct);

    readonly Connector _connect;
    readonly Hello _mine;
    readonly EngineOptions _o;
    readonly CancellationTokenSource _stop = new();
    readonly Lock _gate = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenSource? _session, _attempt;
    LinkConnection? _link;
    Task? _loop;
    string _host;
    int _port;

    public EngineState State { get; private set; } = EngineState.Idle;
    public Hello? Peer { get; private set; }
    /// <summary>Why the last attempt or session ended, for logs.</summary>
    public Exception? LastError { get; private set; }
    public string Host => _host;
    public int Port => _port;

    /// <summary>Raised on the engine's thread; handlers must not block.</summary>
    public event Action<EngineState>? StateChanged;
    /// <summary>Feature messages (everything except heartbeat and capability updates).</summary>
    public event Func<JsonObject, Task>? Message;

    public LinkEngine(string host, int port, Connector connect, Hello mine, EngineOptions? options = null)
    {
        _host = host;
        _port = port;
        _connect = connect;
        _mine = mine;
        _o = options ?? new EngineOptions();
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Retry now: network changed, PC resumed, or the phone was rediscovered.</summary>
    public void Kick()
    {
        lock (_gate) _kick.TrySetResult();
    }

    public void UpdateAddress(string host, int port)
    {
        lock (_gate)
        {
            _host = host;
            _port = port;
            // Don't wait out an attempt at the old address (disposed once that attempt is over).
            if (_link is null) try { _attempt?.Cancel(); } catch (ObjectDisposedException) { }
        }
        Kick();
    }

    /// <summary>Drop the current connection (e.g. the PC is going to sleep); the engine reconnects on its own.</summary>
    public void DropConnection()
    {
        lock (_gate) _session?.Cancel();
    }

    /// <summary>What the phone can do right now (HELLO, then CAPABILITIES_CHANGED); empty while disconnected.</summary>
    public IReadOnlySet<string> Capabilities => _link?.Capabilities ?? (IReadOnlySet<string>)new HashSet<string>();

    public async Task SendAsync(string type, JsonObject? payload = null, string? replyTo = null)
    {
        var link = _link ?? throw new InvalidOperationException("not connected");
        await link.SendAsync(type, payload, replyTo);
    }

    /// <summary>A second, independent connection to the phone (same pinning), e.g. for a file transfer.</summary>
    public async Task<LinkConnection> OpenConnectionAsync(CancellationToken ct)
    {
        if (State != EngineState.Connected) throw new InvalidOperationException("not connected");
        string host;
        int port;
        lock (_gate) (host, port) = (_host, _port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(_o.ConnectTimeout);
        return await _connect(host, port, timeout.Token);
    }

    /// <summary>
    /// Sends a command and returns the payload of the phone's reply (the frame whose replyTo is this id).
    /// Throws <see cref="PhoneErrorException"/> when the phone answers ERROR, <see cref="TimeoutException"/>
    /// when it doesn't answer, and <see cref="IOException"/> when the connection drops first.
    /// </summary>
    public async Task<JsonObject> RequestAsync(string type, JsonObject? payload = null, TimeSpan? timeout = null)
    {
        var link = _link ?? throw new InvalidOperationException("not connected");
        var envelope = Envelope.Create(type, payload);
        var id = envelope["id"]!.GetValue<string>();
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply; // before sending: the reply can arrive before SendAsync returns
        try
        {
            await link.SendAsync(envelope);
            var m = await reply.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
            var p = m["payload"]!.AsObject();
            if (m["type"]!.GetValue<string>() == "ERROR") throw new PhoneErrorException(p["code"]!.GetValue<string>());
            return p;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task RunAsync()
    {
        int attempt = 0, rejections = 0;
        while (!_stop.IsCancellationRequested)
        {
            Set(EngineState.Connecting);
            bool handshook = false, helloDone = false;
            try
            {
                string host;
                int port;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                lock (_gate) (host, port, _attempt) = (_host, _port, cts);
                cts.CancelAfter(_o.ConnectTimeout);
                var link = await _connect(host, port, cts.Token);
                handshook = true;
                try { Peer = await Hello.ExchangeAsync(link, _mine, cts.Token); }
                catch { await link.DisposeAsync(); throw; }
                helloDone = true;
                attempt = rejections = 0;
                await RunSessionAsync(link);
            }
            catch (AuthenticationException)
            {
                // The phone presented a certificate that isn't the pinned one: a different install. Needs re-pairing.
                Set(EngineState.Unauthorized);
                return;
            }
            catch (IncompatibleVersionException)
            {
                Set(EngineState.Incompatible);
                return;
            }
            catch (Exception e) when (!_stop.IsCancellationRequested) // anything else is transient: never let the loop die
            {
                LastError = e;
                // TLS 1.3 reports a rejected client certificate after the handshake, as a dropped connection;
                // a timeout (frozen phone) is not a rejection.
                bool rejected = handshook && !helloDone && e is not OperationCanceledException;
                if (rejected && ++rejections >= _o.RejectionsBeforeUnauthorized)
                {
                    Set(EngineState.Unauthorized);
                    return;
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                break;
            }
            if (_stop.IsCancellationRequested) break;

            Set(EngineState.Waiting);
            await WaitForRetry(Backoff.Delay(attempt++, _o.MinBackoff, _o.MaxBackoff, Random.Shared.NextDouble()));
        }
        Set(EngineState.Stopped);
    }

    async Task RunSessionAsync(LinkConnection link)
    {
        var interval = _o.KeepAliveOverride
            ?? (Peer?.KeepAlive is int s ? TimeSpan.FromSeconds(s) : _o.DefaultKeepAlive);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        long lastHeard = Environment.TickCount64;
        lock (_gate)
        {
            _session = session;
            _link = link;
        }
        Set(EngineState.Connected);

        var heartbeat = Task.Run(async () =>
        {
            while (!session.IsCancellationRequested)
            {
                await Task.Delay(interval, session.Token);
                if (Environment.TickCount64 - Interlocked.Read(ref lastHeard) > 3 * interval.TotalMilliseconds)
                {
                    session.Cancel(); // silent for three intervals: treat as gone
                    return;
                }
                await link.SendAsync("PING", ct: session.Token);
            }
        });

        try
        {
            using var reg = session.Token.Register(() => _ = link.DisposeAsync()); // unblocks the pending read
            while (await link.ReceiveAsync(session.Token) is { } m)
            {
                Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
                if (m["replyTo"]?.GetValue<string>() is { } replyTo && _pending.TryRemove(replyTo, out var waiter))
                {
                    waiter.TrySetResult(m);
                    continue;
                }
                var payload = m["payload"]!.AsObject();
                switch (m["type"]!.GetValue<string>())
                {
                    case "PONG":
                        break;
                    case "PING":
                        await link.SendAsync("PONG", replyTo: m["id"]!.GetValue<string>(), ct: session.Token);
                        break;
                    case "CAPABILITIES_CHANGED":
                        link.Capabilities = payload["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).ToHashSet();
                        if (Message is { } onChange) await onChange(m); // the UI shows or hides features
                        break;
                    default:
                        if (Message is { } handler) await handler(m);
                        break;
                }
            }
        }
        finally
        {
            session.Cancel();
            try { await heartbeat; } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            lock (_gate)
            {
                _session = null;
                _link = null;
            }
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var waiter)) waiter.TrySetException(new IOException("The phone disconnected."));
            await link.DisposeAsync();
        }
    }

    async Task WaitForRetry(TimeSpan delay)
    {
        Task kick;
        lock (_gate) kick = _kick.Task;
        await Task.WhenAny(kick, Task.Delay(delay, _stop.Token)).ConfigureAwait(false);
        lock (_gate)
            if (_kick.Task.IsCompleted) _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    void Set(EngineState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        DropConnection();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
