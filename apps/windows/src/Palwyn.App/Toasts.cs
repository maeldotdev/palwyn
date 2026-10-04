using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.App;

/// <summary>
/// Windows notifications for new text messages, with a reply box. Registered through the package manifest's
/// COM activator, so clicking one also works from Notification Center after Palwyn was closed.
/// </summary>
static class Toasts
{
    static DispatcherQueue? _ui;

    public static void Init(DispatcherQueue ui)
    {
        _ui = ui;
        AppNotificationManager.Default.NotificationInvoked += (_, e) => _ui.TryEnqueue(() => Handle(e));
        AppNotificationManager.Default.Register();
    }

    public static void Shutdown() => AppNotificationManager.Default.Unregister();

    static string Group(string threadId) => "sms-" + threadId;

    public static void Sms(SmsMessage m)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "open").AddArgument("thread", m.ThreadId)
            .AddText(m.Name ?? m.Address)
            .AddText(m.Preview.Length > 500 ? m.Preview[..500] + "…" : m.Preview);
        // Replies go out as SMS from the phone; only offer them when the phone allows sending. Not for MMS:
        // it may be from a group, which gets its reply in the conversation (one MMS to everyone).
        if (App.Current.Link.Capabilities.Contains("sms.send") && !m.Id.StartsWith("mms-", StringComparison.Ordinal))
        {
            builder.AddTextBox("reply", "Reply", "")
                .AddButton(new AppNotificationButton("Send")
                    .AddArgument("action", "reply").AddArgument("thread", m.ThreadId).AddArgument("address", m.Address)
                    .SetInputId("reply"));
        }
        var notification = builder.BuildNotification();
        notification.Group = Group(m.ThreadId);
        AppNotificationManager.Default.Show(notification);
    }

    /// <summary>The user is reading this conversation in Palwyn: its notifications are old news.</summary>
    public static void Clear(string threadId) => _ = AppNotificationManager.Default.RemoveByGroupAsync(Group(threadId));

    static void Error(string text) =>
        AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("Palwyn").AddText(text).BuildNotification());

    // ---- Phone notifications ----

    const string NotificationGroup = "phone";

    // Windows caps tags at 64 characters; phone keys can be longer.
    static string Tag(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    public static void Notification(PhoneNotification n)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "notif-open")
            .AddText(n.Title ?? n.AppName)
            .SetAttributionText(n.AppName);
        if (n.Text is { } text) builder.AddText(text.Length > 500 ? text[..500] + "…" : text);
        if (AppIcons.Has(n.Package)) builder.SetAppLogoOverride(AppIcons.UriFor(n.Package), AppNotificationImageCrop.Default);
        if (App.Current.Link.Capabilities.Contains("notifications.act"))
        {
            var reply = n.Actions.FirstOrDefault(a => a.Reply);
            if (reply is not null)
                builder.AddTextBox("reply", reply.Title, "")
                    .AddButton(NotificationButton(n, reply).SetInputId("reply"));
            foreach (var a in n.Actions.Where(a => !a.Reply).Take(reply is null ? 3 : 2))
                builder.AddButton(NotificationButton(n, a));
        }
        var notification = builder.BuildNotification();
        notification.Tag = Tag(n.Key); // an update replaces the earlier one
        notification.Group = NotificationGroup;
        AppNotificationManager.Default.Show(notification);
    }

    static AppNotificationButton NotificationButton(PhoneNotification n, NotificationAction a) =>
        new AppNotificationButton(a.Title).AddArgument("action", "notif-act").AddArgument("key", n.Key).AddArgument("index", a.Index.ToString());

    public static void RemoveNotification(string key) =>
        _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(Tag(key), NotificationGroup);

    static async Task NotificationAction(IDictionary<string, string> args, IDictionary<string, string> input)
    {
        if (!args.TryGetValue("key", out var key) || !args.TryGetValue("index", out var ix) || !int.TryParse(ix, out int index)) return;
        input.TryGetValue("reply", out var text);
        bool isReply = App.Current.PhoneNotifications.GetValueOrDefault(key)?.Actions.FirstOrDefault(a => a.Index == index)?.Reply ?? text is not null;
        if (isReply && string.IsNullOrWhiteSpace(text)) return;
        try
        {
        App.Current.QuietFor(key);
        for (int i = 0; i < 50 && !App.Current.Link.IsConnected; i++) await Task.Delay(200); // launched by this click
            await App.Current.Link.NotificationActionAsync(key, index, isReply ? text!.Trim() : null);
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Notification action failed: {e.Message}");
            Error("That didn't reach your phone. The notification may be gone, or your phone isn't connected.");
        }
    }

    /// <summary>
    /// The phone's clipboard just landed here. Quiet (no sound), replaces the previous one, and leaves Notification
    /// Center after a minute: it only confirms that Ctrl+V now pastes the phone's text.
    /// </summary>
    public static void ClipboardFromPhone(string phone, string text)
    {
        var preview = text.ReplaceLineEndings(" ");
        var builder = new AppNotificationBuilder()
            .AddText($"Copied from {phone}")
            .AddText(preview.Length > 120 ? preview[..120] + "…" : preview)
            .AddText("Ready to paste")
            .MuteAudio();
        if (IsWebLink(text))
        {
            var id = Guid.NewGuid().ToString("N");
            SharedTexts[id] = text;
            builder.AddButton(new AppNotificationButton("Open").AddArgument("action", "open-link").AddArgument("text", id));
        }
        var notification = builder.BuildNotification();
        notification.Tag = "clipboard";
        notification.Group = "clipboard";
        notification.Expiration = DateTimeOffset.Now.AddMinutes(1);
        AppNotificationManager.Default.Show(notification);
    }

    // ---- Shared from the phone ----

    const string DropGroup = "drop";
    // Text is kept here, not in the notification's arguments, which have a size limit. Lost if Palwyn restarts.
    static readonly Dictionary<string, string> SharedTexts = [];

    static bool IsWebLink(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>The phone stopped answering but is on the cable and allows this PC (LinkManager).</summary>
    public static void EmergencyHint(AdbDevice device) =>
        AppNotificationManager.Default.Show(new AppNotificationBuilder()
            .AddArgument("action", "emergency")
            .AddText("Phone not responding? Open the emergency screen.")
            .AddText($"{device.Model?.Replace('_', ' ') ?? "Your phone"} is on the USB cable. You can see and control it from here, even if its screen is broken.")
            .AddButton(new AppNotificationButton("Emergency screen").AddArgument("action", "emergency"))
            .BuildNotification());

    public static void TextFromPhone(string phone, string text)
    {
        var id = Guid.NewGuid().ToString("N");
        SharedTexts[id] = text;
        var builder = new AppNotificationBuilder()
            .AddText(IsWebLink(text) ? $"Link from {phone}" : $"Text from {phone}")
            .AddText(text.Length > 500 ? text[..500] + "…" : text)
            .AddButton(new AppNotificationButton("Copy").AddArgument("action", "copy").AddArgument("text", id));
        if (IsWebLink(text)) builder.AddButton(new AppNotificationButton("Open").AddArgument("action", "open-link").AddArgument("text", id));
        AppNotificationManager.Default.Show(builder.BuildNotification());
    }

    static void ShowDrop(string dropId, AppNotificationBuilder builder)
    {
        var n = builder.BuildNotification();
        n.Tag = dropId; // "receiving" is replaced by the result
        n.Group = DropGroup;
        AppNotificationManager.Default.Show(n);
    }

    static string What(IReadOnlyCollection<string> names) => names.Count == 1 ? names.First() : $"{names.Count} files";

    public static void Receiving(string dropId, string phone, IReadOnlyList<DropFile> files) =>
        ShowDrop(dropId, new AppNotificationBuilder()
            .AddText($"Receiving from {phone}")
            .AddText(What(files.Select(f => f.Name).ToList()) + "…"));

    public static void Received(string dropId, string phone, IReadOnlyList<string> paths)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "open-folder").AddArgument("path", paths[0])
            .AddText($"Received from {phone}")
            .AddText(What(paths.Select(p => Path.GetFileName(p)).ToList()) + $", in {Where(paths)}");
        if (paths.Count == 1) builder.AddButton(new AppNotificationButton("Open").AddArgument("action", "open-file").AddArgument("path", paths[0]));
        builder.AddButton(new AppNotificationButton("Show in folder").AddArgument("action", "open-folder").AddArgument("path", paths[0]));
        ShowDrop(dropId, builder);
    }

    /// <summary>The folder they went to, or both when photos and other files were split.</summary>
    static string Where(IReadOnlyList<string> paths) =>
        paths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is [var one]
            ? AppSettings.Describe(one) : $"{AppSettings.Describe(AppSettings.PhotosFolder)} and {AppSettings.Describe(AppSettings.FilesFolder)}";

    public static void ReceiveFailed(string dropId, string phone, IReadOnlyList<string> saved) =>
        ShowDrop(dropId, new AppNotificationBuilder()
            .AddText($"Couldn't receive everything from {phone}")
            .AddText(saved.Count == 0 ? "Nothing was saved. Share it again." : $"{saved.Count} saved in {Where(saved)}. Share the rest again."));

    static async Task HandleDrop(string action, IDictionary<string, string> args)
    {
        switch (action)
        {
            case "copy" or "open-link" when args.TryGetValue("text", out var id):
                if (!SharedTexts.TryGetValue(id, out var text))
                {
                    Error("That text is no longer available. Share it again from your phone.");
                    return;
                }
                if (action == "copy")
                {
                    var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    data.SetText(text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                }
                else if (IsWebLink(text)) await Windows.System.Launcher.LaunchUriAsync(new Uri(text.Trim()));
                break;
            case "open-file" when args.TryGetValue("path", out var file) && File.Exists(file):
                await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));
                break;
            case "open-folder" when args.TryGetValue("path", out var file):
                var options = new Windows.System.FolderLauncherOptions();
                if (File.Exists(file)) options.ItemsToSelect.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));
                await Windows.System.Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(file) ?? global::Palwyn.App.Receiving.Folder, options);
                break;
        }
    }

    /// <summary>A click or reply, while running or as the launch that started Palwyn.</summary>
    public static async void Handle(AppNotificationActivatedEventArgs e)
    {
        e.Arguments.TryGetValue("action", out var action);
        e.Arguments.TryGetValue("thread", out var thread);
        if (action == "notif-act")
        {
            await NotificationAction(e.Arguments, e.UserInput);
            return;
        }
        if (action is "copy" or "open-link" or "open-file" or "open-folder")
        {
            await HandleDrop(action, e.Arguments);
            return;
        }
        if (action == "notif-open")
        {
            App.Current.ShowMain("notifications");
            return;
        }
        if (action == "emergency")
        {
            // the phone may have changed since the hint: use the cable's allowed one now
            if (App.Current.Link.UsbDevices.FirstOrDefault(d => d.IsReady) is { } device) ScreenWindow.OpenEmergency(device);
            else Error(Emergency.EmergencyException.NoPhone);
            return;
        }
        if (action == "reply" && e.Arguments.TryGetValue("address", out var address)
            && e.UserInput.TryGetValue("reply", out var text) && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                // When this reply is what launched Palwyn, the phone link is still coming up.
                for (int i = 0; i < 50 && !App.Current.Link.IsConnected; i++) await Task.Delay(200);
                await App.Current.Link.SendSmsAsync(address, text.Trim());
            }
            catch (Exception ex) when (ex is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
            {
                Log.Info($"Reply from notification failed: {ex.Message}");
                Error($"Your reply to {address} wasn't sent. Open Palwyn to try again.");
            }
            return;
        }
        App.Current.ShowMain(thread is null ? "messages" : "messages:" + thread);
    }
}
