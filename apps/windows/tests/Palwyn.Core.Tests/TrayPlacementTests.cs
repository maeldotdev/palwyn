using Palwyn.Core;

public class TrayPlacementTests
{
    const int W = 360, H = 300, Gap = 12;
    static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Bottom_taskbar_sits_above_taskbar_centred_on_icon()
    {
        var work = Screen with { Height = 1032 };
        var r = TrayPlacement.Place(new PixelRect(1600, 1040, 24, 24), work, W, H, Gap);
        Assert.Equal(new PixelRect(1612 - W / 2, 1032 - H - Gap, W, H), r);
    }

    [Fact]
    public void Clamped_inside_right_edge()
    {
        var work = Screen with { Height = 1032 };
        var r = TrayPlacement.Place(new PixelRect(1890, 1040, 24, 24), work, W, H, Gap);
        Assert.Equal(1920 - W - Gap, r.X);
    }

    [Fact]
    public void Top_taskbar()
    {
        var work = new PixelRect(0, 48, 1920, 1032);
        var r = TrayPlacement.Place(new PixelRect(1800, 12, 24, 24), work, W, H, Gap);
        Assert.Equal(48 + Gap, r.Y);
        Assert.Equal(TaskbarEdge.Top, TrayPlacement.EdgeOf(new PixelRect(1800, 12, 24, 24), work));
    }

    [Fact]
    public void Left_taskbar_centres_vertically_on_icon()
    {
        var work = new PixelRect(48, 0, 1872, 1080);
        var r = TrayPlacement.Place(new PixelRect(12, 700, 24, 24), work, W, H, Gap);
        Assert.Equal(new PixelRect(48 + Gap, 712 - H / 2, W, H), r);
    }

    [Fact]
    public void Right_taskbar_clamped_to_bottom()
    {
        var work = new PixelRect(0, 0, 1872, 1080);
        var r = TrayPlacement.Place(new PixelRect(1884, 1040, 24, 24), work, W, H, Gap);
        Assert.Equal(new PixelRect(1872 - W - Gap, 1080 - H - Gap, W, H), r);
    }

    [Fact]
    public void Anchor_inside_work_area_defaults_to_bottom()
    {
        var work = Screen with { Height = 1032 };
        Assert.Equal(TaskbarEdge.Bottom, TrayPlacement.EdgeOf(new PixelRect(1500, 900, 1, 1), work));
    }

    [Fact]
    public void Secondary_monitor_with_negative_coordinates()
    {
        var work = new PixelRect(-1920, 0, 1920, 1032);
        var r = TrayPlacement.Place(new PixelRect(-40, 1040, 24, 24), work, W, H, Gap);
        Assert.Equal(-W - Gap, r.X);
        Assert.Equal(1032 - H - Gap, r.Y);
    }

    [Fact]
    public void Popup_taller_than_work_area_pins_to_top_instead_of_throwing()
    {
        var work = new PixelRect(0, 0, 800, 200);
        var r = TrayPlacement.Place(new PixelRect(700, 210, 24, 24), work, W, H, Gap);
        Assert.Equal(Gap, r.Y);
    }
}
