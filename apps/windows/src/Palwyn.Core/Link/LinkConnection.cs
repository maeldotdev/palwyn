using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Palwyn.Core.Protocol;

namespace Palwyn.Core.Link;

/// <summary>
/// One mutually authenticated TLS 1.3 connection to a phone, carrying protocol frames. The phone's
/// identity is its certificate fingerprint; <c>acceptPeer</c> decides whether that fingerprint is trusted.
/// </summary>
public sealed class LinkConnection : IAsyncDisposable
{
    readonly TcpClient _tcp;
    readonly SslStream _ssl;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public byte[] PeerFingerprint { get; }
    public HashSet<string> Capabilities { get; set; } = [];

    LinkConnection(TcpClient tcp, SslStream ssl, byte[] peerFingerprint)
    {
        _tcp = tcp;
        _ssl = ssl;
        PeerFingerprint = peerFingerprint;
    }

    public static async Task<LinkConnection> ConnectAsync(
        string host, int port, X509Certificate2 identity, Func<byte[], bool> acceptPeer, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            byte[]? peer = null;
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "palwyn",
                EnabledSslProtocols = SslProtocols.Tls13,
                AllowTlsResume = false, // every connection is a full handshake, so the pin is always checked

                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificates = [identity],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
                // Trust is the pinned fingerprint only; chain and name errors are expected for self-signed identities.
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && acceptPeer(peer = Fingerprint.Of(cert)),
            }, ct);
            // Belt and braces: check the certificate actually in use, not only the one the callback saw.
            if (ssl.RemoteCertificate is not { } remote || peer is null || !Fingerprint.Of(remote).AsSpan().SequenceEqual(peer))
                throw new AuthenticationException("The phone's certificate changed during the handshake.");
            return new LinkConnection(tcp, ssl, peer);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public Task SendAsync(string type, JsonObject? payload = null, string? replyTo = null, CancellationToken ct = default) =>
        SendAsync(Envelope.Create(type, payload, replyTo), ct);

    /// <summary>Sends a ready-made envelope, for callers that need its id before it goes out.</summary>
    public async Task SendAsync(JsonObject envelope, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try { await FrameIO.WriteAsync(_ssl, envelope, ct); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Next valid message, or null when the phone closes. Invalid-but-recoverable frames are answered with ERROR.</summary>
    public async Task<JsonObject?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var body = await FrameIO.ReadAsync(_ssl, ct);
            if (body is null) return null;
            var (verdict, message) = Envelope.Validate(body, Side.Pc, Capabilities);
            switch (verdict.Kind)
            {
                case VerdictKind.Accept: return message;
                case VerdictKind.Error:
                    await SendAsync("ERROR", new JsonObject { ["code"] = verdict.Code }, verdict.Id, ct);
                    continue;
                default: throw new ProtocolException("invalid frame from phone");
            }
        }
    }

    /// <summary>
    /// The next length-prefixed blob on a screen stream (a JPEG, not JSON), or null when the phone ends the stream.
    /// Same framing and 1 MiB limit as protocol frames.
    /// </summary>
    public Task<byte[]?> ReadBlobAsync(CancellationToken ct) => FrameIO.ReadAsync(_ssl, ct);

    /// <summary>
    /// Copies exactly <paramref name="count"/> raw bytes that follow a frame on a transfer connection.
    /// Fails if the phone stops early or goes silent for 20 s.
    /// </summary>
    public async Task CopyRawAsync(Stream destination, long count, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long done = 0;
        while (done < count)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(20));
            int n = await _ssl.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - done)), idle.Token);
            if (n == 0) throw new IOException($"The phone stopped after {done} of {count} bytes.");
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            progress?.Report(done);
        }
    }

    /// <summary>Writes exactly <paramref name="count"/> raw bytes from <paramref name="source"/> after a frame on a transfer connection.</summary>
    public async Task WriteRawAsync(Stream source, long count, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long done = 0;
        await _writeLock.WaitAsync(ct);
        try
        {
            while (done < count)
            {
                int n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - done)), ct);
                if (n == 0) throw new IOException($"The file ended after {done} of {count} bytes.");
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(20)); // the phone stopped reading
                await _ssl.WriteAsync(buffer.AsMemory(0, n), idle.Token);
                done += n;
                progress?.Report(done);
            }
            await _ssl.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Receives the next message and requires it to be <paramref name="type"/>; returns its payload.</summary>
    public async Task<JsonObject> ExpectAsync(string type, CancellationToken ct)
    {
        var m = await ReceiveAsync(ct) ?? throw new ProtocolException($"connection closed while waiting for {type}");
        var actual = m["type"]!.GetValue<string>();
        if (actual == "ERROR") throw new ProtocolException($"phone reported {m["payload"]!["code"]}");
        if (actual != type) throw new ProtocolException($"expected {type}, got {actual}");
        return m["payload"]!.AsObject();
    }

    public async ValueTask DisposeAsync()
    {
        await _ssl.DisposeAsync();
        _tcp.Dispose();
        _writeLock.Dispose();
    }
}
