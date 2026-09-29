using System.Text.Json.Nodes;
using Palwyn.Core;

public class NotificationTests
{
    static PhoneNotification Posted(string? text, bool existing = false) => PhoneNotification.From(new JsonObject
    {
        ["key"] = "0|com.whatsapp|1|null|10123", ["package"] = "com.whatsapp", ["appName"] = "WhatsApp",
        ["title"] = "Mika", ["text"] = text, ["postedAt"] = 1790000000000, ["clearable"] = true, ["existing"] = existing,
        ["actions"] = new JsonArray(new JsonObject { ["index"] = 1, ["title"] = "Reply", ["reply"] = true }),
    });

    [Fact]
    public void Only_new_or_changed_content_alerts()
    {
        var first = Posted("See you at 7");
        Assert.Equal(new NotificationAction(1, "Reply", true), first.Actions[0]);
        Assert.True(first.AlertsOver(null));
        Assert.False(Posted("See you at 7").AlertsOver(first)); // a silent repost by the app
        Assert.True(Posted("Running late").AlertsOver(first));
        Assert.False(Posted("See you at 7", existing: true).AlertsOver(null)); // already there when we connected
    }
}
