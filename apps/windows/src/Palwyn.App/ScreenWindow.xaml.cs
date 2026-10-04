using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace Palwyn.App;

/// <summary>
/// The phone's screen, live (docs/research-screen-mirroring.md). Sharing starts only when the user allows it on the
/// phone. With control allowed there, a click taps, a drag swipes (in a straight line, sent on release), the wheel
/// scrolls, right-click is Back, and typing goes into the phone's focused text field.
/// </summary>
public sealed partial class ScreenWindow : Window
{
    static ScreenWindow? _current;

    readonly IScreenSource _src;
    readonly SoftwareBitmapSource _source = new();
    SoftwareBitmap? _shown;
    CancellationTokenSource? _stream;
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
        _current.Activate();
        Win32.SetForegroundWindow(WindowNative.GetWindowHandle(_current)); // opened from the tray: come to the front
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
        InitializeComponent();
        Title = src.Title;
        Picture.Source = _source;
        GlassBackdrop.Follow(this, () => new MicaBackdrop());
        WindowIcon.Follow(AppWindow, Root);
        double scale = Win32.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(420 * scale), (int)(860 * scale));
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - size.Width) / 2, work.Y + Math.Max(0, (work.Height - size.Height) / 2),
            size.Width, Math.Min(size.Height, work.Height)));

        src.Changed += RenderControl;
        src.Lost += OnLost;
        AppSettings.ThemeChanged += ApplyTheme;
        Closed += (_, _) =>
        {
            _closed = true;
            _stream?.Cancel(); // the phone sees the stream close and stops sharing
            src.Changed -= RenderControl;
            src.Lost -= OnLost;
            AppSettings.ThemeChanged -= ApplyTheme;
            _ = src.DisposeAsync().AsTask(); // the emergency helper is stopped and removed from the phone
            if (ReferenceEquals(_current, this)) _current = null;
        };
        ApplyTheme();
        RenderControl();
    }

    void ApplyTheme() => Root.RequestedTheme = AppSettings.Theme;

    void OnLost(string message)
    {
        _stream?.Cancel();
        Show(message, retry: true);
    }

    bool CanControl => _src.CanControl;

    void RenderControl()
    {
        bool can = CanControl;
        BackButton.IsEnabled = HomeButton.IsEnabled = RecentsButton.IsEnabled = can;
        ControlHint.Text = can
            ? "Click to tap, drag to swipe, scroll to scroll. Right-click is Back. Type to fill the selected field."
            : "View only. To control it from here, turn on \"Let my PC control this phone\" in Palwyn on the phone.";
    }

    void Show(string text, bool busy = false, bool retry = false)
    {
        if (_closed) return;
        StatusText.Text = text;
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Retry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        Picture.Opacity = 0.25;
    }

    void HideStatus()
    {
        Status.Visibility = Visibility.Collapsed;
        Picture.Opacity = 1;
    }

    async void Ask()
    {
        Show(_src.Starting, busy: true);
        try
        {
            if (await _src.BeginAsync()) _ = StreamAsync();
        }
        catch (ScreenSourceException e) { Show(e.Message, retry: true); }
    }

    void Retry_Click(object sender, RoutedEventArgs e) => Ask();

    void State(string state)
    {
        switch (state)
        {
            case "started":
                _ = StreamAsync();
                break;
            case "declined":
                Show("Screen sharing wasn't allowed on your phone.", retry: true);
                break;
            case "stopped" when _stream is null:
                Show("Screen sharing ended.", retry: true);
                break;
        }
    }

    async Task StreamAsync()
    {
        _stream?.Cancel();
        var cts = _stream = new CancellationTokenSource();
        Show("Connecting…", busy: true);
        try
        {
            bool first = true;
            await foreach (var jpeg in _src.FramesAsync(cts.Token))
            {
                await ShowFrameAsync(jpeg);
                if (!first) continue;
                first = false;
                HideStatus();
                Surface.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException
                                  or ProtocolException or ObjectDisposedException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            Log.Info($"Phone screen stream ended: {e.GetType().Name}");
        }
        if (!ReferenceEquals(_stream, cts)) return; // replaced by a newer stream
        _stream = null;
        if (!cts.IsCancellationRequested) Show(_src.Ended, retry: true);
    }

    async Task ShowFrameAsync(byte[] jpeg)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream);
        var frame = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        bool turned = _frameW > 0 && (frame.PixelWidth > frame.PixelHeight) != (_frameW > _frameH);
        (_frameW, _frameH) = (frame.PixelWidth, frame.PixelHeight);
        await _source.SetBitmapAsync(frame);
        _shown?.Dispose();
        _shown = frame;
        if (turned) AppWindow.Resize(new SizeInt32(AppWindow.Size.Height, AppWindow.Size.Width)); // the phone rotated
    }

    // ---- Control ----

    /// <summary>A point on the picture as 0–10000 across the phone's screen; null outside it unless clamped.</summary>
    (int X, int Y)? ToPhone(Point p, bool clamp = false)
    {
        if (_frameW == 0 || Picture.ActualWidth == 0) return null;
        double s = Math.Min(Picture.ActualWidth / _frameW, Picture.ActualHeight / _frameH);
        double w = _frameW * s, h = _frameH * s;
        double x = (p.X - (Picture.ActualWidth - w) / 2) / w, y = (p.Y - (Picture.ActualHeight - h) / 2) / h;
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

    void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Surface.Focus(FocusState.Pointer);
        if (!CanControl) return;
        var point = e.GetCurrentPoint(Picture);
        if (point.Properties.IsRightButtonPressed) Key("back");
        else if (point.Properties.IsMiddleButtonPressed) Key("home");
        else if (ToPhone(point.Position) is not null)
        {
            (_down, _downAt, _pressed) = (point.Position, Environment.TickCount64, true);
            Surface.CapturePointer(e.Pointer);
        }
        e.Handled = true;
    }

    void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        Surface.ReleasePointerCapture(e.Pointer);
        var up = e.GetCurrentPoint(Picture).Position;
        if (ToPhone(_down) is not { } from || ToPhone(up, clamp: true) is not { } to) return;
        long held = Environment.TickCount64 - _downAt;
        bool moved = Math.Abs(up.X - _down.X) + Math.Abs(up.Y - _down.Y) > 8;
        // Not moved: a tap, or a long press when held. Moved: a swipe as long as the drag took.
        if (moved) Touch(from, to, Math.Clamp(held, 100, 3000));
        else Touch(from, from, held < 400 ? 60 : Math.Min(held, 3000));
        e.Handled = true;
    }

    void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Picture);
        if (!CanControl || ToPhone(point.Position) is not { } at) return;
        e.Handled = true;
        if (Environment.TickCount64 - _lastWheel < 250) return; // one swipe at a time; a new one would cancel the last
        _lastWheel = Environment.TickCount64;
        // Wheel up shows what's above: the finger moves down the screen.
        int dy = point.Properties.MouseWheelDelta > 0 ? 2500 : -2500;
        Touch(at, (at.X, Math.Clamp(at.Y + dy, 0, 10_000)), 250);
    }

    void Surface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CanControl) return;
        string? key = e.Key switch
        {
            VirtualKey.Back => "backspace",
            VirtualKey.Enter => "enter",
            VirtualKey.Escape => "back",
            _ => null,
        };
        if (key is null) return;
        Key(key);
        e.Handled = true;
    }

    void Surface_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (!CanControl || e.Character < ' ') return; // control characters come through KeyDown
        _typed.Append(e.Character);
        e.Handled = true;
        if (_typed.Length == 1) DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80); // a burst of keys goes as one message, so the field isn't rewritten per letter
            FlushTyping();
        });
    }

    void FlushTyping()
    {
        if (_typed.Length == 0) return;
        var text = _typed.ToString();
        _typed.Clear();
        _ = _src.SendAsync("SCREEN_TEXT", new JsonObject { ["text"] = text });
    }

    void Back_Click(object sender, RoutedEventArgs e) => Key("back");
    void Home_Click(object sender, RoutedEventArgs e) => Key("home");
    void Recents_Click(object sender, RoutedEventArgs e) => Key("recents");
    void Stop_Click(object sender, RoutedEventArgs e) => Close();
}
