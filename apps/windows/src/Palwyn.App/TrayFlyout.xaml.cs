using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Palwyn.Core;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Palwyn.App;

/// <summary>Compact popup anchored to the tray icon. Hides when it loses focus, like Windows' own flyouts.</summary>
public sealed partial class TrayFlyout : Window
{
    const double Gap = 12;
    static readonly UISettings Ui = new();

    public IntPtr Hwnd { get; }
    long _hiddenAt;
    PixelRect _anchor;

    public TrayFlyout()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);

        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.IsShownInSwitchers = false;
        GlassBackdrop.Follow(this, () => new DesktopAcrylicBackdrop());

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HideFlyout();
        };
        App.Current.StatusChanged += Render;
        // Media and actions come and go; refit once PhoneControls has re-rendered.
        App.Current.Link.DashboardChanged += Refit;
        QuickActions.Changed += Refit;
        AppSettings.ThemeChanged += ApplyTheme;
        AppSettings.TransparencyChanged += RenderTransparency;
        ApplyTheme();
        RenderTransparency();
        Render();
    }

    public void Toggle(PixelRect anchor)
    {
        if (AppWindow.IsVisible) { HideFlyout(); return; }
        // The click that deactivated us also arrives here; don't reopen immediately.
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        ShowAt(anchor);
    }

    public void ShowAt(PixelRect anchor)
    {
        _anchor = anchor;
        Root.Opacity = 0;
        Render();
        Place();
        AppWindow.Show();
        Win32.MakeTopmost(Hwnd);
        Activate();
        Win32.SetForegroundWindow(Hwnd);
        // First show: layout hasn't run yet, so re-measure once it has.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Place();
            Animate();
            // Keyboard-ready (Esc, Tab) without drawing a focus rectangle on mouse open.
            (FocusManager.FindFirstFocusableElement(Root) as Control)?.Focus(FocusState.Pointer);
        });
    }

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible) return;
        AppWindow.Hide();
        _hiddenAt = Environment.TickCount64;
        TransparencyRow.Visibility = Visibility.Collapsed; // opens compact next time
    }

    void Place()
    {
        var (work, scale) = Win32.MonitorFor(_anchor);
        Root.Measure(new Windows.Foundation.Size(Root.Width, double.PositiveInfinity));
        double height = Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 240;
        // Outer size includes invisible resize borders; size the window so the client area fits the content.
        int frameW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        int frameH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var r = TrayPlacement.Place(_anchor, work,
            (int)Math.Ceiling(Root.Width * scale) + frameW, (int)Math.Ceiling(height * scale) + frameH, (int)(Gap * scale));
        AppWindow.MoveAndResize(new RectInt32(r.X, r.Y, r.Width, r.Height));
    }

    void Animate()
    {
        if (!Ui.AnimationsEnabled)
        {
            Root.Opacity = 1;
            return;
        }
        var (work, _) = Win32.MonitorFor(_anchor);
        EnterSlide.From = TrayPlacement.EdgeOf(_anchor, work) == TaskbarEdge.Top ? -12 : 12;
        Enter.Begin();
    }

    void Render()
    {
        var s = App.Current.Status;
        Header.Update(s);
        Message.Text = s.State switch
        {
            ConnectionState.NotPaired => "Pair your Android phone to bring its calls, messages and notifications here.",
            ConnectionState.Disconnected => "Your phone isn't reachable. Check that it's on the same Wi-Fi.",
            ConnectionState.Connecting => "Looking for your phone on this network.",
            ConnectionState.Blocked => s.Detail ?? "",
            _ => "",
        };
        Message.Visibility = Message.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The popup is for quick actions; the big button appears only when there's something to fix in the app.
        OpenButton.Content = s.State == ConnectionState.NotPaired ? "Add a phone" : "Open Palwyn";
        OpenButton.Visibility = s.State is ConnectionState.NotPaired or ConnectionState.Blocked ? Visibility.Visible : Visibility.Collapsed;
        ClipboardSwitch.Visibility = s.State == ConnectionState.NotPaired ? Visibility.Collapsed : Visibility.Visible;
        ClipboardSwitch.IsOn = AppSettings.ClipboardToPhone; // may have changed in Settings
        if (AppWindow.IsVisible) Place();
    }

    void Refit() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        if (AppWindow.IsVisible) Place();
    });

    void ApplyTheme()
    {
        Root.RequestedTheme = AppSettings.Theme;
        // Segoe Fluent Icons: QuietHours (moon) E708, Brightness (sun) E706, System E770.
        var (glyph, name, next) = AppSettings.Theme switch
        {
            ElementTheme.Dark => ("", "Dark", "Light"),
            ElementTheme.Light => ("", "Light", "Windows setting"),
            _ => ("", "Windows setting", "Dark"),
        };
        ThemeGlyph.Glyph = glyph;
        string tip = $"Theme: {name}. Click for {next}.";
        ToolTipService.SetToolTip(ThemeButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeButton, tip);
    }

    void Theme_Click(object sender, RoutedEventArgs e) => AppSettings.Theme = AppSettings.Theme switch
    {
        ElementTheme.Dark => ElementTheme.Light,
        ElementTheme.Light => ElementTheme.Default,
        _ => ElementTheme.Dark,
    };

    void Transparency_Click(object sender, RoutedEventArgs e)
    {
        TransparencyRow.Visibility = TransparencyRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Refit();
    }

    void RenderTransparency()
    {
        TransparencySlider.Value = AppSettings.Transparency;
        TransparencyValue.Text = AppSettings.Transparency == 0 ? "Off" : $"{AppSettings.Transparency}%";
    }

    void Transparency_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        AppSettings.Transparency = (int)e.NewValue; // raises TransparencyChanged, which re-renders

    void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) HideFlyout();
    }

    void Open_Click(object sender, RoutedEventArgs e) =>
        App.Current.ShowMain(App.Current.Status.State == ConnectionState.NotPaired ? "add" : null);
    void Clipboard_Toggled(object sender, RoutedEventArgs e) => AppSettings.ClipboardToPhone = ClipboardSwitch.IsOn;
    void Settings_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain("settings");
    void Quit_Click(object sender, RoutedEventArgs e) => App.Current.Quit();
}
