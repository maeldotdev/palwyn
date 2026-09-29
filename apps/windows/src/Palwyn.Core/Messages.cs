using System.Text.Json.Nodes;
using static Palwyn.Core.Node;

namespace Palwyn.Core;

/// <summary>A conversation from SMS_THREADS. <see cref="Names"/> aligns with <see cref="Addresses"/>; "" = no contact.</summary>
public sealed record SmsThread(string Id, IReadOnlyList<string> Addresses, IReadOnlyList<string> Names, string Snippet,
    DateTimeOffset Date, bool Unread)
{
    public string Title => Addresses.Count == 0 ? "Unknown"
        : string.Join(", ", Addresses.Select((a, i) => i < Names.Count && Names[i].Length > 0 ? Names[i] : a));

    /// <summary>Where replies go as SMS. Group conversations (several addresses) are answered with one MMS to all.</summary>
    public string? ReplyAddress => Addresses.Count == 1 ? Addresses[0] : null;

    public bool IsGroup => Addresses.Count > 1;

    /// <summary>The contact name for one of this conversation's numbers, or the number itself.</summary>
    public string NameFor(string address)
    {
        for (int i = 0; i < Addresses.Count; i++)
            if (PhoneContact.SameNumber(Addresses[i], address)) return i < Names.Count && Names[i].Length > 0 ? Names[i] : Addresses[i];
        return address;
    }

    public static IReadOnlyList<SmsThread> ListFrom(JsonObject payload) =>
        payload["threads"]!.AsArray().Select(n =>
        {
            var t = n!.AsObject();
            return new SmsThread(
                t["threadId"]!.GetValue<string>(),
                Strings(t["addresses"]), Strings(t["names"]),
                t["snippet"]!.GetValue<string>(),
                DateTimeOffset.FromUnixTimeMilliseconds(Long(t["date"])),
                t["unread"]!.GetValue<bool>());
        }).ToList();

    static List<string> Strings(JsonNode? a) => a!.AsArray().Select(s => s!.GetValue<string>()).ToList();
}

public enum SmsStatus { Done, Sending, Failed }

/// <summary>An MMS attachment (picture, video, audio, contact card), fetched with TRANSFER_PULL kind "mms".</summary>
public sealed record MmsPart(string Id, string Mime, string? Name)
{
    public bool IsImage => Mime.StartsWith("image/", StringComparison.Ordinal);

    internal static IReadOnlyList<MmsPart> ListFrom(JsonNode? parts) => parts is JsonArray a
        ? a.Select(n => new MmsPart(n!["id"]!.GetValue<string>(), n["mime"]!.GetValue<string>(), n["name"]?.GetValue<string>())).ToList()
        : [];
}

/// <param name="Name">Contact name, only on SMS_RECEIVED events.</param>
/// <param name="Parts">MMS attachments; empty for SMS.</param>
public sealed record SmsMessage(string Id, string ThreadId, string Address, string? Name, string Body, DateTimeOffset Date,
    bool Outgoing, SmsStatus Status = SmsStatus.Done, IReadOnlyList<MmsPart>? Parts = null)
{
    public IReadOnlyList<MmsPart> Attachments => Parts ?? [];

    /// <summary>The text to show where there's room for one line: the body, or what's attached.</summary>
    public string Preview => Body.Length > 0 ? Body
        : Attachments.Count == 0 ? "" : Attachments.All(p => p.IsImage) ? (Attachments.Count == 1 ? "Picture" : $"{Attachments.Count} pictures")
        : "Attachment";

    /// <summary>An SMS_RECEIVED event: a new message in the phone's SMS or MMS store, received or sent.</summary>
    public static SmsMessage FromEvent(JsonObject p) => new(
        p["messageId"]!.GetValue<string>(), p["threadId"]!.GetValue<string>(), p["address"]!.GetValue<string>(),
        p["name"]?.GetValue<string>(), p["body"]!.GetValue<string>(),
        DateTimeOffset.FromUnixTimeMilliseconds(Long(p["date"])), p["outgoing"]?.GetValue<bool>() ?? false,
        Parts: MmsPart.ListFrom(p["parts"]));

    public static IReadOnlyList<SmsMessage> ListFrom(string threadId, JsonObject payload) =>
        payload["messages"]!.AsArray().Select(n =>
        {
            var m = n!.AsObject();
            return new SmsMessage(
                m["messageId"]!.GetValue<string>(), threadId, m["address"]!.GetValue<string>(), null,
                m["body"]!.GetValue<string>(), DateTimeOffset.FromUnixTimeMilliseconds(Long(m["date"])),
                m["outgoing"]!.GetValue<bool>(),
                m["status"]?.GetValue<string>() switch { "sending" => SmsStatus.Sending, "failed" => SmsStatus.Failed, _ => SmsStatus.Done },
                MmsPart.ListFrom(m["parts"]));
        }).ToList();
}

/// <summary>
/// How many texts an SMS body is sent as (3GPP TS 23.038). One character outside the GSM 7-bit alphabet,
/// such as any emoji, switches the whole message to UCS-2: 70 units per text instead of 160.
/// </summary>
public static class SmsLength
{
    const string Gsm = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    const string GsmExtension = "^{}\\[~]|€\f"; // two septets each

    public static (int Parts, bool Unicode) Measure(string text)
    {
        int septets = 0;
        foreach (char ch in text)
        {
            if (Gsm.Contains(ch)) septets++;
            else if (GsmExtension.Contains(ch)) septets += 2;
            else return (Parts(text.Length, 70, 67), true); // UTF-16 units: an emoji counts 2
        }
        return (Parts(septets, 160, 153), false);
    }

    // A multipart text loses room to the header that joins the parts.
    static int Parts(int units, int single, int multi) => units == 0 ? 0 : units <= single ? 1 : (units + multi - 1) / multi;
}
