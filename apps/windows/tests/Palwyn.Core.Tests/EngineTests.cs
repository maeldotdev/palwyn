using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;

/// <summary>Connection engine against a fake phone that accepts connection after connection.</summary>
public class EngineTests
{
    internal static readonly X509Certificate2 PcId = Identity("pc");
    internal static readonly X509Certificate2 PhoneId = Identity("phone");
    static readonly X509Certificate2 ImposterId = Identity("imposter");
    static byte[] Fp(X509Certificate2 c) => Fingerprint.Of(c);

    static readonly EngineOptions Fast = new()
    {
        KeepAliveOverride = TimeSpan.FromMilliseconds(150),
        MinBackoff = TimeSpan.FromMilliseconds(50),
        MaxBackoff = TimeSpan.FromMilliseconds(200),
        ConnectTimeout = TimeSpan.FromSeconds(3),
    };

    static X509Certificate2 Identity(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Phone that serves every connection with <paramref name="script"/>(connection index, stream).</summary>
    internal sealed class FakePhone : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _cts = new();
        public int Connections;
        public readonly ConcurrentQueue<string> Received = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakePhone(Func<int, Stream, FakePhone, CancellationToken, Task> script, X509Certificate2? serverId = null,
            Func<byte[], bool>? acceptPc = null)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (OperationCanceledException) { return; }
                    int n = Interlocked.Increment(ref Connections);
                    _ = Task.Run(async () =>
                    {
                        using var _ = tcp;
                        try
                        {
                            await using var ssl = new SslStream(tcp.GetStream());
                            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            {
                                ServerCertificate = serverId ?? PhoneId,
                                ClientCertificateRequired = true,
                                EnabledSslProtocols = SslProtocols.Tls13,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                RemoteCertificateValidationCallback = (_, c, _, _) =>
                                    c is not null && (acceptPc ?? (fp => PairingCrypto.Same(fp, Fp(PcId))))(Fingerprint.Of(c)),
                            }, _cts.Token);
                            await script(n, ssl, this, _cts.Token);
                        }
                        catch (Exception) { /* connection-level failures are what these tests provoke */ }
                    });
                }
            });
        }

        public static async Task<JsonObject?> Read(Stream s, CancellationToken ct)
        {
            var body = await FrameIO.ReadAsync(s, ct);
            return body is null ? null : Envelope.Validate(body, Side.Phone, new HashSet<string>()).Message;
        }

        public static Task Send(Stream s, string type, JsonObject? payload, CancellationToken ct, string? replyTo = null) =>
            FrameIO.WriteAsync(s, Envelope.Create(type, payload, replyTo), ct);

        /// <summary>HELLO exchange, then answer PINGs until the PC hangs up (or <paramref name="answerPings"/> is false).</summary>
        public async Task Serve(Stream s, CancellationToken ct, bool answerPings = true, int protocolMax = 1)
        {
            await Read(s, ct);
            var hello = new Hello(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", "android", "0.5.0", ["device"], 3).ToPayload();
            hello["protocol"]!["min"] = protocolMax;
            hello["protocol"]!["max"] = protocolMax;
            await Send(s, "HELLO", hello, ct);
            while (await Read(s, ct) is { } m)
            {
                var type = m["type"]!.GetValue<string>();
                Received.Enqueue(type);
                if (type == "PING" && answerPings) await Send(s, "PONG", null, ct, m["id"]!.GetValue<string>());
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await Task.Yield();
        }
    }

    static LinkEngine Engine(FakePhone phone, byte[]? pinned = null) =>
        new("127.0.0.1", phone.Port,
            (h, p, ct) => LinkConnection.ConnectAsync(h, p, PcId, fp => PairingCrypto.Same(fp, pinned ?? Fp(PhoneId)), ct),
            new Hello(Fingerprint.DeviceId(Fp(PcId)), "PC", "windows", "0.5.0", []), Fast);

    internal static async Task Until(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Backoff_doubles_with_jitter_and_caps()
    {
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(60);
        Assert.Equal(1000 * 0.8, Backoff.Delay(0, min, max, 0).TotalMilliseconds, 3);
        Assert.Equal(4000 * 1.2, Backoff.Delay(2, min, max, 1).TotalMilliseconds, 3);
        Assert.Equal(60000, Backoff.Delay(6, min, max, 0.5).TotalMilliseconds, 3); // 64 s capped to 60
        Assert.Equal(60000 * 0.8, Backoff.Delay(1000, min, max, 0).TotalMilliseconds, 3);
    }

    [Fact]
    public async Task Connects_and_keeps_the_link_alive_with_pings()
    {
        await using var phone = new FakePhone((_, s, self, ct) => self.Serve(s, ct));
        await using var engine = Engine(phone);
        engine.Start();

        await Until(() => engine.State == EngineState.Connected);
        await Until(() => phone.Received.Count(t => t == "PING") >= 3);
        Assert.Equal(EngineState.Connected, engine.State);
        Assert.Equal(1, phone.Connections);
        Assert.Equal(3, engine.Peer!.KeepAlive);
    }

    [Fact]
    public async Task Silent_phone_is_declared_dead_and_the_engine_reconnects()
    {
        // First connection never answers pings (a frozen phone); the second behaves.
        await using var phone = new FakePhone((n, s, self, ct) => self.Serve(s, ct, answerPings: n > 1));
        await using var engine = Engine(phone);
        engine.Start();

        await Until(() => phone.Connections >= 2 && engine.State == EngineState.Connected);
    }

    [Fact]
    public async Task Dropped_connection_is_retried_with_backoff()
    {
        // Connections 1-3 drop right after HELLO; the 4th stays up.
        await using var phone = new FakePhone(async (n, s, self, ct) =>
        {
            if (n < 4)
            {
                await Read(s, ct);
                await Send(s, "HELLO", new Hello(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", "android", "0.5.0", []).ToPayload(), ct);
                return;
            }
            await self.Serve(s, ct);
        });
        await using var engine = Engine(phone);
        engine.Start();

        await Until(() => phone.Connections >= 4 && engine.State == EngineState.Connected);
    }

    static Task<JsonObject?> Read(Stream s, CancellationToken ct) => FakePhone.Read(s, ct);
    static Task Send(Stream s, string type, JsonObject? payload, CancellationToken ct, string? replyTo = null) =>
        FakePhone.Send(s, type, payload, ct, replyTo);

    [Fact]
    public async Task Kick_retries_immediately_instead_of_waiting_out_the_backoff()
    {
        await using var phone = new FakePhone((n, s, self, ct) => n == 1 ? Task.CompletedTask : self.Serve(s, ct));
        var slow = Fast with { MinBackoff = TimeSpan.FromSeconds(30), MaxBackoff = TimeSpan.FromSeconds(30) };
        await using var engine = new LinkEngine("127.0.0.1", phone.Port,
            (h, p, ct) => LinkConnection.ConnectAsync(h, p, PcId, fp => PairingCrypto.Same(fp, Fp(PhoneId)), ct),
            new Hello(Fingerprint.DeviceId(Fp(PcId)), "PC", "windows", "0.5.0", []), slow);
        engine.Start();

        await Until(() => engine.State == EngineState.Waiting);
        engine.Kick();
        await Until(() => engine.State == EngineState.Connected, timeoutMs: 3000);
    }

    [Fact]
    public async Task Phone_that_keeps_rejecting_us_stops_the_engine()
    {
        // The phone no longer pins this PC: TLS completes, then the phone drops us before HELLO.
        await using var phone = new FakePhone((_, _, _, _) => Task.CompletedTask);
        await using var engine = Engine(phone);
        engine.Start();

        await Until(() => engine.State == EngineState.Unauthorized);
        int attempts = phone.Connections;
        await Task.Delay(500);
        Assert.Equal(attempts, phone.Connections); // no more retries
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Phone_with_a_different_certificate_is_unauthorized_immediately()
    {
        await using var phone = new FakePhone((_, s, self, ct) => self.Serve(s, ct), serverId: ImposterId);
        await using var engine = Engine(phone);
        engine.Start();

        await Until(() => engine.State == EngineState.Unauthorized);
        Assert.Equal(1, phone.Connections);
    }

    [Fact]
    public async Task Incompatible_protocol_version_stops_the_engine()
    {
        await using var phone = new FakePhone((_, s, self, ct) => self.Serve(s, ct, protocolMax: 2));
        await using var engine = Engine(phone);
        engine.Start();

        await Until(() => engine.State == EngineState.Incompatible);
    }

    [Fact]
    public async Task Feature_messages_reach_the_handler_and_capabilities_update()
    {
        var gotBattery = new TaskCompletionSource<int>();
        await using var phone = new FakePhone(async (_, s, self, ct) =>
        {
            await Read(s, ct);
            await Send(s, "HELLO", new Hello(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", "android", "0.5.0", ["device"]).ToPayload(), ct);
            await Send(s, "CAPABILITIES_CHANGED", new JsonObject { ["capabilities"] = new JsonArray("device", "sms.read") }, ct);
            await Send(s, "BATTERY_CHANGED", new JsonObject { ["level"] = 64, ["charging"] = true }, ct);
            await self.Serve(s, ct);
        });
        await using var engine = Engine(phone);
        engine.Message += m =>
        {
            if (m["type"]!.GetValue<string>() == "BATTERY_CHANGED") gotBattery.TrySetResult(m["payload"]!["level"]!.GetValue<int>());
            return Task.CompletedTask;
        };
        engine.Start();

        Assert.Equal(64, await gotBattery.Task.WaitAsync(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task Requests_get_their_reply_and_phone_errors_surface()
    {
        await using var phone = new FakePhone(async (_, s, _, ct) =>
        {
            await Read(s, ct);
            await Send(s, "HELLO", new Hello(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", "android", "0.6.0",
                ["device", "calls.state", "calls.control"]).ToPayload(), ct);
            while (await FrameIO.ReadAsync(s, ct) is { } body)
            {
                var m = JsonNode.Parse(body)!.AsObject();
                var id = m["id"]!.GetValue<string>();
                switch (m["type"]!.GetValue<string>())
                {
                    case "CALL_ANSWER":
                        // An unrelated event first: replies are matched by replyTo, not by arrival order.
                        await Send(s, "CALL_STATE", new JsonObject
                        {
                            ["callId"] = "c1", ["state"] = "ACTIVE", ["direction"] = "IN", ["since"] = 1,
                        }, ct);
                        await Send(s, "CALL_RESULT", new JsonObject { ["ok"] = true }, ct, id);
                        break;
                    case "CALL_END":
                        await Send(s, "ERROR", new JsonObject { ["code"] = "FAILED" }, ct, id);
                        break;
                }
            }
        });
        await using var engine = Engine(phone);
        var events = new ConcurrentQueue<string>();
        engine.Message += m => { events.Enqueue(m["type"]!.GetValue<string>()); return Task.CompletedTask; };
        engine.Start();
        await Until(() => engine.State == EngineState.Connected);

        Assert.Contains("calls.control", engine.Capabilities);
        var ok = await engine.RequestAsync("CALL_ANSWER", new JsonObject { ["callId"] = "c1" });
        Assert.True(ok["ok"]!.GetValue<bool>());
        var error = await Assert.ThrowsAsync<PhoneErrorException>(() => engine.RequestAsync("CALL_END", new JsonObject { ["callId"] = "c1" }));
        Assert.Equal("FAILED", error.Code);
        Assert.Equal(["CALL_STATE"], events.ToArray());
    }

    [Fact]
    public async Task Pending_request_fails_when_the_connection_drops()
    {
        await using var phone = new FakePhone(async (n, s, self, ct) =>
        {
            if (n > 1) { await self.Serve(s, ct); return; }
            await Read(s, ct);
            await Send(s, "HELLO", new Hello(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", "android", "0.6.0",
                ["device", "calls.control"]).ToPayload(), ct);
            await FrameIO.ReadAsync(s, ct); // the request, never answered: hang up instead
        });
        await using var engine = Engine(phone);
        engine.Start();
        await Until(() => engine.State == EngineState.Connected);

        await Assert.ThrowsAsync<IOException>(() => engine.RequestAsync("CALL_ANSWER", new JsonObject { ["callId"] = "c1" }));
    }

    /// <summary>Connection 1 is the session; later ones are transfers that announce <paramref name="announced"/>
    /// bytes and then send <paramref name="file"/>.</summary>
    static FakePhone PhotoPhone(byte[] file, long announced) => new(async (n, s, self, ct) =>
    {
        if (n == 1) { await self.Serve(s, ct); return; }
        await Send(s, "HELLO", new Hello(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", "android", "0.9.0", ["device", "photos.read"]).ToPayload(), ct);
        var pull = JsonNode.Parse((await FrameIO.ReadAsync(s, ct))!)!.AsObject();
        Assert.Equal("TRANSFER_PULL", pull["type"]!.GetValue<string>());
        await Send(s, "TRANSFER_START", new JsonObject { ["size"] = announced, ["name"] = "IMG_2041.jpg", ["mime"] = "image/jpeg" },
            ct, pull["id"]!.GetValue<string>());
        await s.WriteAsync(file, ct);
        await s.FlushAsync(ct);
    });

    [Fact]
    public async Task Photo_arrives_intact_over_its_own_connection()
    {
        var file = RandomNumberGenerator.GetBytes(300_000); // several reads, not one
        await using var phone = PhotoPhone(file, file.Length);
        await using var engine = Engine(phone);
        engine.Start();
        await Until(() => engine.State == EngineState.Connected);

        await using var link = await engine.OpenConnectionAsync(CancellationToken.None);
        using var received = new MemoryStream();
        long reported = 0;
        var (name, _, size) = await Transfers.PullAsync(link, "photo", "812", received, new Progress<long>(p => reported = p), CancellationToken.None);

        Assert.Equal("IMG_2041.jpg", name);
        Assert.Equal(file.Length, size);
        Assert.Equal(file, received.ToArray());
        Assert.Equal(EngineState.Connected, engine.State); // the session was untouched
    }

    [Fact]
    public async Task Photo_cut_short_is_an_error_not_a_truncated_file()
    {
        await using var phone = PhotoPhone(new byte[10], announced: 100);
        await using var engine = Engine(phone);
        engine.Start();
        await Until(() => engine.State == EngineState.Connected);

        await using var link = await engine.OpenConnectionAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => Transfers.PullAsync(link, "photo", "812", new MemoryStream(), null, CancellationToken.None));
    }

    [Fact]
    public async Task File_pushed_to_the_phone_arrives_intact_and_is_confirmed()
    {
        var file = RandomNumberGenerator.GetBytes(200_000);
        var received = new TaskCompletionSource<(string Name, byte[] Bytes)>();
        await using var phone = new FakePhone(async (n, s, self, ct) =>
        {
            if (n == 1) { await self.Serve(s, ct); return; }
            await Send(s, "HELLO", new Hello(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", "android", "0.10.0", ["device", "drop"]).ToPayload(), ct);
            var push = JsonNode.Parse((await FrameIO.ReadAsync(s, ct))!)!.AsObject();
            var p = push["payload"]!;
            var bytes = new byte[p["size"]!.GetValue<long>()];
            await s.ReadExactlyAsync(bytes, ct);
            await Send(s, "TRANSFER_DONE", new JsonObject { ["ok"] = true }, ct, push["id"]!.GetValue<string>());
            received.SetResult((p["name"]!.GetValue<string>(), bytes));
        });
        await using var engine = Engine(phone);
        engine.Start();
        await Until(() => engine.State == EngineState.Connected);

        await using var link = await engine.OpenConnectionAsync(CancellationToken.None);
        await Transfers.PushAsync(link, "Report.pdf", "application/pdf", new MemoryStream(file), file.Length, null, CancellationToken.None);

        var (name, bytes) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Report.pdf", name);
        Assert.Equal(file, bytes);
    }

    [Fact]
    public void Drop_offers_parse()
    {
        var offer = DropOffer.From(new JsonObject
        {
            ["dropId"] = "a1b2", ["text"] = null,
            ["files"] = new JsonArray(new JsonObject { ["index"] = 0, ["name"] = "VID_1.mp4", ["size"] = 5_000_000, ["mime"] = "video/mp4" }),
        });
        Assert.Equal(new DropFile(0, "VID_1.mp4", 5_000_000, "video/mp4"), offer.Files[0]);
        Assert.Null(offer.Text);
    }

    [Theory]
    [InlineData("IMG_2041.jpg", "IMG_2041.jpg")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("a:b*c?.jpg", "a_b_c_.jpg")]
    [InlineData(null, "photo.jpg")]
    [InlineData("...", "photo.jpg")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.jpg", "_com1.jpg")]
    [InlineData("COM.jpg", "COM.jpg")]
    [InlineData("console.jpg", "console.jpg")]
    [InlineData("a\0b.jpg", "a_b.jpg")]
    [InlineData("..", "photo.jpg")]
    [InlineData("Mañana.jpg", "Mañana.jpg")]
    [InlineData("日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日日.jpg", "photo.jpg")] // 256 bytes: too long for a Linux file name
    public void Phone_file_names_are_made_safe(string? name, string expected) =>
        Assert.Equal(expected, PhonePhoto.SafeFileName(name, "image/jpeg"));

    [Fact]
    public void Call_payloads_parse_and_numbers_are_masked_for_logs()
    {
        var call = PhoneCall.From(new JsonObject
        {
            ["callId"] = "c1", ["state"] = "RINGING", ["direction"] = "IN", ["since"] = 1790000000000, ["number"] = "+639171234567",
        });
        Assert.Equal(CallState.Ringing, call.State);
        Assert.True(call.Incoming);
        Assert.Equal("+639171234567", call.Title);
        Assert.Equal("••67", PhoneCall.Mask(call.Number));
        Assert.Equal("(no number)", PhoneCall.Mask(null));

        var log = CallLogEntry.ListFrom(new JsonObject
        {
            ["calls"] = new JsonArray(new JsonObject { ["id"] = "9", ["type"] = "MISSED", ["date"] = 1, ["duration"] = 0 }),
        });
        Assert.Equal(CallKind.Missed, log[0].Kind);
        Assert.Equal("Unknown", log[0].Title);
    }

    [Fact]
    public async Task Dispose_stops_retrying()
    {
        await using var phone = new FakePhone((_, _, _, _) => Task.Delay(Timeout.Infinite));
        var engine = Engine(phone);
        engine.Start();
        await Until(() => phone.Connections >= 1);
        await engine.DisposeAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }
}
