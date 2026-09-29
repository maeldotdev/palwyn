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

/// <summary>PC-side link and pairing against a fake phone over real TLS 1.3 on loopback.</summary>
public class LinkTests
{
    static readonly X509Certificate2 PcId = NewIdentity("pc");
    static readonly X509Certificate2 PhoneId = NewIdentity("phone");
    static readonly X509Certificate2 OtherId = NewIdentity("stranger");
    static byte[] Fp(X509Certificate2 c) => Fingerprint.Of(c);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    static X509Certificate2 NewIdentity(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        // SChannel needs a persisted key, which a PKCS#12 round trip provides.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Accepts one connection, requires a client certificate that <paramref name="acceptPc"/> trusts, runs the phone script.</summary>
    static (int Port, Task Phone) FakePhone(Func<byte[], bool> acceptPc, Func<Stream, CancellationToken, Task> script)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                using var tcp = await listener.AcceptTcpClientAsync(cts.Token);
                await using var ssl = new SslStream(tcp.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = PhoneId,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, c, _, _) => c is not null && acceptPc(Fingerprint.Of(c)),
                }, cts.Token);
                await script(ssl, cts.Token);
            }
            finally { listener.Stop(); }
        });
        return (port, task);
    }

    static Task PhoneSend(Stream s, string type, JsonObject payload, CancellationToken ct) =>
        FrameIO.WriteAsync(s, Envelope.Create(type, payload), ct);

    static async Task<JsonObject> PhoneExpect(Stream s, string type, CancellationToken ct)
    {
        var body = await FrameIO.ReadAsync(s, ct) ?? throw new IOException("closed");
        var (verdict, m) = Envelope.Validate(body, Side.Phone, new HashSet<string>());
        Assert.Equal(VerdictKind.Accept, verdict.Kind);
        Assert.Equal(type, m!["type"]!.GetValue<string>());
        return m["payload"]!.AsObject();
    }

    static Task<LinkConnection> Connect(int port, X509Certificate2 id, Func<byte[], bool> acceptPhone) =>
        LinkConnection.ConnectAsync("127.0.0.1", port, id, acceptPhone, new CancellationTokenSource(Timeout).Token);

    static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    // ---- QR pairing ----

    static (int, Task) QrPhone(PairingInvite invite, byte[] secretUsed, Action<JsonObject>? onResult = null) =>
        FakePhone(pc => PairingCrypto.Same(pc, invite.PcFingerprint), async (s, ct) =>
        {
            await PhoneSend(s, "PAIR_PROOF", new JsonObject
            {
                ["mac"] = Hex.Of(PairingCrypto.Proof(secretUsed, invite.PcFingerprint, Fp(PhoneId))),
            }, ct);
            onResult?.Invoke(await PhoneExpect(s, "PAIR_RESULT", ct));
        });

    [Fact]
    public async Task Qr_pairing_succeeds_and_returns_phone_fingerprint()
    {
        var invite = PairingInvite.Create(Fp(PcId), "PC");
        JsonObject? result = null;
        var (port, phone) = QrPhone(invite, invite.Secret, r => result = r);

        await using var link = await Connect(port, PcId, _ => true);
        var pinned = await PcPairing.CompleteQrAsync(link, invite, DateTimeOffset.UtcNow.AddMinutes(5), "PC", Ct);
        await phone;

        Assert.Equal(Fp(PhoneId), pinned);
        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.Equal("PC", result["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Qr_pairing_with_wrong_secret_is_rejected()
    {
        var invite = PairingInvite.Create(Fp(PcId), "PC");
        JsonObject? result = null;
        var (port, phone) = QrPhone(invite, RandomNumberGenerator.GetBytes(16), r => result = r);

        await using var link = await Connect(port, PcId, _ => true);
        await Assert.ThrowsAsync<PairingException>(() =>
            PcPairing.CompleteQrAsync(link, invite, DateTimeOffset.UtcNow.AddMinutes(5), "PC", Ct));
        await phone;
        Assert.False(result!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Qr_pairing_after_expiry_is_rejected()
    {
        var invite = PairingInvite.Create(Fp(PcId), "PC");
        var (port, phone) = QrPhone(invite, invite.Secret);

        await using var link = await Connect(port, PcId, _ => true);
        var e = await Assert.ThrowsAsync<PairingException>(() =>
            PcPairing.CompleteQrAsync(link, invite, DateTimeOffset.UtcNow.AddSeconds(-1), "PC", Ct));
        Assert.Contains("expired", e.Message);
        await phone;
    }

    [Fact]
    public async Task Phone_in_qr_mode_refuses_a_pc_that_is_not_in_the_qr()
    {
        var invite = PairingInvite.Create(Fp(PcId), "PC");
        var (port, phone) = QrPhone(invite, invite.Secret);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var link = await Connect(port, OtherId, _ => true);
            await link.ExpectAsync("PAIR_PROOF", Ct);
        });
        await Assert.ThrowsAnyAsync<Exception>(() => phone);
    }

    // ---- Code pairing ----

    static (int, Task<string>) CodePhone(bool phoneAccepts)
    {
        var codeSeen = new TaskCompletionSource<string>();
        var (port, task) = FakePhone(_ => true, async (s, ct) =>
        {
            var commit = Hex.Parse((await PhoneExpect(s, "PAIR_COMMIT", ct))["c"]!.GetValue<string>());
            var nPhone = RandomNumberGenerator.GetBytes(16);
            await PhoneSend(s, "PAIR_NONCE", new JsonObject { ["n"] = Hex.Of(nPhone) }, ct);
            var nPc = Hex.Parse((await PhoneExpect(s, "PAIR_REVEAL", ct))["n"]!.GetValue<string>());
            Assert.True(PairingCrypto.Same(commit, PairingCrypto.Commit(nPc)));
            codeSeen.SetResult(PairingCrypto.Code(nPc, nPhone, Fp(PcId), Fp(PhoneId)));
            await PhoneSend(s, "PAIR_CONFIRM", new JsonObject { ["accepted"] = phoneAccepts }, ct);
            if (!phoneAccepts) return;
            if ((await PhoneExpect(s, "PAIR_CONFIRM", ct))["accepted"]!.GetValue<bool>())
                await PhoneExpect(s, "PAIR_RESULT", ct);
        });
        return (port, task.ContinueWith(t => { t.GetAwaiter().GetResult(); return codeSeen.Task.Result; }));
    }

    [Fact]
    public async Task Code_pairing_shows_the_same_code_on_both_sides()
    {
        var (port, phone) = CodePhone(phoneAccepts: true);
        string? pcCode = null;

        await using var link = await Connect(port, PcId, _ => true);
        var pinned = await PcPairing.CompleteCodeAsync(link, Fp(PcId), "PC",
            (code, _) => { pcCode = code; return Task.FromResult(true); }, Ct);

        Assert.Equal(Fp(PhoneId), pinned);
        Assert.Matches("^[0-9]{6}$", pcCode);
        Assert.Equal(await phone, pcCode);
    }

    [Fact]
    public async Task Code_pairing_declined_on_pc()
    {
        var (port, phone) = CodePhone(phoneAccepts: true);
        await using var link = await Connect(port, PcId, _ => true);
        await Assert.ThrowsAsync<PairingException>(() =>
            PcPairing.CompleteCodeAsync(link, Fp(PcId), "PC", (_, _) => Task.FromResult(false), Ct));
        await phone;
    }

    [Fact]
    public async Task Code_pairing_declined_on_phone_cancels_the_pc_prompt()
    {
        var (port, phone) = CodePhone(phoneAccepts: false);
        bool promptCancelled = false;
        await using var link = await Connect(port, PcId, _ => true);

        var e = await Assert.ThrowsAsync<PairingException>(() =>
            PcPairing.CompleteCodeAsync(link, Fp(PcId), "PC", async (_, ct) =>
            {
                try { await Task.Delay(Timeout, ct); }
                catch (OperationCanceledException) { promptCancelled = true; throw; }
                return true;
            }, Ct));

        Assert.Contains("phone", e.Message);
        Assert.True(promptCancelled);
        await phone;
    }

    // ---- Paired sessions ----

    static JsonObject PhoneHello(string deviceId) => new Hello(deviceId, "narzo 50", "android", "0.4.0", ["device"]).ToPayload();

    [Fact]
    public async Task Pinned_session_exchanges_hello()
    {
        var (port, phone) = FakePhone(pc => PairingCrypto.Same(pc, Fp(PcId)), async (s, ct) =>
        {
            await PhoneExpect(s, "HELLO", ct);
            await PhoneSend(s, "HELLO", PhoneHello(Fingerprint.DeviceId(Fp(PhoneId))), ct);
        });

        await using var link = await Connect(port, PcId, fp => PairingCrypto.Same(fp, Fp(PhoneId)));
        var peer = await Hello.ExchangeAsync(link, new Hello(Fingerprint.DeviceId(Fp(PcId)), "PC", "windows", "0.4.0", []), Ct);
        await phone;

        Assert.Equal("narzo 50", peer.Name);
        Assert.Contains("device", link.Capabilities);
    }

    [Fact]
    public async Task Phone_rejects_a_pc_it_has_not_paired_with()
    {
        var (port, phone) = FakePhone(pc => PairingCrypto.Same(pc, Fp(OtherId)), (_, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var link = await Connect(port, PcId, _ => true);
            await Hello.ExchangeAsync(link, new Hello(Fingerprint.DeviceId(Fp(PcId)), "PC", "windows", "0.4.0", []), Ct);
        });
        await Assert.ThrowsAnyAsync<Exception>(() => phone);
    }

    [Fact]
    public async Task Pc_rejects_a_phone_whose_certificate_is_not_pinned()
    {
        var (port, phone) = FakePhone(_ => true, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<AuthenticationException>(() => Connect(port, PcId, fp => PairingCrypto.Same(fp, Fp(OtherId))));
        try { await phone; } catch (IOException) { } // TLS 1.3: the server may finish before the client rejects it
    }

    [Fact]
    public async Task Hello_with_someone_elses_device_id_is_rejected()
    {
        var (port, phone) = FakePhone(_ => true, async (s, ct) =>
        {
            await PhoneExpect(s, "HELLO", ct);
            await PhoneSend(s, "HELLO", PhoneHello(Fingerprint.DeviceId(Fp(OtherId))), ct);
        });

        await using var link = await Connect(port, PcId, _ => true);
        await Assert.ThrowsAsync<ProtocolException>(() =>
            Hello.ExchangeAsync(link, new Hello(Fingerprint.DeviceId(Fp(PcId)), "PC", "windows", "0.4.0", []), Ct));
        await phone;
    }

    [Fact]
    public void Paired_phones_round_trip_and_remove()
    {
        var store = new PairedPhones(Path.Combine(Path.GetTempPath(), $"pb-{Guid.NewGuid():N}", "phones.json"));
        var phone = new PairedPhone(Fingerprint.DeviceId(Fp(PhoneId)), "narzo 50", Hex.Of(Fp(PhoneId)), "10.0.0.5", 47800, DateTimeOffset.UtcNow);
        store.Save(phone);
        store.Save(phone with { Name = "renamed" });

        Assert.Single(store.All());
        Assert.Equal("renamed", store.Find(Fp(PhoneId))!.Name);
        store.Remove(phone.DeviceId);
        Assert.Empty(store.All());
    }
}
