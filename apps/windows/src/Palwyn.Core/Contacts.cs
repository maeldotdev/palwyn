using System.Text.Json.Nodes;

namespace Palwyn.Core;

/// <param name="Type">"mobile", "home", "work" or "other" (a custom label is "other" plus <paramref name="Label"/>).</param>
/// <param name="Label">The phone's own wording, e.g. "Mobile", or the custom label.</param>
public sealed record ContactNumber(string Number, string Type, string? Label = null);

/// <summary>One contact from CONTACTS (docs/protocol.md). Held in memory only.</summary>
/// <param name="Id">"" for a contact not saved yet.</param>
/// <param name="HasPhoto">The phone has a picture for it (CONTACT_PHOTO_GET).</param>
public sealed record PhoneContact(string Id, string Name, IReadOnlyList<ContactNumber> Numbers, IReadOnlyList<string> Emails,
    bool HasPhoto = false)
{
    /// <summary>Parses a CONTACTS payload that already passed catalog validation.</summary>
    public static (IReadOnlyList<PhoneContact> Contacts, bool More) PageFrom(JsonObject p) =>
        (p["contacts"]!.AsArray().Select(n =>
        {
            var c = n!.AsObject();
            return new PhoneContact(
                c["id"]!.GetValue<string>(),
                c["name"]!.GetValue<string>(),
                c["numbers"]!.AsArray().Select(x => new ContactNumber(
                    x!["number"]!.GetValue<string>(), x["type"]!.GetValue<string>(), x["label"]?.GetValue<string>())).ToList(),
                c["emails"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
                c["photo"]?.GetValue<bool>() ?? false);
        }).ToList(), p["more"]!.GetValue<bool>());

    /// <summary>CONTACT_SAVE payload. A label goes only with "other", where it is the custom label.</summary>
    public JsonObject ToSavePayload()
    {
        var p = new JsonObject
        {
            ["name"] = Name,
            ["numbers"] = new JsonArray(Numbers.Select(n =>
            {
                var o = new JsonObject { ["number"] = n.Number, ["type"] = n.Type };
                if (n.Type == "other" && !string.IsNullOrWhiteSpace(n.Label) && n.Label != "Other") o["label"] = n.Label;
                return (JsonNode)o;
            }).ToArray()),
            ["emails"] = new JsonArray(Emails.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
        };
        if (Id.Length > 0) p["id"] = Id;
        return p;
    }

    /// <summary>Name or email containing <paramref name="query"/>, or a number containing its digits ("0917 12" finds "+63 917 123 4567").</summary>
    public bool Matches(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        if (Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Emails.Any(e => e.Contains(query, StringComparison.OrdinalIgnoreCase))) return true;
        return Numbers.Any(n => NumberMatches(query, n.Number));
    }

    /// <summary>
    /// The number contains the query's digits (3 or more). A leading 0 is a national prefix, so "0917" also finds
    /// "+63 917 …", but only in numbers written with a country code. The phone applies the same rule.
    /// </summary>
    public static bool NumberMatches(string query, string number)
    {
        var digits = Digits(query);
        if (digits.Length < 3) return false;
        var n = Digits(number);
        if (n.Contains(digits)) return true;
        var core = digits.TrimStart('0');
        return core.Length >= 3 && core.Length < digits.Length && number.TrimStart().StartsWith('+') && n.Contains(core);
    }

    public bool HasNumber(string number) => Numbers.Any(n => SameNumber(n.Number, number));

    /// <summary>
    /// Same phone number written two ways ("0917 123 4567" and "+639171234567"): the last 9 digits match.
    /// ponytail: suffix rule, not full E.164 parsing; short codes (under 7 digits) must match exactly.
    /// </summary>
    public static bool SameNumber(string a, string b)
    {
        string x = Digits(a), y = Digits(b);
        if (x.Length < 7 || y.Length < 7) return x.Length > 0 && x == y;
        int n = Math.Min(9, Math.Min(x.Length, y.Length));
        return x[^n..] == y[^n..];
    }

    static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());
}
