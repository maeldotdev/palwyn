using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Palwyn.App;

/// <summary>Keeps a window's title bar, Alt+Tab and taskbar icon in the same theme (dark or light) as its content.</summary>
static class WindowIcon
{
    public static void Follow(AppWindow window, FrameworkElement root)
    {
        void Apply() => window.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets",
            root.ActualTheme == ElementTheme.Light ? "AppIcon-light.ico" : "AppIcon.ico"));
        root.ActualThemeChanged += (_, _) => Apply();
        root.Loaded += (_, _) => Apply(); // ActualTheme is settled once the content is in the window
        Apply();
    }
}
