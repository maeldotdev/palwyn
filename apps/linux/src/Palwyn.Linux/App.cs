using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.Linux;

/// <summary>The Linux desktop app: tray icon, main window and the shared link hub.</summary>
public sealed class App : Application
{
    public static readonly FontFamily Mono = new("avares://palwyn-linux/Assets/JetBrainsMono.ttf#JetBrains Mono");
    public static new App Current => (App)Application.Current!;
    /// <summary>Started by the desktop session (autostart): stay in the tray.</summary>
    public static bool StartHidden { get; set; }
    /// <summary>Files from "palwyn-linux send" when this copy is the one starting: sent once the window is up.</summary>
    public static IReadOnlyList<string> SendAtStart { get; set; } = [];

    public Settings Settings { get; } = Settings.Load();
    public LinuxHost Host { get; private set; } = null!;
    public LinkManager Link { get; private set; } = null!;
    public AvahiDiscovery Discovery { get; } = new();
    /// <summary>False on desktops without a tray host (stock GNOME): the window is the only way in.</summary>
    public bool HasTray { get; private set; }

    static Socket? _instance;
    MainWindow? _main;
    CallWindow? _call;
    TrayIcon? _tray;
    NativeMenuItem? _trayStatus;

    public override void Initialize()
    {
        var accent = Color.Parse("#12A67C"); // the Windows app's brand green
        Styles.Add(new FluentTheme
        {
            Palettes =
            {
                [ThemeVariant.Light] = new ColorPaletteResources { Accent = accent },
                [ThemeVariant.Dark] = new ColorPaletteResources { Accent = accent },
            },
        });
        ApplyTheme();
    }

    public void ApplyTheme() => RequestedThemeVariant = Settings.Theme switch
    {
        "dark" => ThemeVariant.Dark,
        "light" => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the window keeps the link running
        Log.Prune();
        Host = new LinuxHost(Settings, action => Dispatcher.UIThread.Post(action));
        Link = new LinkManager(Host, IdentityFile.Load(Paths.Data), Paths.Data, Discovery, new NoUsb());
        Host.Link = Link;
        // The clipboard belongs to a window; the main window exists (hidden) from here on.
        Host.Clipboard = new ClipboardSync(Settings, () => (_main ??= new MainWindow()).Clipboard);
        Host.StatusChanged += () => Host.Clipboard.Update(Link.IsConnected);
        Host.Open = ShowMain;
        Host.CallChanged += () => (_call ??= new CallWindow()).Show(Host.Call);
        Log.Info($"Palwyn {Host.AppVersion} starting");

        HasTray = !OperatingSystem.IsLinux() || TrayHostRunning();
        if (HasTray) CreateTray();
        else Log.Info("No tray host on this desktop: window only");
        Host.StatusChanged += UpdateTray;
        Link.Start();
        if (Discovery.Problem is { } problem) Log.Info($"Discovery off: {problem}");
        if (SendAtStart.Count > 0) Send(SendAtStart);
        else if (!StartHidden || !HasTray) ShowMain();
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMain(string page = "home", string? item = null)
    {
        _main ??= new MainWindow();
        _main.Navigate(page, item);
        _main.Show();
        _main.Activate();
    }

    /// <summary>Opens Send to phone with these files; they go as soon as the phone is connected.</summary>
    public void Send(IReadOnlyList<string> paths)
    {
        ShowMain("send");
        _main!.QueueFiles(paths);
    }

    public void Quit()
    {
        Log.Info("Quitting");
        Link.Dispose();
        Session.KeepAwake(false);
        _instance?.Dispose();
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown();
    }

    // ---- Tray ----

    void CreateTray()
    {
        _trayStatus = new NativeMenuItem(StatusText(Host.Status)) { IsEnabled = false };
        var open = new NativeMenuItem("Open Palwyn");
        open.Click += (_, _) => ShowMain();
        var add = new NativeMenuItem("Add a phone");
        add.Click += (_, _) => ShowMain("add");
        var quit = new NativeMenuItem("Quit Palwyn");
        quit.Click += (_, _) => Quit();
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://palwyn-linux/Assets/icon.png"))),
            ToolTipText = "Palwyn",
            Menu = new NativeMenu { Items = { _trayStatus, new NativeMenuItemSeparator(), open, add, new NativeMenuItemSeparator(), quit } },
        };
        // No anchored flyout as on Windows: Wayland doesn't tell apps where the tray icon is.
        _tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    void UpdateTray()
    {
        if (_tray is null || _trayStatus is null) return;
        var text = StatusText(Host.Status);
        _trayStatus.Header = text;
        _tray.ToolTipText = $"Palwyn: {text}";
    }

    public static string StatusText(PhoneStatus s) => s.State switch
    {
        ConnectionState.NotPaired => "No phone paired",
        ConnectionState.Connected => $"{s.PhoneName}: connected" + (s.BatteryPercent is int b ? $", {b}%{(s.Charging ? " charging" : "")}" : ""),
        ConnectionState.Connecting => $"Connecting to {s.PhoneName}…",
        ConnectionState.Disconnected => $"{s.PhoneName}: not connected",
        _ => $"{s.PhoneName}: needs attention",
    };

    /// <summary>A StatusNotifierItem host owns org.kde.StatusNotifierWatcher (KDE, XFCE, Cinnamon, GNOME with the
    /// AppIndicator extension). Without one a tray icon would silently show nowhere.</summary>
    static bool TrayHostRunning()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gdbus", ["call", "--session", "--dest", "org.freedesktop.DBus",
                "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Contains("true");
        }
        catch (Win32Exception)
        {
            return false; // can't tell: show the window rather than risk an invisible app
        }
    }

    // ---- One copy at a time ----

    /// <summary>
    /// True when Palwyn is already running: it's asked to show its window and this copy should exit. Otherwise this
    /// copy listens for later launches. A Unix socket in the user's runtime folder, which only the user can open.
    /// </summary>
    /// <param name="message">"show", or "send" and a file path per line.</param>
    public static bool HandOffToRunningCopy(string message = "show")
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? r : Path.GetTempPath();
        var endpoint = new UnixDomainSocketEndPoint(Path.Combine(dir, "palwyn.sock"));
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(endpoint);
            client.Send(System.Text.Encoding.UTF8.GetBytes(message));
            client.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (SocketException) { }
        try
        {
            File.Delete(Path.Combine(dir, "palwyn.sock")); // left behind by a copy that crashed
            _instance = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _instance.Bind(endpoint);
            _instance.Listen();
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException)
        {
            Log.Info($"Single instance off: {e.Message}"); // a second launch then opens a second copy; never fatal
            _instance?.Dispose();
            _instance = null;
            return false;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await _instance.AcceptAsync();
                    using var reader = new StreamReader(new NetworkStream(c), System.Text.Encoding.UTF8);
                    var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (Application.Current is not App app) return;
                        if (lines is ["send", .. var paths]) app.Send(paths);
                        else app.ShowMain();
                    });
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException) { } // quitting
        });
        return false;
    }
}
