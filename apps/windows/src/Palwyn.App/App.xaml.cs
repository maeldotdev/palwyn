using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Palwyn.Core;
using Package = Windows.ApplicationModel.Package;

namespace Palwyn.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public PhoneStatus Status { get; private set; } = PhoneStatus.NotPaired;
    public event Action? StatusChanged;
    public event Action<PhoneCall>? CallUpdated;
    public event Action<SmsMessage>? SmsReceived;

    static DispatcherQueue? _ui;
    public Palwyn.App.Link.LinkManager Link { get; private set; } = null!;
    TrayIcon? _tray;
    TrayFlyout? _flyout;
    MainWindow? _main;
    CallWindow? _call;

    public App()
    {
        InitializeComponent();
        _ui = DispatcherQueue.GetForCurrentThread();
        UnhandledException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        var v = Package.Current.Id.Version;
        Log.Prune();
        // Full-size copies made by "Open" are only for viewing; don't keep phone photos around.
        try { if (Directory.Exists(PhotosPage.OpenFolder)) Directory.Delete(PhotosPage.OpenFolder, recursive: true); }
        catch (IOException) { } // one is still open in a viewer: it goes next time
        Log.Info($"Palwyn {v.Major}.{v.Minor}.{v.Build} starting");

        Link = new Palwyn.App.Link.LinkManager(_ui!); // before the flyout, which listens to it
        _flyout = new TrayFlyout();
        _tray = new TrayIcon(_flyout.Hwnd);
        _tray.Invoked += anchor => _flyout.Toggle(anchor);
        _tray.ClipboardChanged += ClipboardSync.OnChanged;
        _tray.Update(Status);
        Link.UsbDevicesChanged += QuickActions.Refresh; // the emergency actions show while a phone is on the cable
        Link.Start();
        AppSettings.RemoteChanged += PcRemote.Update;
        Toasts.Init(_ui!);

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var kind = activation.Kind;
        bool fromStartup = kind == ExtendedActivationKind.StartupTask;
#if DEBUG
        // Packaged apps get launch arguments through the activation data, not args.Arguments.
        var launchArgs = (activation.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments;
        if (DevArgs.Apply(launchArgs, this, _flyout)) return;
#endif
        Log.Info($"Activated by {kind}");
        if (kind == ExtendedActivationKind.AppNotification)
            Toasts.Handle((Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs)activation.Data);
        else if (kind == ExtendedActivationKind.ShareTarget) Share(activation);
        else if (!fromStartup) ShowMain();
    }

    /// <summary>A second launch (Start menu, "Share > Palwyn") handed to this running copy.</summary>
    public static void OnRedirectedActivation(AppActivationArguments e) =>
        _ui?.TryEnqueue(() =>
        {
            if (e.Kind == ExtendedActivationKind.ShareTarget) Current.Share(e);
            else Current.ShowMain();
        });

    void Share(AppActivationArguments e)
    {
        var share = ((Windows.ApplicationModel.Activation.ShareTargetActivatedEventArgs)e.Data).ShareOperation;
        _ = ShowQuickDrop().AddShared(share);
    }

    QuickDropWindow? _drop;

    /// <summary>The Quick Drop window (PC to phone), created on demand.</summary>
    public QuickDropWindow ShowQuickDrop()
    {
        _flyout?.HideFlyout();
        if (_drop is null)
        {
            _drop = new QuickDropWindow();
            _drop.Closed += (_, _) => _drop = null;
        }
        _drop.AppWindow.Show();
        _drop.Activate();
        return _drop;
    }

    public void SetStatus(PhoneStatus status)
    {
        Status = status;
        KeepPcAwake.Update(status.State == ConnectionState.Connected);
        PcNotificationForwarder.Update();
        PcRemote.Update();
        _ = History; // a phone removed or replaced: its history goes
        _tray?.Update(status);
        StatusChanged?.Invoke();
    }

    /// <summary>What's in the phone's notification shade, by key (muted apps included; views filter).</summary>
    public Dictionary<string, PhoneNotification> PhoneNotifications { get; } = [];
    public event Action<PhoneNotification>? NotificationPosted;
    public event Action<string>? NotificationRemoved;
    public event Action? NotificationsCleared;
    /// <summary>The Notifications page is showing in the focused main window: new ones need no Windows notification.</summary>
    public bool ViewingNotifications { get; set; }

    readonly Dictionary<string, long> _quietUntil = [];

    /// <summary>We just acted on this notification (reply, action): the app's update in response isn't news.</summary>
    public void QuietFor(string key) => _quietUntil[key] = Environment.TickCount64 + 10_000;

    public void OnNotification(PhoneNotification n)
    {
        PhoneNotifications.TryGetValue(n.Key, out var before);
        PhoneNotifications[n.Key] = n;
        AppIcons.Ensure(n.Package);
        if (AppSettings.NotificationToasts && !AppSettings.IsMuted(n.Package) && n.AlertsOver(before) && !(_quietUntil.TryGetValue(n.Key, out long until) && Environment.TickCount64 < until)
            && !(MainWindowActive && ViewingNotifications))
            Toasts.Notification(n);
        if (!AppSettings.IsMuted(n.Package) && History is { } history && history.Add(n))
        {
            // ponytail: rewrites the whole file (≤ 500 entries) per new notification; batch the writes if that shows up.
            try { history.Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info($"History not saved: {e.GetType().Name}"); }
            HistoryChanged?.Invoke();
        }
        NotificationPosted?.Invoke(n);
    }

    static string HistoryFolder => Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "history");
    NotificationHistory? _history;
    public event Action? HistoryChanged;

    static string HistoryPath(string deviceId) => Path.Combine(HistoryFolder, $"notifications-{deviceId}.json");

    /// <summary>
    /// The notification history of the phone in use; null with no phone or with the setting off. Getting it
    /// deletes the history of any phone no longer paired, and all of it when the setting is off.
    /// </summary>
    public NotificationHistory? History
    {
        get
        {
            var id = Link.Paired?.DeviceId;
            var path = id is null || !AppSettings.NotificationHistory ? null : HistoryPath(id);
            if (_history?.Path == path) return _history;
            _history = path is null ? null : new NotificationHistory(path);
            _history?.Load();
            PruneHistory();
            HistoryChanged?.Invoke();
            return _history;
        }
    }

    /// <summary>Deletes the history of every phone no longer paired, and all of it when the setting is off.</summary>
    public static void PruneHistory()
    {
        var keep = AppSettings.NotificationHistory ? Current.Link.Phones.All().Select(p => HistoryPath(p.DeviceId)).ToHashSet() : [];
        try
        {
            if (Directory.Exists(HistoryFolder))
                foreach (var f in Directory.GetFiles(HistoryFolder).Where(f => !keep.Contains(f))) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // next time
    }

    public void ClearHistory()
    {
        History?.Clear();
        HistoryChanged?.Invoke();
    }

    public void OnNotificationRemoved(string key)
    {
        if (!PhoneNotifications.Remove(key)) return;
        _quietUntil.Remove(key);
        Toasts.RemoveNotification(key);
        NotificationRemoved?.Invoke(key);
    }

    /// <summary>The link dropped: live state from the phone is unknown until it reconnects and resends it.</summary>
    public void OnLinkLost()
    {
        _call?.Dismiss();
        PhoneNotifications.Clear();
        NotificationsCleared?.Invoke();
    }

    string? _lastCallLogged;

    /// <summary>Phone call events (null = the link dropped, call state unknown). Runs on the UI thread.</summary>
    public void OnCall(PhoneCall? call)
    {
        CallMediaPause.Update(call);
        if (call is null)
        {
            _call?.Dismiss();
            return;
        }
        if (call.State == CallState.Ringing && call.Id != _lastCallLogged)
        {
            _lastCallLogged = call.Id;
            RecentActivity.Add("", $"Call from {call.Name ?? call.Number ?? "an unknown number"}");
        }
        (_call ??= new CallWindow()).Show(call);
        CallUpdated?.Invoke(call);
    }

    /// <summary>The conversation open in the main window, while that window has focus; its messages don't need a notification.</summary>
    public string? ViewingThreadId { get; set; }
    public bool MainWindowActive { get; private set; }

    /// <summary>A new message in the phone's SMS store, received or sent. Runs on the UI thread.</summary>
    public void OnSms(SmsMessage m)
    {
        if (!m.Outgoing && !(MainWindowActive && ViewingThreadId == m.ThreadId)) Toasts.Sms(m);
        RecentActivity.Add("", m.Outgoing ? $"Texted {m.Name ?? m.Address}" : $"Text from {m.Name ?? m.Address}");
        SmsReceived?.Invoke(m);
    }

    public void ShowMain(string? page = null)
    {
        _flyout?.HideFlyout();
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) =>
            {
                _main = null;
                MainWindowActive = false;
            };
            _main.Activated += (_, e) => MainWindowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        }
        if (page is not null) _main.Navigate(page);
        _main.AppWindow.Show();
        _main.Activate();
    }

    public void Quit()
    {
        Log.Info("Quit requested");
        Emergency.EmergencySession.CloseAll();
        Link?.Dispose();
        _tray?.Dispose();
        _main?.Close();
        _flyout?.Close();
        _call?.Quit();
        _drop?.Close();
        Toasts.Shutdown();
        Exit();
    }
}
