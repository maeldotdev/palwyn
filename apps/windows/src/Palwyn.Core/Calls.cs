using System.Text.Json.Nodes;
using static Palwyn.Core.Node;

namespace Palwyn.Core;

public enum CallState { Ringing, Active, Ended }

/// <summary>The phone's current call, from CALL_STATE (docs/protocol.md section 6).</summary>
/// <param name="Since">When <paramref name="State"/> began.</param>
public sealed record PhoneCall(string Id, CallState State, bool Incoming, string? Number, string? Name, DateTimeOffset Since)
{
    /// <summary>Parses a CALL_STATE payload that already passed catalog validation.</summary>
    public static PhoneCall From(JsonObject p) => new(
        p["callId"]!.GetValue<string>(),
        Enum.Parse<CallState>(p["state"]!.GetValue<string>(), ignoreCase: true),
        p["direction"]!.GetValue<string>() == "IN",
        p["number"]?.GetValue<string>(),
        p["name"]?.GetValue<string>(),
        DateTimeOffset.FromUnixTimeMilliseconds(Long(p["since"])));

    public string Title => Name ?? Number ?? (Incoming ? "Unknown caller" : "Unknown number");

    /// <summary>For logs, which never carry full numbers (docs/architecture.md section 7).</summary>
    public static string Mask(string? number) => number is { Length: > 0 } ? "••" + number[^Math.Min(2, number.Length)..] : "(no number)";
}

public enum CallKind { In, Out, Missed, Rejected, Blocked, Voicemail }

/// <summary>One row of the phone's call history (CALL_LOG).</summary>
public sealed record CallLogEntry(string Id, CallKind Kind, string? Number, string? Name, DateTimeOffset Date, TimeSpan Duration)
{
    public static IReadOnlyList<CallLogEntry> ListFrom(JsonObject payload) =>
        payload["calls"]!.AsArray().Select(n =>
        {
            var c = n!.AsObject();
            return new CallLogEntry(
                c["id"]!.GetValue<string>(),
                Enum.Parse<CallKind>(c["type"]!.GetValue<string>(), ignoreCase: true),
                c["number"]?.GetValue<string>(),
                c["name"]?.GetValue<string>(),
                DateTimeOffset.FromUnixTimeMilliseconds(Long(c["date"])),
                TimeSpan.FromSeconds(Long(c["duration"])));
        }).ToList();

    public string Title => Name ?? Number ?? "Unknown";
}

static class Node
{
    /// <summary>Integer field of a validated payload, whether it was parsed (long) or built in code (int).</summary>
    public static long Long(JsonNode? n) => n!.AsValue() is var v && v.TryGetValue(out long l) ? l : v.GetValue<int>();
}
