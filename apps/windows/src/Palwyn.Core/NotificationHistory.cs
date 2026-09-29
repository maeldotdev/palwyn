using System.Text.Json;

namespace Palwyn.Core;

/// <summary>One phone notification as it was shown, kept after it leaves the phone's shade.</summary>
public sealed record HistoryEntry(string Key, string Package, string AppName, string? Title, string? Text, DateTimeOffset PostedAt)
{
    public PhoneNotification ToNotification() => new(Key, Package, AppName, Title, Text, PostedAt, false, [], true);
}

/// <summary>
/// The last <paramref name="max"/> notifications from one phone, newest first, in a JSON file on this PC
/// (docs/security.md section 7: bounded, user-clearable, deleted with the phone).
/// </summary>
public sealed class NotificationHistory(string path, int max = 500)
{
    List<HistoryEntry> _items = [];

    public IReadOnlyList<HistoryEntry> Items => _items;
    public string Path => path;

    /// <summary>Records it unless its latest entry already says the same (apps repost to update; reconnects resend).</summary>
    public bool Add(PhoneNotification n)
    {
        if (n.Title is null && n.Text is null) return false;
        var last = _items.Find(e => e.Key == n.Key);
        if (last is not null && last.Title == n.Title && last.Text == n.Text) return false;
        _items.Insert(0, new HistoryEntry(n.Key, n.Package, n.AppName, n.Title, n.Text, n.PostedAt));
        if (_items.Count > max) _items.RemoveRange(max, _items.Count - max);
        return true;
    }

    public IEnumerable<HistoryEntry> Search(string query) =>
        string.IsNullOrWhiteSpace(query) ? _items : _items.Where(e => e.ToNotification().Matches(query.Trim()));

    public void Load()
    {
        try
        {
            _items = File.Exists(path) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (JsonException)
        {
            _items = []; // a damaged file starts over
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_items));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Clear()
    {
        _items.Clear();
        File.Delete(path);
    }
}
