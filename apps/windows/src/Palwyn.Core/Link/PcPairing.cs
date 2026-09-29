using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Palwyn.Core.Protocol;

namespace Palwyn.Core.Link;

public sealed class PairingException(string message) : Exception(message);

/// <summary>PC side of pairing (docs/security.md section 5). Each returns the phone's fingerprint to pin.</summary>
public static class PcPairing
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(5);

    /// <summary>QR flow: the phone proves it saw our QR by MACing both certificate fingerprints with the secret.</summary>
    public static async Task<byte[]> CompleteQrAsync(
        LinkConnection link, PairingInvite invite, DateTimeOffset expiresAt, string pcName, CancellationToken ct)
    {
        var proof = await link.ExpectAsync("PAIR_PROOF", ct);
        bool fresh = DateTimeOffset.UtcNow <= expiresAt;
        bool valid = PairingCrypto.Same(
            Hex.Parse(proof["mac"]!.GetValue<string>()),
            PairingCrypto.Proof(invite.Secret, invite.PcFingerprint, link.PeerFingerprint));

        await link.SendAsync("PAIR_RESULT", new JsonObject { ["ok"] = fresh && valid, ["name"] = pcName }, ct: ct);
        if (!fresh) throw new PairingException("The pairing code expired. Show a new QR code and try again.");
        if (!valid) throw new PairingException("The phone couldn't prove it scanned this PC's QR code.");
        return link.PeerFingerprint;
    }

    /// <summary>
    /// Code flow: commit, nonce, reveal, then both sides show the same 6 digits and the user confirms on each.
    /// <paramref name="confirm"/> shows the code and returns the user's answer; it is cancelled if the phone declines first.
    /// </summary>
    public static async Task<byte[]> CompleteCodeAsync(
        LinkConnection link, byte[] pcFingerprint, string pcName,
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken ct)
    {
        var nPc = RandomNumberGenerator.GetBytes(16);
        await link.SendAsync("PAIR_COMMIT", new JsonObject { ["c"] = Hex.Of(PairingCrypto.Commit(nPc)) }, ct: ct);
        var nPhone = Hex.Parse((await link.ExpectAsync("PAIR_NONCE", ct))["n"]!.GetValue<string>());
        await link.SendAsync("PAIR_REVEAL", new JsonObject { ["n"] = Hex.Of(nPc) }, ct: ct);
        var code = PairingCrypto.Code(nPc, nPhone, pcFingerprint, link.PeerFingerprint);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remote = link.ExpectAsync("PAIR_CONFIRM", ct);
        var local = confirm(code, cts.Token);
        if (await Task.WhenAny(remote, local) == remote && !Accepted(await remote))
        {
            cts.Cancel();
            try { await local; } catch (OperationCanceledException) { } // let the prompt close before reporting
            throw new PairingException("Pairing was cancelled on the phone.");
        }

        bool ok;
        try { ok = await local; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ok = false; }
        await link.SendAsync("PAIR_CONFIRM", new JsonObject { ["accepted"] = ok }, ct: ct);
        if (!ok) throw new PairingException("Pairing was cancelled on this PC.");
        if (!Accepted(await remote)) throw new PairingException("Pairing was cancelled on the phone.");

        await link.SendAsync("PAIR_RESULT", new JsonObject { ["ok"] = true, ["name"] = pcName }, ct: ct);
        return link.PeerFingerprint;
    }

    static bool Accepted(JsonObject confirm) => confirm["accepted"]!.GetValue<bool>();
}

/// <param name="KeepAlive">Heartbeat interval the phone asks for (seconds); null = the PC's default.</param>
public sealed record Hello(string DeviceId, string Name, string Platform, string App, HashSet<string> Capabilities, int? KeepAlive = null)
{
    public JsonObject ToPayload()
    {
        var p = new JsonObject
        {
            ["protocol"] = new JsonObject { ["min"] = Envelope.Version, ["max"] = Envelope.Version },
            ["app"] = App,
            ["deviceId"] = DeviceId,
            ["name"] = Name,
            ["platform"] = Platform,
            ["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode)c).ToArray()),
        };
        if (KeepAlive is int k) p["keepAlive"] = k;
        return p;
    }

    /// <summary>Both sides send HELLO first. The phone's claimed id must match its certificate.</summary>
    public static async Task<Hello> ExchangeAsync(LinkConnection link, Hello mine, CancellationToken ct)
    {
        await link.SendAsync("HELLO", mine.ToPayload(), ct: ct);
        var p = await link.ExpectAsync("HELLO", ct);
        var protocol = p["protocol"]!;
        if (protocol["min"]!.GetValue<int>() > Envelope.Version || protocol["max"]!.GetValue<int>() < Envelope.Version)
        {
            await link.SendAsync("ERROR", new JsonObject { ["code"] = "INCOMPATIBLE_VERSION" }, ct: ct);
            throw new IncompatibleVersionException();
        }
        var peer = new Hello(
            p["deviceId"]!.GetValue<string>(), p["name"]!.GetValue<string>(), p["platform"]!.GetValue<string>(),
            p["app"]!.GetValue<string>(), p["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).ToHashSet(),
            p["keepAlive"]?.GetValue<int>());
        if (peer.DeviceId != Fingerprint.DeviceId(link.PeerFingerprint))
            throw new ProtocolException("The phone's HELLO doesn't match its certificate.");
        link.Capabilities = peer.Capabilities;
        return peer;
    }
}
