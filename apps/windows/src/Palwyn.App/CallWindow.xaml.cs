using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Palwyn.Core;
using Palwyn.Core.Link;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Palwyn.App;

/// <summary>
/// Incoming / ongoing call card in the bottom-right corner, above other windows, shown without taking focus
/// so it never interrupts typing. Call audio stays on the phone (Phase 0).
/// </summary>
public sealed partial class CallWindow : Window
{
    const double Gap = 12;
    static readonly UISettings Ui = new();

    readonly IntPtr _hwnd;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _autoHide = new() { Interval = TimeSpan.FromSeconds(4) };
    PhoneCall? _call;
    string? _hiddenCallId;
    string? _error;
    bool _busy, _declinedHere, _quitting;

    public CallWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.IsShownInSwitchers = false;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        // Alt+F4 hides the card instead of destroying it; App.Quit closes it for real.
        AppWindow.Closing += (_, e) =>
        {
            if (_quitting) return;
            e.Cancel = true;
            Hide_Click(this, new RoutedEventArgs());
        };

        // Fixed colours: the meaning (answer / hang up) must not change with the theme, and white on these
        // passes WCAG AA (green 5.4:1, red 5.6:1).
        Tint(AnswerButton, Color.FromArgb(255, 0x0F, 0x7B, 0x0F), Color.FromArgb(255, 0x0C, 0x6A, 0x0C), Color.FromArgb(255, 0x0A, 0x5A, 0x0A));
        foreach (var b in new[] { DeclineButton, EndButton })
            Tint(b, Color.FromArgb(255, 0xC4, 0x2B, 0x1C), Color.FromArgb(255, 0xB0, 0x26, 0x1A), Color.FromArgb(255, 0x9C, 0x22, 0x17));

        _tick.Tick += (_, _) => RenderStatus();
        _autoHide.Tick += (_, _) => Dismiss();
        AppSettings.ThemeChanged += ApplyTheme;
        ApplyTheme();
    }

    static void Tint(Button b, Color rest, Color hover, Color pressed)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        b.Resources["ButtonBackground"] = new SolidColorBrush(rest);
        b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        b.Resources["ButtonForeground"] = white;
        b.Resources["ButtonForegroundPointerOver"] = white;
        b.Resources["ButtonForegroundPressed"] = white;
        b.Resources["ButtonBorderBrush"] = new SolidColorBrush(rest);
        b.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(hover);
        b.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(pressed);
    }

    public void Show(PhoneCall call)
    {
        bool isNew = _call?.Id != call.Id;
        if (isNew)
        {
            // A call we never saw ringing or active has nothing left to act on.
            if (call.State == CallState.Ended) return;
            _declinedHere = false;
            _error = null;
        }
        _call = call;
        if (call.Id == _hiddenCallId) return;

        _autoHide.Stop();
        Render();
        if (call.State == CallState.Ended) _autoHide.Start();
        if (AppWindow.IsVisible) Place();
        else Appear();
    }

    public void Quit()
    {
        _quitting = true;
        Close();
    }

    /// <summary>Hides the card (call over, link lost). The next call shows it again.</summary>
    public void Dismiss()
    {
        _tick.Stop();
        _autoHide.Stop();
        if (AppWindow.IsVisible) AppWindow.Hide();
    }

    void Appear()
    {
        Root.Opacity = 0;
        Place();
        Win32.ShowNoActivate(_hwnd);
        Win32.MakeTopmost(_hwnd);
        // First show: layout hasn't run yet, so measure again once it has.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Place();
            if (Ui.AnimationsEnabled) Enter.Begin();
            else Root.Opacity = 1;
        });
    }

    void Place()
    {
        var work = DisplayArea.Primary.WorkArea;
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        Root.Measure(new Windows.Foundation.Size(Root.Width, double.PositiveInfinity));
        double height = Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 180;
        int frameW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        int frameH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        int w = (int)Math.Ceiling(Root.Width * scale) + frameW;
        int h = (int)Math.Ceiling(height * scale) + frameH;
        int gap = (int)(Gap * scale);
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - gap, work.Y + work.Height - h - gap, w, h));
    }

    void Render()
    {
        var c = _call!;
        Avatar.DisplayName = c.Name ?? ""; // initials for contacts, the generic person glyph otherwise
        ContactPhotos.SetNumber(Avatar, c.Number);
        TitleText.Text = c.Title;
        SubtitleText.Text = c.Name is not null ? c.Number ?? "" : "";
        SubtitleText.Visibility = SubtitleText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool ringing = c.State == CallState.Ringing && c.Incoming;
        bool active = c.State == CallState.Active;
        bool control = App.Current.Link.Capabilities.Contains("calls.control");
#if DEBUG
        control |= c.Id == "demo"; // --call=… demo card (DevArgs): show it as a phone with Calls allowed would
#endif
        DeclineButton.Visibility = AnswerButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
        EndButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        Actions.Visibility = ringing || active ? Visibility.Visible : Visibility.Collapsed;
        DeclineButton.IsEnabled = AnswerButton.IsEnabled = EndButton.IsEnabled = control && !_busy;

        Hint.Text = _error
            ?? (!control && (ringing || active) ? "To answer from this PC, open Palwyn on your phone and allow Calls."
            : active ? "Audio stays on your phone. Talk on it or on a headset connected to it."
            : "");
        Hint.Visibility = Hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderStatus();
        if (active) _tick.Start();
        else _tick.Stop();
    }

    void RenderStatus()
    {
        var c = _call!;
        StatusText.Text = c.State switch
        {
            CallState.Ringing => "Incoming call",
            CallState.Active => $"{(c.Incoming ? "On call" : "Outgoing call")} {Elapsed(c.Since)}",
            _ => _declinedHere ? "Declined" : "Call ended",
        };
    }

    static string Elapsed(DateTimeOffset since)
    {
        // The phone's clock stamps "since"; a few seconds of skew must not show a negative time.
        var t = DateTimeOffset.Now - since;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    async Task Command(string type)
    {
        if (_call is not { } call) return;
        _busy = true;
        _error = null;
        _declinedHere = type == "CALL_DECLINE";
        Render();
        try
        {
            await App.Current.Link.CallCommandAsync(type, call.Id);
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"{type} failed: {e.Message}");
            _declinedHere = false;
            _error = e switch
            {
                PhoneErrorException { Code: "FAILED" } => "The call already changed on your phone.",
                PhoneErrorException => "Your phone didn't allow that. Check that Palwyn can manage calls on the phone.",
                _ => "Couldn't reach your phone. Use the phone instead.",
            };
        }
        finally
        {
            _busy = false;
            if (_call?.Id == call.Id && AppWindow.IsVisible) Render();
        }
        if (AppWindow.IsVisible) Place();
    }

    async void Answer_Click(object sender, RoutedEventArgs e) => await Command("CALL_ANSWER");
    async void Decline_Click(object sender, RoutedEventArgs e) => await Command("CALL_DECLINE");
    async void End_Click(object sender, RoutedEventArgs e) => await Command("CALL_END");

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        _hiddenCallId = _call?.Id; // stays hidden for the rest of this call
        Dismiss();
    }

    void ApplyTheme() => Root.RequestedTheme = AppSettings.Theme;
}
