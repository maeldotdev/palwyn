using Palwyn.Core;

namespace Palwyn.Core.Tests;

public class AdbTests
{
    [Fact]
    public void ParsesDevicesWithModelAndStates()
    {
        var d = AdbOutput.ParseDevices("List of devices attached\r\nQ8G6 device usb:1-1 product:RMX3286T2 model:RMX3286 device:RE54B4L1 transport_id:4\r\nAB12 unauthorized usb:1-2 transport_id:5\r\n192.168.1.5:5555 device product:x model:Y transport_id:6\r\n");
        Assert.Equal(3, d.Count);
        Assert.Equal(("Q8G6", true, "RMX3286", true), (d[0].Serial, d[0].IsReady, d[0].Model, d[0].IsUsb));
        Assert.False(d[1].IsReady);
        Assert.False(d[2].IsUsb);
    }

    [Fact]
    public void SkipsDaemonLinesAndWirelessSerials()
    {
        var d = AdbOutput.ParseDevices("* daemon not running; starting now at tcp:5037\n* daemon started successfully\nList of devices attached\nadb-Q8G6-KT2lcY._adb-tls-connect._tcp device\nemulator-5554 device\n\n");
        Assert.Equal(2, d.Count);
        Assert.All(d, x => Assert.False(x.IsUsb));
    }

    [Fact]
    public void ParsesStatPathsWithSpacesAndQuotes()
    {
        var f = AdbOutput.ParseStat("1234 1790000000 /sdcard/DCIM/IMG 2026'01.jpg\n5 1790000001 /sdcard/Download/ñ.pdf\n");
        Assert.Equal(new RemoteFile("/sdcard/DCIM/IMG 2026'01.jpg", 1234, 1790000000), f[0]);
        Assert.Equal("/sdcard/Download/ñ.pdf", f[1].Path);
    }

    [Fact]
    public void QuotesPathsForTheShell() =>
        Assert.Equal("'/sdcard/IMG 2026'\\''01.jpg'", AdbOutput.ShellQuote("/sdcard/IMG 2026'01.jpg"));

    [Fact]
    public void MapsPhonePathsToSafeLocalPaths()
    {
        Assert.Equal(Path.Combine(@"C:\R", "DCIM", "Camera", "a_b.jpg"), AdbOutput.LocalPath("/sdcard/DCIM/Camera/a:b.jpg", @"C:\R"));
        Assert.Equal(Path.Combine(@"C:\R", "Download", "x.txt"), AdbOutput.LocalPath("/sdcard/Download/../../x.txt", @"C:\R"));
        Assert.Equal(Path.Combine(@"C:\R", "Documents", "_CON.txt"), AdbOutput.LocalPath("/sdcard/Documents/CON.txt", @"C:\R"));
    }

    [Fact]
    public void SkipsSameSizeAndTimeWithinTwoSeconds()
    {
        var r = new RemoteFile("/sdcard/a.jpg", 10, 1790000000);
        Assert.False(AdbOutput.ShouldCopy(r, 10, DateTimeOffset.FromUnixTimeSeconds(1790000002)));
        Assert.True(AdbOutput.ShouldCopy(r, 11, DateTimeOffset.FromUnixTimeSeconds(1790000000)));
        Assert.True(AdbOutput.ShouldCopy(r, null, null));
    }
}
