namespace Palwyn.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
}

public enum TaskbarEdge { Bottom, Top, Left, Right }

public static class TrayPlacement
{
    /// <summary>
    /// Positions a popup of the given physical size next to the tray anchor (icon rect, or a 1x1 rect at the
    /// cursor when the icon is hidden in the overflow), on the taskbar's side, kept inside the work area.
    /// </summary>
    public static PixelRect Place(PixelRect anchor, PixelRect workArea, int width, int height, int gap)
    {
        var edge = EdgeOf(anchor, workArea);
        int x = edge switch
        {
            TaskbarEdge.Left => workArea.X + gap,
            TaskbarEdge.Right => workArea.Right - width - gap,
            _ => anchor.CenterX - width / 2,
        };
        int y = edge switch
        {
            TaskbarEdge.Top => workArea.Y + gap,
            TaskbarEdge.Bottom => workArea.Bottom - height - gap,
            _ => anchor.CenterY - height / 2,
        };
        x = Math.Clamp(x, workArea.X + gap, Math.Max(workArea.X + gap, workArea.Right - width - gap));
        y = Math.Clamp(y, workArea.Y + gap, Math.Max(workArea.Y + gap, workArea.Bottom - height - gap));
        return new PixelRect(x, y, width, height);
    }

    // The taskbar is outside the work area, so the anchor's side of the work area is the taskbar's side.
    // An anchor inside the work area (overflow flyout, auto-hide taskbar) falls back to the Windows default.
    public static TaskbarEdge EdgeOf(PixelRect anchor, PixelRect workArea) =>
        anchor.CenterY >= workArea.Bottom ? TaskbarEdge.Bottom
        : anchor.CenterY < workArea.Y ? TaskbarEdge.Top
        : anchor.CenterX < workArea.X ? TaskbarEdge.Left
        : anchor.CenterX >= workArea.Right ? TaskbarEdge.Right
        : TaskbarEdge.Bottom;
}
