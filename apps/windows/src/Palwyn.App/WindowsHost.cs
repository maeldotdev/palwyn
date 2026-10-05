using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Palwyn.Core;
using Package = Windows.ApplicationModel.Package;

namespace Palwyn.App;

/// <summary>The link hub's view of this Windows app: its UI thread, log, settings and the classes phone events go to.</summary>
sealed class WindowsHost(DispatcherQueue ui) : IPcHost
{
    public void Post(Action action) => ui.TryEnqueue(() => action());
    public void Log(string line) => global::Palwyn.App.Log.Info(line);
    public string Platform => "windows";
    public string AppVersion
    {
        get
        {
            var v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
    public IReadOnlyCollection<string> PcCapabilities { get; } =
        ["device", "calls.state", "calls.control", "calls.log", "sms.read", "sms.send", "notifications.read", "notifications.act", "photos.read", "drop", "clipboard", "ring", "media", "contacts.read", "contacts.write", "pc.notifications", "camera", "remote"];
    public string? ActivePhone
    {
        get => AppSettings.ActivePhone;
        set => AppSettings.ActivePhone = value;
    }

    public void SetStatus(PhoneStatus status) => App.Current.SetStatus(status);
    public void OnLinkLost() => App.Current.OnLinkLost();
    public void OnPhoneRemoved() => App.PruneHistory();
    public void OnActivity(string glyph, string text) => RecentActivity.Add(glyph, text);
    public void OnSms(SmsMessage sms) => App.Current.OnSms(sms);
    public void OnNotification(PhoneNotification notification) => App.Current.OnNotification(notification);
    public void OnNotificationRemoved(string key) => App.Current.OnNotificationRemoved(key);
    public void OnCall(PhoneCall call) => App.Current.OnCall(call);
    public void OnClipboard(string text) => ClipboardSync.Received(text);
    public void OnDrop(DropOffer offer) => Receiving.Accept(offer);
    public void OnScreenState(string state) => ScreenWindow.OnState(state);
    public void OnEmergencyHint(AdbDevice device) => Toasts.EmergencyHint(device);
    public void OnRemoteInput(string type, JsonObject payload) => PcRemote.Input(type, payload);
    public Task<string?> OnRemoteActAsync(string type, JsonObject payload) => PcRemote.ActAsync(type, payload);
}
