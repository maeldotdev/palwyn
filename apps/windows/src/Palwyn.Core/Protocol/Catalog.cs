using System.Text.Json.Nodes;

namespace Palwyn.Core.Protocol;

/// <param name="Sender">Who may send it; null = either side.</param>
public sealed record TypeSpec(Side? Sender, string? Capability, Func<JsonObject, bool> PayloadValid)
{
    public bool ReceivableBy(Side receiver) => Sender is null || Sender != receiver;
}

/// <summary>Message types from docs/protocol.md. Each feature phase adds its own.</summary>
public static class Catalog
{
    static readonly Func<JsonObject, bool> Any = _ => true;
    static readonly Func<JsonObject, bool> ContactNumber = n =>
        Check.Str(n, "number", 64) && Check.Enum(n, "type", "mobile", "home", "work", "other") && Check.Str(n, "label", 64, optional: true);
    /// <summary>MMS attachments on a message: absent for SMS.</summary>
    static bool MmsParts(JsonObject m) => m["parts"] is null
        || Check.ObjArray(m, "parts", 10, a => Check.Str(a, "id", 32) && Check.Str(a, "mime", 127) && Check.Str(a, "name", 255, optional: true));
    const Side P = Side.Phone, C = Side.Pc;
    /// <summary>Keys the phone's remote can press (REMOTE_KEY); letters and symbols go as REMOTE_TEXT.</summary>
    public static readonly string[] RemoteKeys =
        ["enter", "backspace", "delete", "tab", "escape", "left", "right", "up", "down", "home", "end", "pageUp", "pageDown", "f5"];

    public static readonly IReadOnlyDictionary<string, TypeSpec> Types = new Dictionary<string, TypeSpec>
    {
        ["HELLO"] = new(null, null, p =>
            p["protocol"] is JsonObject pr && Check.Int(pr, "min", 1, 1000) && Check.Int(pr, "max", 1, 1000)
            && Check.Str(p, "app", 32) && Check.Hex(p, "deviceId", 32) && Check.Str(p, "name", 64)
            && Check.Enum(p, "platform", "android", "windows", "linux") && Check.StrArray(p, "capabilities")
            && Check.Int(p, "keepAlive", 1, 300, optional: true)),
        ["PING"] = new(null, null, Any),
        ["PONG"] = new(null, null, Any),
        ["ERROR"] = new(null, null, p => Check.Str(p, "code", 64) && Check.Str(p, "message", 500, optional: true)),
        ["CAPABILITIES_CHANGED"] = new(null, null, p => Check.StrArray(p, "capabilities")),
        ["UNPAIR"] = new(null, null, Any),

        ["DEVICE_INFO"] = new(P, "device", p =>
            Check.Str(p, "manufacturer", 64) && Check.Str(p, "model", 64) && Check.Str(p, "androidVersion", 32)
            && Check.Int(p, "sdk", 1, 1000) && Check.Int(p, "storageTotal", 0, long.MaxValue)
            && Check.Int(p, "storageFree", 0, long.MaxValue)),
        ["BATTERY_CHANGED"] = new(P, "device", p => Check.Int(p, "level", 0, 100) && Check.Bool(p, "charging")),

        ["CALL_STATE"] = new(P, "calls.state", p =>
            Check.Str(p, "callId", 64) && Check.Enum(p, "state", "RINGING", "ACTIVE", "ENDED")
            && Check.Enum(p, "direction", "IN", "OUT") && Check.Int(p, "since", 0, long.MaxValue)
            && Check.Str(p, "number", 64, optional: true) && Check.Str(p, "name", 128, optional: true)),
        ["CALL_ANSWER"] = new(C, "calls.control", p => Check.Str(p, "callId", 64)),
        ["CALL_DECLINE"] = new(C, "calls.control", p => Check.Str(p, "callId", 64)),
        ["CALL_END"] = new(C, "calls.control", p => Check.Str(p, "callId", 64)),
        ["CALL_RESULT"] = new(P, "calls.control", p => Check.Bool(p, "ok")),
        ["CALL_LOG_GET"] = new(C, "calls.log", p =>
            Check.Int(p, "limit", 1, 100) && Check.Int(p, "before", 0, long.MaxValue, optional: true)
            && Check.Str(p, "query", 100, optional: true)),
        ["CALL_LOG"] = new(P, "calls.log", p => Check.ObjArray(p, "calls", 100, c =>
            Check.Str(c, "id", 32) && Check.Str(c, "number", 64, optional: true) && Check.Str(c, "name", 128, optional: true)
            && Check.Enum(c, "type", "IN", "OUT", "MISSED", "REJECTED", "BLOCKED", "VOICEMAIL")
            && Check.Int(c, "date", 0, long.MaxValue) && Check.Int(c, "duration", 0, long.MaxValue))),

        ["SMS_RECEIVED"] = new(P, "sms.read", p =>
            Check.Str(p, "threadId", 32) && Check.Str(p, "messageId", 32) && Check.Str(p, "address", 64)
            && Check.Str(p, "name", 128, optional: true) && Check.Str(p, "body", 10_000)
            && Check.Int(p, "date", 0, long.MaxValue) && Check.Bool(p, "outgoing", optional: true) && MmsParts(p)),
        ["SMS_THREADS_GET"] = new(C, "sms.read", p =>
            Check.Int(p, "limit", 1, 100) && Check.Int(p, "before", 0, long.MaxValue, optional: true)
            && Check.Str(p, "query", 100, optional: true)),
        ["SMS_THREADS"] = new(P, "sms.read", p => Check.ObjArray(p, "threads", 100, t =>
            Check.Str(t, "threadId", 32) && Check.StrArray(t, "addresses") && Check.StrArray(t, "names", 128)
            && Check.Str(t, "snippet", 200) && Check.Int(t, "date", 0, long.MaxValue) && Check.Bool(t, "unread"))),
        ["SMS_MESSAGES_GET"] = new(C, "sms.read", p =>
            Check.Str(p, "threadId", 32) && Check.Int(p, "limit", 1, 200) && Check.Int(p, "before", 0, long.MaxValue, optional: true)),
        ["SMS_MESSAGES"] = new(P, "sms.read", p => Check.ObjArray(p, "messages", 200, m =>
            Check.Str(m, "messageId", 32) && Check.Str(m, "address", 64) && Check.Str(m, "body", 10_000)
            && Check.Int(m, "date", 0, long.MaxValue) && Check.Bool(m, "outgoing") && Check.Bool(m, "read")
            && (m["status"] is null || Check.Enum(m, "status", "sending", "failed")) && MmsParts(m))),
        ["SMS_SEND"] = new(C, "sms.send", p => Check.Str(p, "address", 64) && Check.Str(p, "body", 5000)),
        ["MMS_SEND"] = new(C, "sms.send", p =>
            Check.StrArray(p, "addresses") && p["addresses"]!.AsArray().Count is >= 1 and <= 20 && Check.Str(p, "body", 5000)),
        ["SMS_SEND_RESULT"] = new(P, "sms.send", p => Check.Bool(p, "ok")),
        ["NOTIFICATION_POSTED"] = new(P, "notifications.read", p =>
            Check.Str(p, "key", 512) && Check.Str(p, "package", 128) && Check.Str(p, "appName", 128)
            && Check.Str(p, "title", 500, optional: true) && Check.Str(p, "text", 4000, optional: true)
            && Check.Int(p, "postedAt", 0, long.MaxValue) && Check.Bool(p, "clearable") && Check.Bool(p, "existing", optional: true)
            && Check.ObjArray(p, "actions", 5, a => Check.Int(a, "index", 0, 63) && Check.Str(a, "title", 64) && Check.Bool(a, "reply"))),
        ["NOTIFICATION_REMOVED"] = new(P, "notifications.read", p => Check.Str(p, "key", 512)),
        ["NOTIFICATION_DISMISS"] = new(C, "notifications.act", p => Check.Str(p, "key", 512)),
        ["NOTIFICATION_ACTION"] = new(C, "notifications.act", p =>
            Check.Str(p, "key", 512) && Check.Int(p, "index", 0, 63) && Check.Str(p, "replyText", 5000, optional: true)),
        ["NOTIFICATION_RESULT"] = new(P, "notifications.act", p => Check.Bool(p, "ok")),
        ["APP_ICON_GET"] = new(C, "notifications.read", p => Check.Str(p, "package", 128)),
        ["APP_ICON"] = new(P, "notifications.read", p => Check.Str(p, "package", 128) && Check.Str(p, "png", 90_000)),
        ["PHOTOS_GET"] = new(C, "photos.read", p =>
            Check.Int(p, "limit", 1, 200) && Check.Str(p, "beforeId", 32, optional: true) && Check.Str(p, "album", 32, optional: true)),
        ["ALBUMS_GET"] = new(C, "photos.read", _ => true),
        ["ALBUMS"] = new(P, "photos.read", p => Check.ObjArray(p, "albums", 200, a =>
            Check.Str(a, "id", 32) && Check.Str(a, "name", 255) && Check.Int(a, "count", 0, int.MaxValue))),
        ["CAMERA_REQUEST"] = new(C, "camera", _ => true),
        ["CAMERA_RESULT"] = new(P, "camera", p => Check.Bool(p, "ok")),
        ["PHOTOS"] = new(P, "photos.read", p => Check.ObjArray(p, "photos", 200, f =>
            Check.Str(f, "id", 32) && Check.Str(f, "name", 255, optional: true) && Check.Int(f, "date", 0, long.MaxValue)
            && Check.Int(f, "width", 0, 100_000) && Check.Int(f, "height", 0, 100_000)
            && Check.Int(f, "size", 0, long.MaxValue) && Check.Str(f, "mime", 64)
            && Check.Bool(f, "video", optional: true) && Check.Int(f, "duration", 0, long.MaxValue, optional: true))),
        ["PHOTO_THUMB_GET"] = new(C, "photos.read", p => Check.Str(p, "id", 32) && Check.Bool(p, "video", optional: true)),
        ["PHOTO_THUMB"] = new(P, "photos.read", p => Check.Str(p, "id", 32) && Check.Str(p, "jpeg", 200_000)),
        // Transfer connections (docs/protocol.md): the PC's first frame instead of HELLO. Pull authorization depends
        // on the kind (photos.read, or a live offer from the phone), so the phone checks it in the handler.
        ["TRANSFER_PULL"] = new(C, null, p => Check.Enum(p, "kind", "photo", "video", "drop", "mms") && Check.Str(p, "id", 64)),
        ["TRANSFER_START"] = new(P, null, p =>
            Check.Int(p, "size", 0, long.MaxValue) && Check.Str(p, "name", 255) && Check.Str(p, "mime", 127)),
        ["TRANSFER_PUSH"] = new(C, "drop", p =>
            Check.Enum(p, "kind", "file", "clipboard") && Check.Str(p, "name", 255) && Check.Int(p, "size", 0, long.MaxValue)
            && Check.Str(p, "mime", 127) && Check.Str(p, "folder", 255, optional: true)),
        ["TRANSFER_DONE"] = new(P, "drop", p => Check.Bool(p, "ok")),
        ["DROP_TEXT"] = new(C, "drop", p => Check.Str(p, "text", 50_000)),
        ["DROP_DONE"] = new(P, "drop", p => Check.Bool(p, "ok")),
        ["DROP_OFFER"] = new(P, "drop", p =>
            Check.Str(p, "dropId", 32) && Check.Str(p, "text", 50_000, optional: true)
            && (p["purpose"] is null || Check.Enum(p, "purpose", "clipboard", "camera"))
            && Check.ObjArray(p, "files", 50, f =>
                Check.Int(f, "index", 0, 49) && Check.Str(f, "name", 255) && Check.Int(f, "size", 0, long.MaxValue) && Check.Str(f, "mime", 127))),
        ["DROP_RESULT"] = new(C, "drop", p => Check.Str(p, "dropId", 32) && Check.Bool(p, "ok")),
        // Either way: the PC sends it only with sync turned on, the phone only when the user taps "Send clipboard".
        ["CLIPBOARD_SET"] = new(null, "clipboard", p => Check.Str(p, "text", 50_000)),
        // This PC's Windows notifications, sent only while its setting is on. No reply.
        ["PC_NOTIFICATION"] = new(C, "pc.notifications", p =>
            Check.Str(p, "id", 32) && Check.Str(p, "app", 128) && Check.Str(p, "title", 500, optional: true)
            && Check.Str(p, "text", 4000, optional: true)),
        ["PC_NOTIFICATION_REMOVED"] = new(C, "pc.notifications", p => Check.Str(p, "id", 32)),
        ["DEVICE_STATUS"] = new(P, "device", p =>
            Check.Bool(p, "wifi") && Check.Int(p, "wifiSignal", 0, 4, optional: true) && Check.Bool(p, "bluetooth", optional: true)
            && Check.Int(p, "storageFree", 0, long.MaxValue) && Check.Int(p, "cellSignal", 0, 4, optional: true)
            && Check.Str(p, "cellNetwork", 16, optional: true) && Check.Str(p, "carrier", 64, optional: true)),
        ["RING"] = new(C, "ring", p => Check.Bool(p, "on")),
        ["RING_RESULT"] = new(P, "ring", p => Check.Bool(p, "ok")),
        ["MEDIA_STATE"] = new(P, "media", p =>
            Check.Bool(p, "active") && Check.Str(p, "app", 128, optional: true) && Check.Str(p, "title", 500, optional: true)
            && Check.Str(p, "artist", 500, optional: true) && Check.Bool(p, "playing", optional: true)),
        ["MEDIA_CONTROL"] = new(C, "media", p => Check.Enum(p, "action", "play", "pause", "next", "previous", "volumeUp", "volumeDown")),
        ["MEDIA_RESULT"] = new(P, "media", p => Check.Bool(p, "ok")),
        ["CONTACTS_GET"] = new(C, "contacts.read", p =>
            Check.Int(p, "limit", 1, 500) && Check.Int(p, "offset", 0, 1_000_000, optional: true)),
        ["CONTACTS"] = new(P, "contacts.read", p => Check.Bool(p, "more") && Check.ObjArray(p, "contacts", 500, c =>
            Check.Str(c, "id", 32) && Check.Str(c, "name", 128) && Check.StrArray(c, "emails", 254) && Check.Bool(c, "photo", optional: true)
            && Check.ObjArray(c, "numbers", 20, ContactNumber))),
        ["CONTACT_PHOTO_GET"] = new(C, "contacts.read", p => Check.Str(p, "id", 32)),
        ["CONTACT_PHOTO"] = new(P, "contacts.read", p => Check.Str(p, "id", 32) && Check.Str(p, "jpeg", 200_000, optional: true)),
        ["CONTACT_SAVE"] = new(C, "contacts.write", p =>
            Check.Str(p, "id", 32, optional: true) && Check.Str(p, "name", 128) && Check.StrArray(p, "emails", 254)
            && Check.ObjArray(p, "numbers", 20, ContactNumber)),
        ["CONTACT_DELETE"] = new(C, "contacts.write", p => Check.Str(p, "id", 32)),
        ["CONTACT_RESULT"] = new(P, "contacts.write", p => Check.Bool(p, "ok") && Check.Str(p, "id", 32, optional: true)),

        // Phone as a remote for the PC. The PC says what it allows (its own settings) and what's playing; the phone
        // sends input only while the user is on its Remote screen. The PC checks its settings again on every message.
        ["PC_REMOTE"] = new(C, "remote", p =>
            Check.Bool(p, "input") && Check.Bool(p, "media")
            && Check.ObjArray(p, "commands", 50, c => Check.Str(c, "id", 32) && Check.Str(c, "name", 64))
            && Check.Str(p, "app", 128, optional: true) && Check.Str(p, "title", 500, optional: true)
            && Check.Str(p, "artist", 500, optional: true) && Check.Bool(p, "playing", optional: true)
            && Check.Int(p, "volume", 0, 100, optional: true) && Check.Bool(p, "muted", optional: true)),
        ["REMOTE_POINTER"] = new(P, "remote", p => Check.Int(p, "dx", -5000, 5000) && Check.Int(p, "dy", -5000, 5000)),
        ["REMOTE_BUTTON"] = new(P, "remote", p =>
            Check.Enum(p, "button", "left", "right", "middle") && Check.Enum(p, "action", "click", "down", "up")),
        ["REMOTE_SCROLL"] = new(P, "remote", p => Check.Int(p, "dy", -12000, 12000)),
        ["REMOTE_TEXT"] = new(P, "remote", p => Check.Str(p, "text", 1000)),
        ["REMOTE_KEY"] = new(P, "remote", p => Check.Enum(p, "key", RemoteKeys)),
        ["PC_MEDIA"] = new(P, "remote", p => Check.Enum(p, "action", "playPause", "next", "previous")),
        ["PC_VOLUME"] = new(P, "remote", p => Check.Int(p, "level", 0, 100, optional: true) && Check.Bool(p, "muted", optional: true)),
        ["PC_COMMAND"] = new(P, "remote", p => Check.Str(p, "id", 32)),
        ["PC_LOCK"] = new(P, "remote", _ => true),
        ["REMOTE_RESULT"] = new(C, "remote", p => Check.Bool(p, "ok")),

        // The phone's screen on the PC. Only the user can start a capture (a notification, then Android's dialog);
        // frames go on a connection of their own, opened with SCREEN_PULL. Control needs the phone's own switch
        // and its accessibility service, and works only while the screen is shared with that PC.
        ["SCREEN_REQUEST"] = new(C, "screen", _ => true),
        ["SCREEN_RESULT"] = new(P, "screen", p => Check.Bool(p, "ok")),
        ["SCREEN_STATE"] = new(P, "screen", p => Check.Enum(p, "state", "started", "declined", "stopped")),
        ["SCREEN_PULL"] = new(C, null, _ => true),
        ["SCREEN_TOUCH"] = new(C, "screen.control", p =>
            Check.Int(p, "x1", 0, 10_000) && Check.Int(p, "y1", 0, 10_000) && Check.Int(p, "x2", 0, 10_000)
            && Check.Int(p, "y2", 0, 10_000) && Check.Int(p, "ms", 1, 10_000)),
        ["SCREEN_KEY"] = new(C, "screen.control", p => Check.Enum(p, "key", "back", "home", "recents", "backspace", "enter")),
        ["SCREEN_TEXT"] = new(C, "screen.control", p => Check.Str(p, "text", 1000)),

        // Pairing (docs/security.md section 5), before any HELLO.
        ["PAIR_PROOF"] = new(P, null, p => Check.Hex(p, "mac", 64)),
        ["PAIR_COMMIT"] = new(C, null, p => Check.Hex(p, "c", 64)),
        ["PAIR_NONCE"] = new(P, null, p => Check.Hex(p, "n", 32)),
        ["PAIR_REVEAL"] = new(C, null, p => Check.Hex(p, "n", 32)),
        ["PAIR_CONFIRM"] = new(null, null, p => Check.Bool(p, "accepted")),
        ["PAIR_RESULT"] = new(C, null, p => Check.Bool(p, "ok") && Check.Str(p, "name", 64)),
    };
}

static class Check
{
    public static bool Str(JsonObject p, string key, int max, bool optional = false) =>
        p[key] is null ? optional : Json.IsString(p[key], out var s) && s.Length <= max;

    public static bool Int(JsonObject p, string key, long min, long max, bool optional = false) =>
        p[key] is null ? optional : Json.IsInt(p[key], out var v) && v >= min && v <= max;

    public static bool Bool(JsonObject p, string key, bool optional = false) => p[key] is null ? optional : Json.IsBool(p[key]);

    public static bool Enum(JsonObject p, string key, params string[] values) =>
        Json.IsString(p[key], out var s) && values.Contains(s);

    public static bool Hex(JsonObject p, string key, int length) =>
        Json.IsString(p[key], out var s) && s.Length == length && s.All(Uri.IsHexDigit);

    public static bool StrArray(JsonObject p, string key, int maxLength = 64) =>
        p[key] is JsonArray a && a.Count <= 64 && a.All(e => Json.IsString(e, out var s) && s.Length <= maxLength);

    public static bool ObjArray(JsonObject p, string key, int max, Func<JsonObject, bool> item) =>
        p[key] is JsonArray a && a.Count <= max && a.All(e => e is JsonObject o && item(o));
}
