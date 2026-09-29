using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Palwyn.Core.Protocol;

public enum Side { Phone, Pc }

public enum VerdictKind { Accept, Close, Error }

public sealed record Verdict(VerdictKind Kind, string? Code = null, string? Id = null)
{
    public static readonly Verdict Accepted = new(VerdictKind.Accept);
    public static readonly Verdict Closed = new(VerdictKind.Close);
    public static Verdict Error(string code, string id) => new(VerdictKind.Error, code, id);
}

public class ProtocolException(string message) : Exception(message);

public sealed class IncompatibleVersionException() : ProtocolException("The phone runs an incompatible Palwyn version.");

/// <summary>Envelope rules from docs/protocol.md section 3, applied in order; first failure wins.</summary>
public static partial class Envelope
{
    public const int Version = 1;
    public const int MaxFrame = 1 << 20;

    public static bool LengthOk(int length) => length is > 0 and <= MaxFrame;

    public static JsonObject Create(string type, JsonObject? payload = null, string? replyTo = null)
    {
        var o = new JsonObject
        {
            ["v"] = Version,
            ["id"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            ["type"] = type,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = payload ?? new JsonObject(),
        };
        if (replyTo is not null) o["replyTo"] = replyTo;
        return o;
    }

    public static (Verdict Verdict, JsonObject? Message) Validate(
        ReadOnlySpan<byte> body, Side receiver, IReadOnlySet<string> capabilities, int version = Version)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return (Verdict.Closed, null); }
        catch (ArgumentException) { return (Verdict.Closed, null); } // invalid UTF-8

        if (o is null
            || !Json.IsInt(o["v"], out long v)
            || !Json.IsString(o["id"], out var id) || !IdPattern().IsMatch(id)
            || !Json.IsString(o["type"], out var type) || !TypePattern().IsMatch(type)
            || !Json.IsInt(o["ts"], out _)
            || o["payload"] is not JsonObject payload
            || (o["replyTo"] is { } r && !Json.IsString(r, out _)))
            return (Verdict.Closed, null);

        if (v != version) return (Verdict.Closed, null);
        if (!Catalog.Types.TryGetValue(type, out var spec) || !spec.ReceivableBy(receiver))
            return (Verdict.Error("UNSUPPORTED_TYPE", id), null);
        if (!spec.PayloadValid(payload)) return (Verdict.Error("INVALID_PAYLOAD", id), null);
        if (spec.Capability is { } cap && !capabilities.Contains(cap)) return (Verdict.Error("NOT_CAPABLE", id), null);
        return (Verdict.Accepted, o);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex TypePattern();
}

public static class FrameIO
{
    /// <returns>The frame body, or null on a clean end of stream.</returns>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        int got = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct);
        if (got == 0) return null;
        if (got < 4) throw new ProtocolException("truncated frame header");
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (!Envelope.LengthOk(length)) throw new ProtocolException($"bad frame length {length}");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (!Envelope.LengthOk(body.Length)) throw new ProtocolException("frame too large");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }
}

static class Json
{
    public static bool IsInt(JsonNode? n, out long value)
    {
        value = 0;
        return n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value);
    }

    public static bool IsString(JsonNode? n, out string value)
    {
        value = "";
        return n is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue(out value!);
    }

    public static bool IsBool(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
}
