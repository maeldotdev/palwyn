using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Palwyn.Core;

/// <summary>Identity rules shared with Android (docs/security.md section 3).</summary>
public static class Fingerprint
{
    public static byte[] Of(X509Certificate certificate) => SHA256.HashData(certificate.GetRawCertData());

    public static string DeviceId(byte[] fingerprint) => Convert.ToHexStringLower(fingerprint, 0, 16);

    public static string Display(string deviceId) => $"{deviceId[..4].ToUpperInvariant()} {deviceId[4..8].ToUpperInvariant()}";
}

/// <summary>Pairing maths (docs/security.md section 5, shared/protocol/pairing-vectors.json).</summary>
public static class PairingCrypto
{
    public static byte[] Proof(byte[] secret, byte[] fpPc, byte[] fpPhone)
    {
        byte[] data = [.. "PB-PAIR-1"u8, .. fpPc, .. fpPhone];
        return HMACSHA256.HashData(secret, data);
    }

    public static byte[] Commit(byte[] nonce) => SHA256.HashData(nonce);

    public static string Code(byte[] nPc, byte[] nPhone, byte[] fpPc, byte[] fpPhone)
    {
        byte[] key = [.. nPc, .. nPhone];
        byte[] data = [.. "PB-SAS-1"u8, .. fpPc, .. fpPhone];
        var mac = HMACSHA256.HashData(key, data);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(mac) & 0x7FFFFFFF;
        return (value % 1_000_000).ToString("D6");
    }

    public static bool Same(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
}

/// <summary>What the PC's QR code carries: its fingerprint, a one-time secret and its name.</summary>
public sealed record PairingInvite(byte[] PcFingerprint, byte[] Secret, string PcName)
{
    public static PairingInvite Create(byte[] pcFingerprint, string pcName) =>
        new(pcFingerprint, RandomNumberGenerator.GetBytes(16), pcName);

    public string ToUri() =>
        $"palwyn://pair?v=1&fp={Base64Url.EncodeToString(PcFingerprint)}&s={Base64Url.EncodeToString(Secret)}" +
        $"&n={Uri.EscapeDataString(PcName)}";

    public static PairingInvite? Parse(string uri)
    {
        if (!uri.StartsWith("palwyn://pair?", StringComparison.Ordinal)) return null;
        var q = uri["palwyn://pair?".Length..].Split('&')
            .Select(kv => kv.Split('=', 2)).Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => kv[1]);
        try
        {
            if (q.GetValueOrDefault("v") != "1") return null;
            var fp = Base64Url.DecodeFromChars(q["fp"]);
            var secret = Base64Url.DecodeFromChars(q["s"]);
            if (fp.Length != 32 || secret.Length != 16) return null;
            return new PairingInvite(fp, secret, Uri.UnescapeDataString(q.GetValueOrDefault("n", "")));
        }
        catch (Exception e) when (e is KeyNotFoundException or FormatException) { return null; }
    }

    public override string ToString() => $"PairingInvite({PcName})"; // never print the secret
}

public static class Hex
{
    public static string Of(byte[] b) => Convert.ToHexStringLower(b);
    public static byte[] Parse(string s) => Convert.FromHexString(s);
}
