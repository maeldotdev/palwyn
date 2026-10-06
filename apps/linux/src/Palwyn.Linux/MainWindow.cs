using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Palwyn.Core;
using Palwyn.Core.Link;
using QRCoder;

namespace Palwyn.Linux;

/// <summary>The main window, built in code: Home, Add a phone and Settings here; Calls, Messages and Notifications in
/// MainWindow.Pages.cs. Wording follows the Windows app.</summary>
public sealed partial class MainWindow : Window
{
    static App App => App.Current;
    static LinkManager Link => App.Link;
    static readonly IBrush Tile = new SolidColorBrush(Color.Parse("#1A808080")); // reads on light and dark

    readonly ContentControl _content = new() { Padding = new Thickness(24) };
    readonly ScrollViewer _scroll = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    string _page = "home";

    public MainWindow()
    {
        Title = "Palwyn";
        Width = 880;
        Height = 620;
        MinWidth = 520;
        MinHeight = 420;
        FontFamily = App.Mono;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://palwyn-linux/Assets/icon.png")));

        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(12) };
        foreach (var (page, label) in new[] { ("home", "Home"), ("calls", "Calls"), ("messages", "Messages"), ("notifications", "Notifications"), ("photos", "Photos"), ("send", "Send to phone"), ("contacts", "Contacts"), ("add", "Add a phone"), ("settings", "Settings") })
        {
            var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => Navigate(page);
            nav.Children.Add(b);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*") };
        grid.Children.Add(nav);
        _scroll.Content = _content;
        Grid.SetColumn(_scroll, 1);
        grid.Children.Add(_scroll);
        Content = grid;

        App.Host.StatusChanged += () =>
        {
            RefreshIf("home");
            if (Link.IsConnected) _ = SendPendingAsync(); // files queued while the phone was away
        };
        App.Host.ActivityChanged += () => RefreshIf("home");
        Link.DashboardChanged += () => RefreshIf("home");
        Link.UsbDevicesChanged += () => { RefreshIf("home"); RefreshIf("settings"); };
        App.Host.NotificationsChanged += () => RefreshIf("notifications");
        App.Host.CallChanged += () => RefreshIf("calls");
        App.Host.SmsReceived += OnSmsReceived;
        Activated += (_, _) => UpdateViewing();
        Deactivated += (_, _) => UpdateViewing();
        Link.Discovery.Found += p => Dispatcher.UIThread.Post(() => OnFound(p));
        Link.Discovery.Lost += key => Dispatcher.UIThread.Post(() => _codePhones.Remove(_codePhones.FirstOrDefault(p => p.Key == key)!));
        _timer.Tick += (_, _) => Tick();
        HookTextBoxes();
    }

    /// <summary>Closing hides the window: the link (and notifications) keep running until Quit.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = !e.IsProgrammatic;
        if (e.Cancel) Hide();
        base.OnClosing(e);
    }

    /// <param name="item">A conversation id for "messages", a notification key for "notifications".</param>
    public void Navigate(string page, string? item = null)
    {
        if (_pairing && page != "add") _cts.Cancel(); // leaving Add a phone stops a pairing in progress
        _page = page;
        _loadError = null;
        if (page == "add") NewInvite();
        else _timer.Stop();
        if (page == "calls") _ = LoadCallsAsync();
        if (page == "messages") _ = item is null ? LoadThreadsAsync() : OpenThreadAsync(item);
        if (page == "notifications") _replyKey = item;
        if (page == "photos" && _photos.Count == 0) _ = LoadPhotosAsync();
        if (page == "contacts") { _editing = null; _ = LoadContactsAsync(); }
        if (page == "settings") Link.CheckUsb(); // the emergency access line reflects the cable now
        UpdateViewing();
        Refresh();
    }

    void RefreshIf(string page)
    {
        if (_page == page) Refresh();
    }

    /// <summary>Rebuilds the page. Text boxes are kept across rebuilds (fields), so typing survives, and focus too.</summary>
    void Refresh()
    {
        var focused = FocusManager?.GetFocusedElement() as TextBox;
        _content.Content = _page switch
        {
            "add" => AddPage(),
            "settings" => SettingsPage(),
            "calls" => CallsPage(),
            "messages" => MessagesPage(),
            "notifications" => NotificationsPage(),
            "photos" => PhotosPage(),
            "send" => SendPage(),
            "contacts" => ContactsPage(),
            _ => HomePage(),
        };
        if (focused is not null && TopLevel.GetTopLevel(focused) is not null) focused.Focus();
    }

    /// <summary>A kept control, taken out of the page it was in so the rebuilt page can hold it.</summary>
    static T Keep<T>(T control) where T : Control
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Decorator decorator) decorator.Child = null;
        else if (control.Parent is ContentControl content) content.Content = null;
        return control;
    }

    static TextBlock Text(string text, double size = 14, FontWeight weight = FontWeight.Normal, double opacity = 1) =>
        new() { Text = text, FontSize = size, FontWeight = weight, Opacity = opacity, TextWrapping = TextWrapping.Wrap };

    static TextBlock Heading(string text) => Text(text, 24, FontWeight.SemiBold);

    // ---- Home ----

    Control HomePage()
    {
        var s = App.Host.Status;
        var page = new StackPanel { Spacing = 16 };
        if (s.State == ConnectionState.NotPaired)
        {
            page.Children.Add(Heading("Welcome to Palwyn"));
            page.Children.Add(Text("Pair your Android phone to see its notifications and status on this PC. Install Palwyn on the phone first (version 0.15 or later).", opacity: 0.8));
            var add = new Button { Content = "Add a phone", Classes = { "accent" } };
            add.Click += (_, _) => Navigate("add");
            page.Children.Add(add);
        }
        else
        {
            page.Children.Add(Heading(s.PhoneName ?? "Your phone"));
            page.Children.Add(Text(App.StatusText(s), opacity: 0.8));
            if (s.Detail is { } detail) page.Children.Add(Text(detail));
            var tiles = new WrapPanel();
            foreach (var (title, value, detailText) in Tiles(s)) tiles.Children.Add(TileView(title, value, detailText));
            page.Children.Add(tiles);
        }
        var actions = new WrapPanel();
        void Action(string label, string target)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => App.Open(target, null);
            actions.Children.Add(b);
        }
        if (Link.IsConnected) Action("Phone screen", "screen");
        if (Link.UsbDevices.Any(d => d.IsReady))
        {
            Action("Emergency screen", "emergency"); // on the cable: works even when the phone's screen or Palwyn doesn't
            Action("Rescue files", "rescue");
        }
        if (actions.Children.Count > 0)
        {
            page.Children.Add(actions);
        }

        if (!App.HasTray)
            page.Children.Add(Text("There's no tray icon on this desktop. On GNOME, the AppIndicator extension adds one. Palwyn keeps running when you close this window; open it again from your apps.", 12, opacity: 0.7));
        if (App.Discovery.Problem is { } problem)
            page.Children.Add(Text($"{problem} Without it Palwyn can't find your phone on the network: install it, then restart Palwyn.", 12, opacity: 0.7));

        if (App.Host.Activity.Count > 0)
        {
            page.Children.Add(Text("Recent activity", 16, FontWeight.SemiBold));
            foreach (var (at, text) in App.Host.Activity) page.Children.Add(Text($"{at.ToLocalTime():HH:mm}  {text}", 13, opacity: 0.8));
        }
        return page;
    }

    static List<(string Title, string Value, string Detail)> Tiles(PhoneStatus s)
    {
        var tiles = new List<(string, string, string)>();
        bool connected = s.State == ConnectionState.Connected;
        tiles.Add(connected
            ? ("Connection", $"Connected since {Link.ConnectedSince?.ToLocalTime():t}",
                Link.Paired?.Address is null ? "Live over your Wi-Fi, encrypted" : "Live by address (VPN or other network), encrypted")
            : ("Connection", "Not connected",
                Link.LastSeen is { } seen ? $"Last connected {seen.ToLocalTime():t}, {seen.ToLocalTime():d MMM}" : "Waiting for your phone"));
        if (connected && s.BatteryPercent is int battery)
            tiles.Add(("Battery", $"{battery}%", s.Charging ? "Charging" : "On battery"));
        if (connected && Link.DeviceStatus is { } st)
        {
            tiles.Add(("Wi-Fi", st.WifiText, st.WifiSignal is int bars ? $"{bars} of 4 bars" : ""));
            tiles.Add(("Mobile", st.CellText,
                string.Join(" · ", new[] { st.Carrier, st.CellNetwork, st.CellSignal is int b ? $"{b} of 4 bars" : null }.Where(x => x is not null))));
            tiles.Add(("Bluetooth", st.Bluetooth switch { true => "On", false => "Off", null => "Not available" }, ""));
        }
        if (Link.Device is { } d)
        {
            long free = connected && Link.DeviceStatus is { } status ? status.StorageFree : d.StorageFree;
            if (connected && d.StorageTotal > 0)
                tiles.Add(("Storage", $"{Size(free)} free", $"{Size(d.StorageTotal - free)} of {Size(d.StorageTotal)} used"));
            tiles.Add(("Phone", $"{d.Manufacturer} {d.Model}", $"Android {d.AndroidVersion}"));
        }
        return tiles;
    }

    static Control TileView(string title, string value, string detail) => new Border
    {
        Background = Tile,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 0, 12, 12),
        Width = 240,
        Child = new StackPanel
        {
            Spacing = 4,
            Children = { Text(title, 12, opacity: 0.7), Text(value, 16, FontWeight.SemiBold), Text(detail, 12, opacity: 0.7) },
        },
    };

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB";

    // ---- Add a phone ----

    readonly ObservableCollection<DiscoveredPhone> _codePhones = [];
    PairingInvite? _invite;
    DateTimeOffset _expiresAt;
    Bitmap? _qr;
    string _pairStatus = "";
    string? _code;
    TaskCompletionSource<bool>? _confirm;
    bool _pairing;
    CancellationTokenSource _cts = new();

    void NewInvite()
    {
        _invite = Link.NewInvite();
        _expiresAt = DateTimeOffset.UtcNow + PcPairing.InviteLifetime;
        using var data = new QRCodeGenerator().CreateQrCode(_invite.ToUri(), QRCodeGenerator.ECCLevel.M);
        _qr = new Bitmap(new MemoryStream(new PngByteQRCode(data).GetGraphic(8)));
        _pairStatus = "Waiting for your phone…";
        _timer.Start();
        foreach (var p in Link.Discovery.Current) OnFound(p);
    }

    void Tick()
    {
        if (_page != "add") return;
        if (_expiresAt <= DateTimeOffset.UtcNow && !_pairing) NewInvite(); // one-time secrets expire; show a fresh one
        Refresh();
    }

    void OnFound(DiscoveredPhone phone)
    {
        _codePhones.Remove(_codePhones.FirstOrDefault(p => p.Key == phone.Key)!);
        if (phone.PairMode == "code") _codePhones.Add(phone);
        if (_page == "add" && phone.PairMode == Link.PairingTag && !_pairing && _invite is { } invite)
            _ = Pair(phone, ct => Link.PairWithQrAsync(phone, invite, _expiresAt, ct));
        if (_page == "add") Refresh();
    }

    async Task Pair(DiscoveredPhone phone, Func<CancellationToken, Task<PairedPhone>> pair)
    {
        _pairing = true;
        _cts = new CancellationTokenSource();
        _pairStatus = $"Pairing with {phone.Name ?? "your phone"}…";
        Refresh();
        try
        {
            await pair(_cts.Token);
            _pairing = false;
            Navigate("home");
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Pairing failed: {ex.GetType().Name}: {ex.Message}");
            _pairing = false;
            NewInvite(); // a failed attempt burns the one-time secret
            _pairStatus = ex is PairingException ? ex.Message : "Pairing didn't finish. Try again.";
        }
        catch (OperationCanceledException)
        {
            _pairing = false;
        }
        _code = null;
        Refresh();
    }

    /// <summary>Code pairing: both screens show the same 6 digits; the user confirms here.</summary>
    async Task<bool> ConfirmCode(string code, CancellationToken ct)
    {
        _code = code;
        _confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(Refresh);
        using (ct.Register(() => _confirm.TrySetCanceled()))
            try { return await _confirm.Task; }
            finally { _code = null; }
    }

    Control AddPage()
    {
        var page = new StackPanel { Spacing = 16 };
        page.Children.Add(Heading("Add a phone"));
        page.Children.Add(Text("On your phone, scan this code with the camera and open the Palwyn link. Phone and PC need the same Wi-Fi.", opacity: 0.8));
        if (_qr is not null)
            page.Children.Add(new Border
            {
                Background = Brushes.White, Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Image { Source = _qr, Width = 240, Height = 240 },
            });
        var left = _expiresAt - DateTimeOffset.UtcNow;
        page.Children.Add(Text(_pairStatus, 14, FontWeight.SemiBold));
        if (!_pairing && left > TimeSpan.Zero)
            page.Children.Add(Text($"This code works once and changes in {left:m\\:ss}.", 12, opacity: 0.7));

        if (_code is { } code)
        {
            page.Children.Add(Text("Check that your phone shows the same code:", 14));
            page.Children.Add(Text(code, 36, FontWeight.Bold));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var yes = new Button { Content = "It matches", Classes = { "accent" } };
            yes.Click += (_, _) => _confirm?.TrySetResult(true);
            var no = new Button { Content = "It doesn't" };
            no.Click += (_, _) => _confirm?.TrySetResult(false);
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            page.Children.Add(buttons);
        }
        else if (!_pairing)
        {
            page.Children.Add(Text("Can't scan? Choose \"Pair with a code\" on the phone, then pick it here:", 14));
            if (_codePhones.Count == 0) page.Children.Add(Text("No phone is waiting for a code.", 12, opacity: 0.7));
            foreach (var phone in _codePhones)
            {
                var b = new Button { Content = phone.Name ?? "Android phone" };
                b.Click += (_, _) => _ = Pair(phone, ct => Link.PairWithCodeAsync(phone, ConfirmCode, ct));
                page.Children.Add(b);
            }
        }
        return page;
    }

    // ---- Settings ----

    string? _removing;

    /// <summary>Whether the emergency screen would work now, and what's missing if not.</summary>
    static string EmergencyAccess()
    {
        if (!Palwyn.Core.Emergency.Adb.Available) return "Not available: adb isn't bundled with this build or installed.";
        var devices = Link.UsbDevices;
        if (devices.FirstOrDefault(d => d.IsReady) is { } ready) return $"Ready: {ready.Model?.Replace('_', ' ') ?? "your phone"} allows this PC.";
        if (devices.Any(d => d.State == "unauthorized")) return "On the phone, tick \"Always allow from this computer\" and tap Allow.";
        if (devices.Any(d => d.State == "no"))
            return "This PC isn't allowed to open the phone's USB connection. Install your distribution's Android udev rules (android-sdk-platform-tools-common on Ubuntu and Debian, android-tools on Fedora, android-udev on Arch), then plug the phone in again.";
        return "Not set up: plug in your phone with USB debugging on.";
    }
    readonly TextBox _commandName = new() { PlaceholderText = "Name, as the phone shows it", MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox _commandLine = new() { PlaceholderText = "Command, for example: systemctl suspend", MinWidth = 320, Margin = new Thickness(0, 0, 8, 0) };

    /// <summary>A settings switch that saves and tells the phone what changed.</summary>
    static ToggleSwitch Switch(string label, bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { Content = label, IsChecked = value };
        toggle.IsCheckedChanged += (_, _) =>
        {
            set(toggle.IsChecked == true);
            App.Settings.Save();
            App.Host.Remote.Update();
        };
        return toggle;
    }

    Control SettingsPage()
    {
        var page = new StackPanel { Spacing = 16 };
        page.Children.Add(Heading("Settings"));

        page.Children.Add(Text("Phones", 16, FontWeight.SemiBold));
        var active = Link.Paired?.DeviceId;
        foreach (var phone in Link.Phones.All())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Text($"{phone.Name}  {Fingerprint.Display(phone.DeviceId)}"));
            if (phone.DeviceId == active) row.Children.Add(Text("In use", 12, opacity: 0.7));
            else
            {
                var use = new Button { Content = "Use" };
                use.Click += async (_, _) => { await Link.SwitchToAsync(phone.DeviceId); Refresh(); };
                row.Children.Add(use);
            }
            var remove = new Button { Content = _removing == phone.DeviceId ? "Remove? Click again" : "Remove" };
            remove.Click += async (_, _) =>
            {
                if (_removing != phone.DeviceId)
                {
                    _removing = phone.DeviceId;
                    Refresh();
                    return;
                }
                _removing = null;
                await Link.RemoveAsync(phone.DeviceId);
                Refresh();
            };
            row.Children.Add(remove);
            page.Children.Add(row);
        }
        var add = new Button { Content = "Add a phone" };
        add.Click += (_, _) => Navigate("add");
        page.Children.Add(add);

        page.Children.Add(Text("This PC", 16, FontWeight.SemiBold));
        var notifications = new ToggleSwitch { Content = "Show phone notifications on this desktop", IsChecked = App.Settings.Notifications };
        notifications.IsCheckedChanged += (_, _) =>
        {
            App.Settings.Notifications = notifications.IsChecked == true;
            App.Settings.Save();
        };
        page.Children.Add(notifications);
        var history = new ToggleSwitch { Content = "Keep a history of the phone's notifications on this PC", IsChecked = App.Settings.NotificationHistory };
        history.IsCheckedChanged += (_, _) =>
        {
            App.Settings.NotificationHistory = history.IsChecked == true;
            App.Settings.Save();
        };
        page.Children.Add(history);
        var clear = new Button { Content = "Clear notification history" };
        clear.Click += (_, _) => App.Host.ClearHistory();
        page.Children.Add(clear);
        var autostart = new ToggleSwitch { Content = "Start Palwyn when you sign in", IsChecked = Settings.Autostart };
        autostart.IsCheckedChanged += (_, _) => Settings.Autostart = autostart.IsChecked == true;
        page.Children.Add(autostart);
        var clipboard = new ToggleSwitch { Content = "Send what I copy on this PC to my phone", IsChecked = App.Settings.ClipboardToPhone };
        clipboard.IsCheckedChanged += (_, _) =>
        {
            App.Settings.ClipboardToPhone = clipboard.IsChecked == true;
            App.Settings.Save();
            App.Host.Clipboard?.Update(Link.IsConnected);
            DispatcherTimer.RunOnce(() => RefreshIf("settings"), TimeSpan.FromSeconds(1)); // a watch that can't start says why
        };
        page.Children.Add(clipboard);
        if (App.Settings.ClipboardToPhone && App.Host.Clipboard?.AutoProblem is { } clipProblem) page.Children.Add(Text(clipProblem, 12, opacity: 0.7));

        var theme = new ComboBox { ItemsSource = new[] { "System theme", "Dark", "Light" } };
        theme.SelectedIndex = App.Settings.Theme switch { "dark" => 1, "light" => 2, _ => 0 };
        theme.SelectionChanged += (_, _) =>
        {
            App.Settings.Theme = theme.SelectedIndex switch { 1 => "dark", 2 => "light", _ => "system" };
            App.Settings.Save();
            App.ApplyTheme();
        };
        page.Children.Add(theme);

        page.Children.Add(Text("Phone as a remote", 16, FontWeight.SemiBold));
        page.Children.Add(Switch("Let the phone use this PC's mouse and keyboard", App.Settings.RemoteInput, on => App.Settings.RemoteInput = on));
        if (App.Settings.RemoteInput && XTest.Problem is { } inputProblem) page.Children.Add(Text(inputProblem, 12, opacity: 0.7));
        page.Children.Add(Switch("Let the phone control music, volume and locking", App.Settings.RemoteMedia, on => App.Settings.RemoteMedia = on));
        page.Children.Add(Switch("Keep this PC awake while the phone is connected", App.Settings.KeepPcAwake, on =>
        {
            App.Settings.KeepPcAwake = on;
            Session.KeepAwake(on && Link.IsConnected);
        }));
        page.Children.Add(Switch("Pause music and videos during calls", App.Settings.PauseMediaDuringCalls, on => App.Settings.PauseMediaDuringCalls = on));

        page.Children.Add(Text("Commands the phone can run here (as you, without a terminal):", 13, opacity: 0.8));
        foreach (var command in App.Settings.RemoteCommands)
        {
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                App.Settings.RemoteCommands.Remove(command);
                App.Settings.Save();
                App.Host.Remote.Update();
                Refresh();
            };
            page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Text($"{command.Name}: {command.Command}", 13), remove } });
        }
        var addCommand = new Button { Content = "Add" };
        addCommand.Click += (_, _) =>
        {
            if ((_commandName.Text ?? "").Trim() is not { Length: > 0 } name || (_commandLine.Text ?? "").Trim() is not { Length: > 0 } line) return;
            App.Settings.RemoteCommands.Add(new RemoteCommand(Guid.NewGuid().ToString("N")[..12], name.Length > 64 ? name[..64] : name, line));
            App.Settings.Save();
            App.Host.Remote.Update();
            (_commandName.Text, _commandLine.Text) = ("", "");
            Refresh();
        };
        page.Children.Add(new WrapPanel { Children = { Keep(_commandName), Keep(_commandLine), addCommand } });

        page.Children.Add(Text("Emergency access", 16, FontWeight.SemiBold));
        page.Children.Add(Text(EmergencyAccess(), 13, opacity: 0.8));
        page.Children.Add(Text("If the phone's screen ever breaks, plug it into this PC to see and control it and copy its files, with nothing to tap on the phone. It only works if you set it up now: Developer options > USB debugging on, then plug in and tick \"Always allow from this computer\".", 12, opacity: 0.7));

        page.Children.Add(Text("About", 16, FontWeight.SemiBold));
        page.Children.Add(Text($"Palwyn {App.Host.AppVersion} for Linux. This PC's id: {Fingerprint.Display(Fingerprint.DeviceId(Link.Fingerprint))}", 13, opacity: 0.8));
        var logs = new Button { Content = "Open log folder" };
        logs.Click += (_, _) =>
        {
            Directory.CreateDirectory(Paths.Logs);
            try { Process.Start(new ProcessStartInfo("xdg-open", [Paths.Logs]) { UseShellExecute = false }); }
            catch (System.ComponentModel.Win32Exception) { Log.Info("xdg-open isn't installed"); }
        };
        page.Children.Add(logs);
        var quit = new Button { Content = "Quit Palwyn" };
        quit.Click += (_, _) => App.Quit();
        page.Children.Add(quit);
        return page;
    }
}
