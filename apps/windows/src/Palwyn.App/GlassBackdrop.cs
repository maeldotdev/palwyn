using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Palwyn.App;

/// <summary>
/// Acrylic whose tint thins as <see cref="AppSettings.Transparency"/> rises, so more of the desktop shows through.
/// Windows' acrylic always blurs; only its tint and luminosity layers are adjustable.
/// </summary>
sealed partial class GlassBackdrop : SystemBackdrop
{
    DesktopAcrylicController? _controller;
    // Our own configuration rather than the window's default: the default follows window focus, and acrylic
    // turns solid whenever the window is inactive. The user chose see-through, so it stays see-through.
    readonly SystemBackdropConfiguration _config = new() { IsInputActive = true };
    int _level;

    public GlassBackdrop(int level) => _level = level;

    public void SetLevel(int level)
    {
        _level = level;
        Apply();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _controller = new DesktopAcrylicController();
        FollowHighContrast(target, root);
        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(target);
        // The default configuration follows Windows' app mode, not our Theme setting: tint by what the content shows.
        _content = root.Content as FrameworkElement;
        if (_content is not null) _content.ActualThemeChanged += OnTheme;
        Apply();
    }

    FrameworkElement? _content;

    // Raised on theme, focus and high-contrast changes. The base implementation throws ArgumentException for a
    // custom backdrop (it crashed the app on switching theme), so it is not called. Focus is ignored on purpose.
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        if (_controller is null) return;
        FollowHighContrast(target, root);
        Apply();
    }

    // High contrast still turns the glass solid, as Windows requires for readability.
    void FollowHighContrast(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        try { _config.IsHighContrast = GetDefaultSystemBackdropConfiguration(target, root).IsHighContrast; }
        catch (ArgumentException) { } // a target this backdrop was just swapped away from
    }

    void OnTheme(FrameworkElement sender, object args) => Apply();

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        if (_content is not null) _content.ActualThemeChanged -= OnTheme;
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }

    void Apply()
    {
        if (_controller is null) return;
        bool dark = _content?.ActualTheme == ElementTheme.Dark;
        _config.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        // Windows 11's own acrylic base colours.
        _controller.TintColor = dark ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
        _controller.FallbackColor = dark ? Windows.UI.Color.FromArgb(255, 0x2C, 0x2C, 0x2C) : Windows.UI.Color.FromArgb(255, 0xF9, 0xF9, 0xF9);
        // 1% = nearly the normal acrylic. 100% = the clearest glass that still keeps text readable on a bright
        // wallpaper; below these floors white text on dark glass washes out.
        float solid = 1 - _level / 100f;
        _controller.TintOpacity = 0.2f + 0.6f * solid;
        _controller.LuminosityOpacity = 0.45f + 0.5f * solid;
    }

    /// <summary>
    /// Keeps a window's backdrop in step with the setting: <paramref name="normal"/> at 0, glass above it.
    /// </summary>
    public static void Follow(Window window, Func<SystemBackdrop> normal)
    {
        void Update()
        {
            int level = AppSettings.Transparency;
            if (level == 0) window.SystemBackdrop = normal();
            else if (window.SystemBackdrop is GlassBackdrop glass) glass.SetLevel(level);
            else window.SystemBackdrop = new GlassBackdrop(level);
        }
        Update();
        AppSettings.TransparencyChanged += Update;
        window.Closed += (_, _) => AppSettings.TransparencyChanged -= Update;
    }
}
