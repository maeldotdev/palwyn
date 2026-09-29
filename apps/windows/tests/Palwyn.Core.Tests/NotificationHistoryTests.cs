using Palwyn.Core;

public class NotificationHistoryTests
{
    static PhoneNotification N(string key, string? title, string? text) =>
        new(key, "com.chat", "Chat", title, text, DateTimeOffset.Now, true, [], false);

    [Fact]
    public void Keeps_changes_only_bounded_and_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pb-history-{Guid.NewGuid():N}.json");
        var h = new NotificationHistory(path, max: 3);
        Assert.True(h.Add(N("a", "Ana", "hi")));
        Assert.False(h.Add(N("a", "Ana", "hi"))); // repost or reconnect: same words
        Assert.True(h.Add(N("a", "Ana", "hi again"))); // the app updated it
        Assert.False(h.Add(N("b", null, null))); // nothing to read
        Assert.True(h.Add(N("c", "Ben", "lunch?")));
        Assert.True(h.Add(N("d", "Cy", "call me")));
        Assert.Equal(["d", "c", "a"], h.Items.Select(e => e.Key)); // newest first, oldest dropped past 3
        Assert.Equal(["c"], h.Search("LUNCH").Select(e => e.Key));
        Assert.Equal(3, h.Search("chat").Count()); // app name

        h.Save();
        var again = new NotificationHistory(path, max: 3);
        again.Load();
        Assert.Equal(h.Items, again.Items);
        again.Clear();
        Assert.False(File.Exists(path));
    }
}
