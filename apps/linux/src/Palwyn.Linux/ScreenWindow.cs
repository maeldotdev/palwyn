using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Palwyn.Core;
using Palwyn.Core.Emergency;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;

namespace Palwyn.Linux;

/// <summary>
/// The phone's screen, live, as on Windows. Sharing over the link starts only when the user allows it on the phone;
/// the emergency screen comes over adb with no prompt. With control allowed, a click taps, a drag swipes (sent on
/// release), the wheel scrolls, right-click is Back, middle-click is Home, and typing goes into the focused field.
/// </summary>
public sealed class ScreenWindow : Window
{
    static ScreenWindow? _current;

    readonly IScreenSource _src;
    readonly Image _picture = new() { Stretch = Stretch.Uniform };
    readonly Border _surface;
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 320 };
    readonly Button _retry = new() { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Center };
    readonly StackPanel _overlay;
    readonly TextBlock _hint = new() { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
    CancellationTokenSource? _stream;
    Bitmap? _shown;
    int _frameW, _frameH;
    bool _closed, _pressed;
    Point _down;
    long _downAt, _lastWheel;
    readonly StringBuilder _typed = new();

    /// <summary>Opens (or brings back) the window and asks the phone to share its screen.</summary>
    public static void Open() => OpenWith(() => new LinkScreenSource(), s => s is LinkScreenSource);

    /// <summary>The emergency screen of <paramref name="device"/>: through adb, no prompt on the phone.</summary>
    public static void OpenEmergency(AdbDevice device) => OpenWith(() => new EmergencyScreenSource(device), s => s is EmergencyScreenSource);

    /// <summary>One phone screen window at a time: opening the other kind replaces it.</summary>
    static void OpenWith(Func<IScreenSource> create, Func<IScreenSource, bool> isSame)
    {
        if (_current is { } open && !isSame(open._src)) open.Close();
        _current ??= new ScreenWindow(create());
        _current.Show();
        _current.Activate();
        if (_current._stream is null) _current.Ask();
    }

    /// <summary>SCREEN_STATE from the phone, on the UI thread.</summary>
    public static void OnState(string state)
    {
        if (_current?._src is LinkScreenSource) _current.State(state);
    }

    ScreenWindow(IScreenSource src)
    {
        _src = src;
        Title = src.Title;
        Width = 420;
        Height = 860;
        FontFamily = App.Mono;
        _surface = new Border { Background = Brushes.Transparent, Focusable = true, Child = _picture };
        _overlay = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { _status, _retry } };
        _retry.Click += (_, _) => Ask();
        foreach (var (label, key) in new[] { ("Back", "back"), ("Home", "home"), ("Recents", "recents") })
        {
            var b = new Button { Content = label };
            b.Click += (_, _) => Key(key);
            _keys.Children.Add(b);
        }
        var stop = new Button { Content = "Stop" };
        stop.Click += (_, _) => Close();
        _keys.Children.Add(stop);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(8) };
        grid.Children.Add(_surface);
        grid.Children.Add(_overlay);
        Grid.SetRow(_keys, 1);
        grid.Children.Add(_keys);
        Grid.SetRow(_hint, 2);
        _hint.Margin = new Thickness(0, 6, 0, 0);
        grid.Children.Add(_hint);
        Content = grid;

        _surface.PointerPressed += OnPressed;
        _surface.PointerReleased += OnReleased;
        _surface.PointerWheelChanged += OnWheel;
        _surface.KeyDown += OnKey;
        _surface.TextInput += OnText;
        src.Changed += RenderControl;
        src.Lost += OnLost;
        Closed += (_, _) =>
        {
            _closed = true;
            _stream?.Cancel(); // the phone sees the stream close and stops sharing
            src.Changed -= RenderControl;
            src.Lost -= OnLost;
            _ = src.DisposeAsync().AsTask(); // the emergency helper is stopped and removed from the phone
            if (ReferenceEquals(_current, this)) _current = null;
        };
        RenderControl();
    }

    void OnLost(string message) => Dispatcher.UIThread.Post(() =>
    {
        _stream?.Cancel();
        ShowStatus(message, retry: true);
    });

    void RenderControl() => Dispatcher.UIThread.Post(() =>
    {
        _keys.IsEnabled = _src.CanControl;
        _hint.Text = _src.CanControl
            ? "Click to tap, drag to swipe, scroll to scroll. Right-click is Back. Type to fill the selected field."
            : "View only. To control it from here, turn on \"Let my PC control this phone\" in Palwyn on the phone.";
    });

    void ShowStatus(string text, bool retry = false)
    {
        if (_closed) return;
        _status.Text = text;
        _retry.IsVisible = retry;
        _overlay.IsVisible = true;
        _picture.Opacity = 0.25;
    }

    void HideStatus()
    {
        _overlay.IsVisible = false;
        _picture.Opacity = 1;
    }

    async void Ask()
    {
        ShowStatus(_src.Starting);
        try
        {
            if (await _src.BeginAsync()) _ = StreamAsync();
        }
        catch (ScreenSourceException e) { ShowStatus(e.Message, retry: true); }
    }

    void State(string state)
    {
        switch (state)
        {
            case "started": _ = StreamAsync(); break;
            case "declined": ShowStatus("Screen sharing wasn't allowed on your phone.", retry: true); break;
            case "stopped" when _stream is null: ShowStatus("Screen sharing ended.", retry: true); break;
        }
    }

    async Task StreamAsync()
    {
        _stream?.Cancel();
        var cts = _stream = new CancellationTokenSource();
        ShowStatus("Connecting…");
        try
        {
            bool first = true;
            await foreach (var jpeg in _src.FramesAsync(cts.Token))
            {
                var frame = await Task.Run(() => new Bitmap(new MemoryStream(jpeg))); // decoded off the UI thread
                ShowFrame(frame);
                if (!first) continue;
                first = false;
                HideStatus();
                _surface.Focus();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException
                                  or ProtocolException or ObjectDisposedException or TimeoutException or ArgumentException)
        {
            Log.Info($"Phone screen stream ended: {e.GetType().Name}");
        }
        if (!ReferenceEquals(_stream, cts)) return; // replaced by a newer stream
        _stream = null;
        if (!cts.IsCancellationRequested) ShowStatus(_src.Ended, retry: true);
    }

    void ShowFrame(Bitmap frame)
    {
        var (w, h) = (frame.PixelSize.Width, frame.PixelSize.Height);
        bool turned = _frameW > 0 && (w > h) != (_frameW > _frameH);
        (_frameW, _frameH) = (w, h);
        _picture.Source = frame;
        _shown?.Dispose();
        _shown = frame;
        if (turned) (Width, Height) = (Height, Width); // the phone rotated
    }

    // ---- Control ----

    /// <summary>A point on the picture as 0–10000 across the phone's screen; null outside it unless clamped.</summary>
    (int X, int Y)? ToPhone(Point p, bool clamp = false)
    {
        var size = _picture.Bounds.Size;
        if (_frameW == 0 || size.Width == 0) return null;
        double s = Math.Min(size.Width / _frameW, size.Height / _frameH);
        double w = _frameW * s, h = _frameH * s;
        double x = (p.X - (size.Width - w) / 2) / w, y = (p.Y - (size.Height - h) / 2) / h;
        if (!clamp && (x < 0 || x > 1 || y < 0 || y > 1)) return null;
        return ((int)(Math.Clamp(x, 0, 1) * 10_000), (int)(Math.Clamp(y, 0, 1) * 10_000));
    }

    void Touch((int X, int Y) from, (int X, int Y) to, long ms) =>
        _ = _src.SendAsync("SCREEN_TOUCH", new JsonObject
        {
            ["x1"] = from.X, ["y1"] = from.Y, ["x2"] = to.X, ["y2"] = to.Y, ["ms"] = (int)Math.Clamp(ms, 1, 10_000),
        });

    void Key(string key)
    {
        FlushTyping();
        _ = _src.SendAsync("SCREEN_KEY", new JsonObject { ["key"] = key });
    }

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _surface.Focus();
        if (!_src.CanControl) return;
        var point = e.GetCurrentPoint(_picture);
        if (point.Properties.IsRightButtonPressed) Key("back");
        else if (point.Properties.IsMiddleButtonPressed) Key("home");
        else if (ToPhone(point.Position) is not null)
        {
            (_down, _downAt, _pressed) = (point.Position, Environment.TickCount64, true);
            e.Pointer.Capture(_surface);
        }
        e.Handled = true;
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        var up = e.GetPosition(_picture);
        if (ToPhone(_down) is not { } from || ToPhone(up, clamp: true) is not { } to) return;
        long held = Environment.TickCount64 - _downAt;
        bool moved = Math.Abs(up.X - _down.X) + Math.Abs(up.Y - _down.Y) > 8;
        // Not moved: a tap, or a long press when held. Moved: a swipe as long as the drag took.
        if (moved) Touch(from, to, Math.Clamp(held, 100, 3000));
        else Touch(from, from, held < 400 ? 60 : Math.Min(held, 3000));
        e.Handled = true;
    }

    void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_src.CanControl || ToPhone(e.GetPosition(_picture)) is not { } at) return;
        e.Handled = true;
        if (Environment.TickCount64 - _lastWheel < 250) return; // one swipe at a time; a new one would cancel the last
        _lastWheel = Environment.TickCount64;
        // Wheel up shows what's above: the finger moves down the screen.
        int dy = e.Delta.Y > 0 ? 2500 : -2500;
        Touch(at, (at.X, Math.Clamp(at.Y + dy, 0, 10_000)), 250);
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (!_src.CanControl) return;
        string? key = e.Key switch { Avalonia.Input.Key.Back => "backspace", Avalonia.Input.Key.Enter => "enter", Avalonia.Input.Key.Escape => "back", _ => null };
        if (key is null) return;
        Key(key);
        e.Handled = true;
    }

    void OnText(object? sender, TextInputEventArgs e)
    {
        if (!_src.CanControl || string.IsNullOrEmpty(e.Text) || e.Text[0] < ' ') return; // control keys come through KeyDown
        _typed.Append(e.Text);
        e.Handled = true;
        if (_typed.Length == e.Text.Length)
            DispatcherTimer.RunOnce(FlushTyping, TimeSpan.FromMilliseconds(80)); // a burst of keys goes as one message
    }

    void FlushTyping()
    {
        if (_typed.Length == 0) return;
        var text = _typed.ToString();
        _typed.Clear();
        _ = _src.SendAsync("SCREEN_TEXT", new JsonObject { ["text"] = text });
    }
}

/// <summary>Screen sharing over the paired link (docs/protocol.md, SCREEN_*).</summary>
sealed class LinkScreenSource : IScreenSource
{
    static LinkManager Link => App.Current.Link;

    public string Title => "Phone screen";
    public bool CanControl => Link.Capabilities.Contains("screen.control");
    public string Starting => "On your phone, tap the Palwyn notification, then Start. To skip this next time, turn on \"Keep screen sharing ready\" in Palwyn on the phone.";
    public string Ended => "Screen sharing ended.";
    public event Action? Changed;
    public event Action<string>? Lost;

    public LinkScreenSource()
    {
        Link.DashboardChanged += OnDashboard; // capabilities: control comes and goes with sharing
        App.Current.Host.StatusChanged += OnStatus;
    }

    void OnDashboard() => Changed?.Invoke();

    void OnStatus()
    {
        if (!Link.IsConnected) Lost?.Invoke("Your phone disconnected.");
    }

    public async Task<bool> BeginAsync()
    {
        try { await Link.RequestScreenAsync(); }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            throw new ScreenSourceException(e is PhoneErrorException ? "Update Palwyn on your phone to see its screen here." : "Your phone isn't connected.");
        }
        return false;
    }

    public async IAsyncEnumerable<byte[]> FramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var link = await Link.OpenScreenAsync(ct);
        while (await link.ReadBlobAsync(ct) is { } jpeg)
        {
            if (jpeg.Length > 0 && jpeg[0] == (byte)'{') yield break; // ERROR instead of frames: not shared with this PC
            yield return jpeg;
        }
    }

    public Task SendAsync(string type, JsonObject payload) => Link.ScreenControlAsync(type, payload);

    public ValueTask DisposeAsync()
    {
        Link.DashboardChanged -= OnDashboard;
        App.Current.Host.StatusChanged -= OnStatus;
        return ValueTask.CompletedTask;
    }
}
