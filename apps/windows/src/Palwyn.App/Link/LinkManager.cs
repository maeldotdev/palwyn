using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.System.Power;
using Palwyn.Core;
using Palwyn.Core.Link;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Package = Windows.ApplicationModel.Package;

namespace Palwyn.App.Link;

/// <summary>
/// Owns this PC's identity, the paired phone and the connection engine for it, and feeds the engine the
/// signals that should trigger an immediate retry: network changes, resume from sleep, rediscovery.
/// </summary>
public sealed class LinkManager : IDisposable
{
    readonly DispatcherQueue _ui;
    readonly X509Certificate2 _identity;
    readonly PhoneDiscovery _discovery = new();
    readonly UsbLink _usb = new();
    readonly Lock _gate = new();
    LinkEngine? _engine;
    PairedPhone? _phone;
    /// <summary>Where the phone is over the network, to go back to when the USB cable is unplugged.</summary>
    (string Host, int Port) _lan;
    int? _battery;
    bool _charging, _wasConnected;

    public byte[] Fingerprint { get; }
    public PairedPhones Phones { get; } = new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "paired-phones.json"));
    public string? PhoneDetails { get; private set; }
    public DeviceInfo? Device { get; private set; }
    public DeviceStatus? DeviceStatus { get; private set; }
    public NowPlaying? NowPlaying { get; private set; }
    /// <summary>When the current connection started; null while disconnected.</summary>
    public DateTimeOffset? ConnectedSince { get; private set; }
    /// <summary>When the phone was last connected, if it isn't now.</summary>
    public DateTimeOffset? LastSeen { get; private set; }
    /// <summary>Dashboard data changed (device status, media, capabilities, connection). Raised on the UI thread.</summary>
    public event Action? DashboardChanged;
    public PhoneDiscovery Discovery => _discovery;

    public LinkManager(DispatcherQueue ui)
    {
        _ui = ui;
        _identity = IdentityStore.Load();
        Fingerprint = Core.Fingerprint.Of(_identity);
        Log.Info($"PC identity {Core.Fingerprint.Display(Core.Fingerprint.DeviceId(Fingerprint))}");
    }

    /// <summary>The phone in use: the one picked in Settings, else the first paired. One is connected at a time.</summary>
    public PairedPhone? Paired
    {
        get
        {
            var all = Phones.All();
            return all.FirstOrDefault(p => p.DeviceId == AppSettings.ActivePhone) ?? all.FirstOrDefault();
        }
    }
    public string PairingTag => "qr:" + Hex.Of(Fingerprint)[..8];

    public void Start()
    {
        _discovery.Found += OnFound;
        _discovery.Start();
        NetworkInformation.NetworkStatusChanged += _ =>
        {
            Log.Info("Network changed");
            _engine?.Kick();
        };
        PowerManager.SystemSuspendStatusChanged += (_, _) =>
        {
            var s = PowerManager.SystemSuspendStatus;
            Log.Info($"Power: {s}");
            if (s == SystemSuspendStatus.Entering) _engine?.DropConnection();
            else if (s is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume) _engine?.Kick();
        };
        if (Paired is { } p) StartEngine(p);
        else Publish(PhoneStatus.NotPaired);
        _usb.Changed += _ => TryUsb();
        _usb.DevicesChanged += () => _ui.TryEnqueue(() => UsbDevicesChanged?.Invoke());
        _usb.Start();
    }

    long _unreachableSince;
    bool _hinted;

    /// <summary>Once per episode: the paired phone hasn't answered for 20 s but an allowed phone is on the cable (Palwyn
    /// on it frozen, or the phone restarted and locked). The emergency screen still works there.</summary>
    void EmergencyHint(EngineState state)
    {
        if (state == EngineState.Connected)
        {
            (_unreachableSince, _hinted) = (0, false);
            return;
        }
        if (_unreachableSince == 0) _unreachableSince = Environment.TickCount64;
        if (_hinted || state != EngineState.Waiting || Environment.TickCount64 - _unreachableSince < 20_000) return;
        if (_usb.Devices.FirstOrDefault(d => d.IsReady) is not { } device) return;
        _hinted = true;
        Log.Info("Emergency hint shown");
        _ui.TryEnqueue(() => Toasts.EmergencyHint(device));
    }

    /// <summary>Phones on a USB cable, allowed or not: the emergency screen and Rescue files need an allowed one.</summary>
    public IReadOnlyList<AdbDevice> UsbDevices => _usb.Devices;
    /// <summary>UsbDevices changed. Raised on the UI thread.</summary>
    public event Action? UsbDevicesChanged;
    /// <summary>Looks at the cable again now (Settings opening, the user plugging in).</summary>
    public void CheckUsb() => _usb.Check();

    /// <summary>Connected, or connecting, over the USB cable.</summary>
    public bool OverUsb => _engine is { } e && UsbLink.Is(e.Host, e.Port);

    /// <summary>Moves the link to the USB cable when the phone in use is on it, and back to the network when it's unplugged.</summary>
    async void TryUsb()
    {
        if (_engine is not { } engine || _phone is not { } phone) return;
        if (!_usb.Forwarded)
        {
            if (!UsbLink.Is(engine.Host, engine.Port)) return;
            Log.Info("Link: USB cable gone, back to the network");
            engine.UpdateAddress(_lan.Host, _lan.Port);
            return;
        }
        // Check it's this phone first: the engine treats a different certificate as "needs re-pairing".
        // The phone app may still be starting, so try for a minute.
        for (int i = 0; i < 20 && _usb.Forwarded && ReferenceEquals(engine, _engine) && !UsbLink.Is(engine.Host, engine.Port); i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using (await LinkConnection.ConnectAsync(UsbLink.Host, UsbLink.LocalPort, _identity,
                                 fp => PairingCrypto.Same(fp, phone.FingerprintBytes), cts.Token)) { }
                Log.Info("Link: phone found on USB, switching to the cable");
                _lan = (engine.Host, engine.Port);
                engine.UpdateAddress(UsbLink.Host, UsbLink.LocalPort);
                engine.DropConnection(); // a Wi-Fi session reconnects over the cable now
                return;
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                Log.Info("Link: the phone on USB isn't the phone in use");
                return;
            }
            catch (Exception) { } // not reachable yet: refused, reset or timed out
            await Task.Delay(3000);
        }
    }

    void OnFound(DiscoveredPhone d)
    {
        var engine = _engine;
        if (engine is null || _phone is not { } p || p.DeviceId != d.DeviceId || d.PairMode is not null) return;
        if (p.Address is not null) return; // the user's address wins over discovery
        _usb.PhonePort = d.Port;
        if (UsbLink.Is(engine.Host, engine.Port) && _usb.Forwarded)
        {
            _lan = (d.Host, d.Port); // the cable wins; this is where to go when it's unplugged
            return;
        }
        if (d.Host != engine.Host || d.Port != engine.Port) engine.UpdateAddress(d.Host, d.Port);
        else if (engine.State != EngineState.Connected) engine.Kick(); // also mid-attempt: the next wait is skipped
    }

    Hello MyHello() => new(Core.Fingerprint.DeviceId(Fingerprint), Environment.MachineName, "windows", AppVersion(),
        ["device", "calls.state", "calls.control", "calls.log", "sms.read", "sms.send", "notifications.read", "notifications.act", "photos.read", "drop", "clipboard", "ring", "media", "contacts.read", "contacts.write", "pc.notifications", "camera", "remote"]);

    static string AppVersion()
    {
        var v = Package.Current.Id.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ---- Pairing ----

    public PairingInvite NewInvite() => PairingInvite.Create(Fingerprint, Environment.MachineName);

    public async Task<PairedPhone> PairWithQrAsync(DiscoveredPhone phone, PairingInvite invite, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var link = await ConnectForPairing(phone, ct);
        var fp = await PcPairing.CompleteQrAsync(link, invite, expiresAt, Environment.MachineName, ct);
        return await Adopt(phone, fp);
    }

    public async Task<PairedPhone> PairWithCodeAsync(
        DiscoveredPhone phone, Func<string, CancellationToken, Task<bool>> confirm, CancellationToken ct)
    {
        await using var link = await ConnectForPairing(phone, ct);
        var fp = await PcPairing.CompleteCodeAsync(link, Fingerprint, Environment.MachineName, confirm, ct);
        return await Adopt(phone, fp);
    }

    // Before pairing the phone's certificate is unknown; the pairing proof or code is what authenticates it.
    Task<LinkConnection> ConnectForPairing(DiscoveredPhone phone, CancellationToken ct) =>
        LinkConnection.ConnectAsync(phone.Host, phone.Port, _identity, _ => true, ct);

    async Task<PairedPhone> Adopt(DiscoveredPhone phone, byte[] fp)
    {
        await StopEngine();
        var paired = new PairedPhone(Core.Fingerprint.DeviceId(fp), phone.Name ?? "Android phone", Hex.Of(fp),
            phone.Host, phone.Port, DateTimeOffset.UtcNow);
        Phones.Save(paired); // other paired phones stay; the new one becomes the one in use
        AppSettings.ActivePhone = paired.DeviceId;
        Log.Info($"Paired with phone {Core.Fingerprint.Display(paired.DeviceId)}");
        StartEngine(paired);
        return paired;
    }

    /// <summary>Forgets a phone (the one in use when null). The connected phone is told; then the next paired phone, if any, is used.</summary>
    public async Task RemoveAsync(string? deviceId = null)
    {
        if (Paired is not { } active) return;
        var id = deviceId ?? active.DeviceId;
        bool inUse = id == active.DeviceId;
        if (inUse)
        {
            if (_engine is { State: EngineState.Connected } e)
            {
                try { await e.SendAsync("UNPAIR"); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
            }
            await StopEngine();
        }
        Phones.Remove(id);
        App.PruneHistory(); // its notification history goes now, not at the next phone switch
        Log.Info($"Removed phone {Core.Fingerprint.Display(id)}");
        if (inUse) UseNext();
        else Publish(App.Current.Status); // the list changed; the link didn't
    }

    /// <summary>After the phone in use went away: carry on with another paired phone, or none.</summary>
    void UseNext()
    {
        _ui.TryEnqueue(App.Current.OnLinkLost);
        if (Paired is { } next)
        {
            AppSettings.ActivePhone = next.DeviceId;
            StartEngine(next);
            Publish(new PhoneStatus(ConnectionState.Connecting, next.Name));
        }
        else Publish(PhoneStatus.NotPaired);
    }

    /// <summary>Disconnects the phone in use and connects to another paired one.</summary>
    public async Task SwitchToAsync(string deviceId)
    {
        if (Paired?.DeviceId == deviceId || Phones.All().FirstOrDefault(p => p.DeviceId == deviceId) is not { } phone) return;
        await StopEngine();
        AppSettings.ActivePhone = deviceId;
        Log.Info($"Switched to phone {Core.Fingerprint.Display(deviceId)}");
        _ui.TryEnqueue(App.Current.OnLinkLost); // the last phone's calls and notifications
        StartEngine(phone);
        Publish(new PhoneStatus(ConnectionState.Connecting, phone.Name));
    }

    /// <summary>Sets or clears (null) the address a phone is reached at; reconnects if it's the phone in use.</summary>
    public async Task SetAddressAsync(string deviceId, string? address)
    {
        if (Phones.All().FirstOrDefault(p => p.DeviceId == deviceId) is not { } phone) return;
        phone = phone with { Address = address };
        Phones.Save(phone);
        Log.Info(address is null ? "Phone address cleared" : "Phone address set"); // not the address: it can identify a network
        if (Paired?.DeviceId != deviceId)
        {
            Publish(App.Current.Status);
            return;
        }
        await StopEngine();
        StartEngine(phone);
        Publish(new PhoneStatus(ConnectionState.Connecting, phone.Name));
    }

    // ---- Calls ----

    public bool IsConnected => _engine?.State == EngineState.Connected;
    public IReadOnlySet<string> Capabilities => _engine?.Capabilities ?? (IReadOnlySet<string>)new HashSet<string>();

    /// <summary>CALL_ANSWER / CALL_DECLINE / CALL_END. Throws when the phone refuses or can't be reached.</summary>
    public async Task CallCommandAsync(string type, string callId)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync(type, new JsonObject { ["callId"] = callId }, TimeSpan.FromSeconds(8));
        Log.Info($"{type}: ok");
    }

    /// <param name="query">Only calls whose contact name or number matches (searched on the phone).</param>
    public async Task<IReadOnlyList<CallLogEntry>> CallHistoryAsync(int limit, DateTimeOffset? before = null, string? query = null)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var request = new JsonObject { ["limit"] = limit };
        if (before is { } b) request["before"] = b.ToUnixTimeMilliseconds();
        if (!string.IsNullOrWhiteSpace(query)) request["query"] = query.Trim();
        return CallLogEntry.ListFrom(await engine.RequestAsync("CALL_LOG_GET", request));
    }

    // ---- Contacts ----

    public async Task<(IReadOnlyList<PhoneContact> Contacts, bool More)> ContactsAsync(int limit, int offset)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        return PhoneContact.PageFrom(await engine.RequestAsync("CONTACTS_GET", new JsonObject { ["limit"] = limit, ["offset"] = offset }));
    }

    /// <summary>Creates the contact (empty id) or replaces its details on the phone. Returns its id.</summary>
    public async Task<string> SaveContactAsync(PhoneContact contact)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var reply = await engine.RequestAsync("CONTACT_SAVE", contact.ToSavePayload());
        Log.Info(contact.Id.Length == 0 ? "Contact added" : "Contact edited"); // never the name
        return reply["id"]?.GetValue<string>() ?? contact.Id;
    }

    public async Task DeleteContactAsync(string id)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var reply = await engine.RequestAsync("CONTACT_DELETE", new JsonObject { ["id"] = id });
        if (reply["ok"]?.GetValue<bool>() != true) throw new PhoneErrorException("FAILED");
        Log.Info("Contact deleted");
    }

    // ---- Messages ----

    /// <param name="query">Only conversations whose contact or number matches, or with an SMS containing it (searched on the phone).</param>
    public async Task<IReadOnlyList<SmsThread>> SmsThreadsAsync(int limit, DateTimeOffset? before = null, string? query = null)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var request = new JsonObject { ["limit"] = limit };
        if (before is { } b) request["before"] = b.ToUnixTimeMilliseconds();
        if (!string.IsNullOrWhiteSpace(query)) request["query"] = query.Trim();
        return SmsThread.ListFrom(await engine.RequestAsync("SMS_THREADS_GET", request));
    }

    public async Task<IReadOnlyList<SmsMessage>> SmsMessagesAsync(string threadId, int limit, DateTimeOffset? before = null)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var request = new JsonObject { ["threadId"] = threadId, ["limit"] = limit };
        if (before is { } b) request["before"] = b.ToUnixTimeMilliseconds();
        return SmsMessage.ListFrom(threadId, await engine.RequestAsync("SMS_MESSAGES_GET", request));
    }

    /// <summary>Completes when the phone's radio reports the SMS sent (the phone waits up to 60 s for that).</summary>
    public async Task SendSmsAsync(string address, string body)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync("SMS_SEND", new JsonObject { ["address"] = address, ["body"] = body }, TimeSpan.FromSeconds(75));
        Log.Info($"SMS to {PhoneCall.Mask(address)}: sent");
    }

    /// <summary>A group text: one MMS to every address. The phone waits up to 120 s for the carrier.</summary>
    public async Task SendGroupAsync(IReadOnlyList<string> addresses, string body)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var to = new JsonArray(addresses.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        await engine.RequestAsync("MMS_SEND", new JsonObject { ["addresses"] = to, ["body"] = body }, TimeSpan.FromSeconds(135));
        Log.Info($"MMS to {addresses.Count} people: sent");
    }

    /// <summary>The contact's picture as JPEG bytes, or null when it has none.</summary>
    public async Task<byte[]?> ContactPhotoAsync(string id)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var reply = await engine.RequestAsync("CONTACT_PHOTO_GET", new JsonObject { ["id"] = id });
        return reply["jpeg"]?.GetValue<string>() is { } jpeg ? Convert.FromBase64String(jpeg) : null;
    }

    // ---- Notifications ----

    public async Task DismissNotificationAsync(string key)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync("NOTIFICATION_DISMISS", new JsonObject { ["key"] = key });
    }

    public async Task NotificationActionAsync(string key, int index, string? replyText = null)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var request = new JsonObject { ["key"] = key, ["index"] = index };
        if (replyText is not null) request["replyText"] = replyText;
        await engine.RequestAsync("NOTIFICATION_ACTION", request);
    }

    public async Task<byte[]> AppIconAsync(string package)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var reply = await engine.RequestAsync("APP_ICON_GET", new JsonObject { ["package"] = package });
        return Convert.FromBase64String(reply["png"]!.GetValue<string>());
    }

    // ---- Photos ----

    /// <param name="album">Only this album (an ALBUMS id); null for all.</param>
    public async Task<IReadOnlyList<PhonePhoto>> PhotosAsync(int limit, string? beforeId = null, string? album = null)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var request = new JsonObject { ["limit"] = limit };
        if (beforeId is not null) request["beforeId"] = beforeId;
        if (album is not null) request["album"] = album;
        return PhonePhoto.ListFrom(await engine.RequestAsync("PHOTOS_GET", request));
    }

    public async Task<IReadOnlyList<PhotoAlbum>> AlbumsAsync()
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        return PhotoAlbum.ListFrom(await engine.RequestAsync("ALBUMS_GET", new JsonObject()));
    }

    /// <summary>Asks the phone for a photo: it shows a notification that opens its camera; the photo arrives as a drop.</summary>
    public async Task RequestCameraAsync()
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync("CAMERA_REQUEST", new JsonObject());
        Log.Info("Photo requested from the phone");
    }


    public async Task<byte[]> PhotoThumbnailAsync(string id, bool video)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var reply = await engine.RequestAsync("PHOTO_THUMB_GET", new JsonObject { ["id"] = id, ["video"] = video });
        return Convert.FromBase64String(reply["jpeg"]!.GetValue<string>());
    }

    /// <summary>
    /// Copies a phone file (<paramref name="kind"/> "photo", "video" or "drop") to <paramref name="path"/> over its own
    /// connection. Written to a temporary name first, so a failed copy never leaves a partial file behind.
    /// </summary>
    public async Task DownloadAsync(string kind, string id, string path, IProgress<long>? progress, CancellationToken ct)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var partial = path + ".partial";
        try
        {
            await using (var link = await engine.OpenConnectionAsync(ct))
            await using (var file = File.Create(partial))
            {
                var started = Environment.TickCount64;
                var (_, _, size) = await Transfers.PullAsync(link, kind, id, file, progress, ct);
                Log.Info($"Received {kind}: {size / 1024} KB in {Environment.TickCount64 - started} ms");
            }
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial); // no-op after a successful move
        }
    }

    // ---- Quick Drop ----

    /// <summary>
    /// Sends a file to the phone's Download/Palwyn folder (inside <paramref name="folder"/>, "Trip/Day 1", when
    /// sending a folder) over its own connection.
    /// </summary>
    public async Task SendFileAsync(string name, string mime, Stream source, long size, IProgress<long>? progress, CancellationToken ct,
        string? folder = null)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await using var link = await engine.OpenConnectionAsync(ct);
        var started = Environment.TickCount64;
        await Transfers.PushAsync(link, name, mime, source, size, progress, ct, folder: folder);
        Log.Info($"Sent file: {size / 1024} KB in {Environment.TickCount64 - started} ms");
    }

    /// <summary>A PNG for the phone's clipboard, over its own connection.</summary>
    public async Task SendClipboardImageAsync(byte[] png)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await using var link = await engine.OpenConnectionAsync(CancellationToken.None);
        using var source = new MemoryStream(png);
        await Transfers.PushAsync(link, "clipboard.png", "image/png", source, png.Length, null, CancellationToken.None, kind: "clipboard");
        Log.Info($"Clipboard image to phone: {png.Length / 1024} KB");
    }


    /// <summary>Text or a link, shown on the phone as a notification.</summary>
    public async Task SendTextAsync(string text)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync("DROP_TEXT", new JsonObject { ["text"] = text });
    }

    /// <summary>One of this PC's Windows notifications, shown on the phone. No reply; logs never hold its words.</summary>
    public async Task SendPcNotificationAsync(string id, string app, string? title, string? text)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var p = new JsonObject { ["id"] = id, ["app"] = app };
        if (title is not null) p["title"] = title;
        if (text is not null) p["text"] = text;
        await engine.SendAsync("PC_NOTIFICATION", p);
    }

    public async Task RemovePcNotificationAsync(string id)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.SendAsync("PC_NOTIFICATION_REMOVED", new JsonObject { ["id"] = id });
    }

    public async Task SendClipboardAsync(string text)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.SendAsync("CLIPBOARD_SET", new JsonObject { ["text"] = text });
    }

    /// <summary>Rings the phone at full volume (on = false stops it).</summary>
    public async Task RingAsync(bool on)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync("RING", new JsonObject { ["on"] = on });
    }

    /// <summary>play, pause, next, previous, volumeUp, volumeDown.</summary>
    public async Task MediaAsync(string action)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync("MEDIA_CONTROL", new JsonObject { ["action"] = action });
    }

    // ---- Phone screen ----

    /// <summary>Asks the phone to share its screen: it shows a notification; the user's consent there starts it (SCREEN_STATE).</summary>
    public async Task RequestScreenAsync()
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.RequestAsync("SCREEN_REQUEST", new JsonObject());
        Log.Info("Phone screen requested");
    }

    /// <summary>The connection the phone streams its screen on, after SCREEN_STATE "started".</summary>
    public async Task<LinkConnection> OpenScreenAsync(CancellationToken ct)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        var link = await engine.OpenConnectionAsync(ct);
        try
        {
            await Transfers.OpenScreenAsync(link, ct);
            return link;
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    /// <summary>SCREEN_TOUCH / SCREEN_KEY / SCREEN_TEXT: no reply; dropped by the phone unless control is on.</summary>
    public async Task ScreenControlAsync(string type, JsonObject payload)
    {
        if (_engine is not { } engine) return;
        try { await engine.SendAsync(type, payload); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>What the phone's remote may do on this PC, and what's playing here. No reply.</summary>
    public async Task SendPcRemoteAsync(JsonObject state)
    {
        var engine = _engine ?? throw new InvalidOperationException("not connected");
        await engine.SendAsync("PC_REMOTE", state);
    }

    /// <summary>Tells the phone its share finished, so its "Send to PC" screen can close.</summary>
    public async Task DropResultAsync(string dropId, bool ok)
    {
        if (_engine is not { } engine) return;
        try { await engine.SendAsync("DROP_RESULT", new JsonObject { ["dropId"] = dropId, ["ok"] = ok }); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    // ---- Engine ----

    void StartEngine(PairedPhone phone)
    {
        var (host, port) = phone.Endpoint;
        var engine = new LinkEngine(host, port,
            (host, port, ct) => LinkConnection.ConnectAsync(host, port, _identity,
                fp => PairingCrypto.Same(fp, phone.FingerprintBytes), ct),
            MyHello());
        engine.StateChanged += s => OnEngineState(engine, s);
        engine.Message += m => OnMessage(engine, m);
        lock (_gate)
        {
            _engine = engine;
            _phone = phone;
            _battery = null;
            _lan = (host, port);
        }
        engine.Start();
        if (_usb.Forwarded) TryUsb();
    }

    async Task StopEngine()
    {
        LinkEngine? engine;
        lock (_gate)
        {
            engine = _engine;
            _engine = null;
            _phone = null;
            _wasConnected = false; // the next engine starts from scratch
            PhoneDetails = null;
            (Device, DeviceStatus, NowPlaying, ConnectedSince, LastSeen) = (null, null, null, null, null);
        }
        if (engine is not null) await engine.DisposeAsync();
    }

    void OnEngineState(LinkEngine engine, EngineState state)
    {
        if (!ReferenceEquals(engine, _engine) || _phone is not { } phone) return;
        if (engine.LastError is { } err && state == EngineState.Waiting)
            Log.Info($"Link: {state} ({err.GetType().Name}: {err.Message})");
        else Log.Info($"Link: {state}");
        // Events stop with the link, so the call card and the notification list would go stale.
        if (state != EngineState.Connected && _wasConnected)
        {
            (ConnectedSince, LastSeen, NowPlaying) = (null, DateTimeOffset.Now, null);
            _ui.TryEnqueue(App.Current.OnLinkLost);
            Activity("", "Phone disconnected");
            Dashboard();
        }
        if (state == EngineState.Connected && !_wasConnected)
        {
            (ConnectedSince, LastSeen) = (DateTimeOffset.Now, null);
            Activity("", "Phone connected");
            Dashboard();
        }
        _wasConnected = state == EngineState.Connected;
        EmergencyHint(state);
        if (state == EngineState.Waiting && UsbLink.Is(engine.Host, engine.Port)) _usb.Check(); // unplugged? then back to the network

        switch (state)
        {
            case EngineState.Connected:
                phone = UsbLink.Is(engine.Host, engine.Port) // the cable's address is no use once unplugged: keep the network one
                    ? phone with { Name = engine.Peer?.Name ?? phone.Name }
                    : phone with { Name = engine.Peer?.Name ?? phone.Name, Host = engine.Host, Port = engine.Port };
                Phones.Save(phone);
                lock (_gate) _phone = phone;
                Publish(new PhoneStatus(ConnectionState.Connected, phone.Name, _battery, _charging));
                break;
            case EngineState.Connecting:
                Publish(new PhoneStatus(ConnectionState.Connecting, phone.Name));
                break;
            case EngineState.Waiting:
                Publish(new PhoneStatus(ConnectionState.Disconnected, phone.Name));
                break;
            case EngineState.Unauthorized:
                Publish(new PhoneStatus(ConnectionState.Blocked, phone.Name,
                    Detail: "This phone no longer recognizes this PC. Remove it and pair again."));
                break;
            case EngineState.Incompatible:
                Publish(new PhoneStatus(ConnectionState.Blocked, phone.Name,
                    Detail: "Update Palwyn on your phone and PC to the same version."));
                break;
        }
    }

    async Task OnMessage(LinkEngine engine, JsonObject m)
    {
        if (!ReferenceEquals(engine, _engine) || _phone is not { } phone) return;
        var p = m["payload"]!.AsObject();
        var type = m["type"]!.GetValue<string>();
        switch (type)
        {
            case "REMOTE_POINTER" or "REMOTE_BUTTON" or "REMOTE_SCROLL" or "REMOTE_TEXT" or "REMOTE_KEY":
                PcRemote.Input(type, p);
                return;
            case "SCREEN_STATE":
                var state = p["state"]!.GetValue<string>();
                Log.Info($"Phone screen: {state}");
                _ui.TryEnqueue(() => ScreenWindow.OnState(state));
                return;
            case "PC_MEDIA" or "PC_VOLUME" or "PC_COMMAND" or "PC_LOCK":
                var error = await PcRemote.ActAsync(type, p);
                var replyTo = m["id"]!.GetValue<string>();
                try
                {
                    if (error is null) await engine.SendAsync("REMOTE_RESULT", new JsonObject { ["ok"] = true }, replyTo);
                    else await engine.SendAsync("ERROR", new JsonObject { ["code"] = error }, replyTo);
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
                return;
            case "DEVICE_INFO":
                PhoneDetails = $"{p["manufacturer"]} {p["model"]}, Android {p["androidVersion"]}";
                Device = DeviceInfo.From(p);
                Dashboard();
                break;
            case "BATTERY_CHANGED":
                _battery = p["level"]!.GetValue<int>();
                _charging = p["charging"]!.GetValue<bool>();
                break;
            case "DEVICE_STATUS":
                DeviceStatus = Core.DeviceStatus.From(p);
                Dashboard();
                return;
            case "MEDIA_STATE":
                NowPlaying = Core.NowPlaying.From(p);
                Dashboard();
                return;
            case "CAPABILITIES_CHANGED":
                Log.Info($"Phone capabilities: {string.Join(", ", engine.Capabilities)}");
                Dashboard();
                break;
            case "SMS_RECEIVED":
                var sms = SmsMessage.FromEvent(p);
                Log.Info($"SMS {(sms.Outgoing ? "sent" : "received")} {PhoneCall.Mask(sms.Address)}");
                _ui.TryEnqueue(() => App.Current.OnSms(sms));
                return;
            case "NOTIFICATION_POSTED":
                var n = PhoneNotification.From(p);
                _ui.TryEnqueue(() => App.Current.OnNotification(n));
                return;
            case "NOTIFICATION_REMOVED":
                var key = p["key"]!.GetValue<string>();
                _ui.TryEnqueue(() => App.Current.OnNotificationRemoved(key));
                return;
            case "CLIPBOARD_SET":
                var clip = p["text"]!.GetValue<string>();
                _ui.TryEnqueue(() => ClipboardSync.Received(clip));
                return;
            case "DROP_OFFER":
                var offer = DropOffer.From(p);
                Log.Info($"Phone shared {offer.Files.Count} files{(offer.Text is null ? "" : " and text")}");
                _ui.TryEnqueue(() => Receiving.Accept(offer));
                return;
            case "CALL_STATE":
                var call = PhoneCall.From(p);
                Log.Info($"Call {call.State} {(call.Incoming ? "in" : "out")} {PhoneCall.Mask(call.Number)}");
                _ui.TryEnqueue(() => App.Current.OnCall(call));
                return;
            case "UNPAIR":
                Log.Info("Phone removed this PC");
                Phones.Remove(phone.DeviceId);
                _ = Task.Run(async () => // not awaited: we're running on the engine's own loop
                {
                    await StopEngine();
                    UseNext();
                });
                return;
        }
        Publish(new PhoneStatus(ConnectionState.Connected, phone.Name, _battery, _charging));
    }

    void Publish(PhoneStatus status) => _ui.TryEnqueue(() => App.Current.SetStatus(status));
    void Dashboard() => _ui.TryEnqueue(() => DashboardChanged?.Invoke());
    void Activity(string glyph, string text) => _ui.TryEnqueue(() => RecentActivity.Add(glyph, text));

    public void Dispose()
    {
        _discovery.Dispose();
        _usb.Dispose();
        StopEngine().Wait(2000);
    }
}
