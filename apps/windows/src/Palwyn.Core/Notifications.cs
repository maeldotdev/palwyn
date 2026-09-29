using System.Text.Json.Nodes;
using static Palwyn.Core.Node;

namespace Palwyn.Core;

public sealed record NotificationAction(int Index, string Title, bool Reply);

/// <summary>A phone notification from NOTIFICATION_POSTED (docs/protocol.md section 6).</summary>
/// <param name="Existing">Already in the phone's shade when the PC connected: show it, don't alert.</param>
public sealed record PhoneNotification(string Key, string Package, string AppName, string? Title, string? Text,
    DateTimeOffset PostedAt, bool Clearable, IReadOnlyList<NotificationAction> Actions, bool Existing)
{
    public static PhoneNotification From(JsonObject p) => new(
        p["key"]!.GetValue<string>(), p["package"]!.GetValue<string>(), p["appName"]!.GetValue<string>(),
        p["title"]?.GetValue<string>(), p["text"]?.GetValue<string>(),
        DateTimeOffset.FromUnixTimeMilliseconds(Long(p["postedAt"])), p["clearable"]!.GetValue<bool>(),
        p["actions"]!.AsArray().Select(n => n!.AsObject()).Select(a => new NotificationAction(
            (int)Long(a["index"]), a["title"]!.GetValue<string>(), a["reply"]!.GetValue<bool>())).ToList(),
        p["existing"]?.GetValue<bool>() ?? false);

    /// <summary>Worth a new alert compared with <paramref name="before"/> (null = first time seen)?
    /// Apps repost notifications to update them; only a change in what the user reads alerts again.</summary>
    public bool Matches(string query) =>
        query.Length == 0
        || AppName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (Title?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || (Text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    public bool AlertsOver(PhoneNotification? before) =>
        !Existing && (before is null || before.Title != Title || before.Text != Text);
}
