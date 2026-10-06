using Palwyn.Core;
using Palwyn.Linux;

public class AvahiTests
{
    const string Txt = "\"n=Pixel\\0327\" \"pair=qr:ab12cd34\" \"id=7fcde326d3a90dead570ea1892db1173\"";

    [Fact]
    public void Reads_a_resolved_phone()
    {
        var p = AvahiDiscovery.Parse($"=;wlan0;IPv4;Pixel\\0327;_palwyn._tcp;local;pixel.local;192.168.1.23;47800;{Txt}")!;
        Assert.Equal(("7fcde326d3a90dead570ea1892db1173", "qr:ab12cd34", "Pixel 7"), (p.DeviceId, p.PairMode, p.Name));
        Assert.Equal(("192.168.1.23", 47800), (p.Host, p.Port));
    }

    [Fact]
    public void Decodes_escaped_bytes_quotes_and_semicolons()
    {
        // "Mañana's \"phone\"; ok": ñ is two UTF-8 bytes (195 177), quotes are backslashed.
        var p = AvahiDiscovery.Parse("=;wlan0;IPv4;x;_palwyn._tcp;local;h.local;10.0.0.5;47800;" +
            "\"id=00\" \"n=Ma\\195\\177ana's \\\"phone\\\"; ok\"")!;
        Assert.Equal("Mañana's \"phone\"; ok", p.Name);
    }

    [Theory]
    [InlineData("+;wlan0;IPv4;Pixel;_palwyn._tcp;local")] // found, not resolved yet
    [InlineData("-;wlan0;IPv4;Pixel;_palwyn._tcp;local")]
    [InlineData("=;wlan0;IPv6;Pixel;_palwyn._tcp;local;pixel.local;fe80::1c2a:3bff:fe4d:5e6f;47800;\"id=00\"")]
    [InlineData("=;wlan0;IPv4;Pixel;_palwyn._tcp;local;pixel.local;192.168.1.23;0;\"id=00\"")]
    [InlineData("=;wlan0;IPv4;Pixel;_palwyn._tcp;local;pixel.local;192.168.1.23;47800;\"n=no id\"")]
    [InlineData("Failed to create client object: Daemon not running")]
    public void Ignores_everything_else(string line) => Assert.Null(AvahiDiscovery.Parse(line));
}

public class NotifyTextTests
{
    [Theory]
    [InlineData("Tom & Jerry", "Tom &amp; Jerry")]
    [InlineData("<b>bold</b>", "&lt;b&gt;bold&lt;/b&gt;")]
    [InlineData("<a href=\"https://x\">tap</a>", "&lt;a href=\"https://x\"&gt;tap&lt;/a&gt;")]
    [InlineData("1 < 2 &amp; 3", "1 &lt; 2 &amp;amp; 3")]
    public void Phone_text_stays_text(string text, string escaped) => Assert.Equal(escaped, NotifyText.Escape(text));
}

public class NotifyActionTests
{
    static readonly PhoneNotification WhatsApp = new("k", "com.whatsapp", "WhatsApp", "Mika", "See you at 7", DateTimeOffset.UnixEpoch,
        true, [new NotificationAction(0, "Reply", true), new NotificationAction(1, "Mark as read", false)], false);

    [Fact]
    public void Buttons_are_the_phones_actions_then_open()
    {
        Assert.Equal([("a0", "Reply…"), ("a1", "Mark as read"), ("default", "Open")], DesktopNotifier.ButtonsFor(WhatsApp));
    }

    [Theory]
    [InlineData("a1", 1)]
    [InlineData("a0", 0)]
    [InlineData("default", null)]
    [InlineData("a7", null)]
    [InlineData("answer", null)]
    [InlineData("a", null)]
    public void A_button_maps_back_to_its_action(string id, int? index) =>
        Assert.Equal(index, DesktopNotifier.Chosen(WhatsApp, id)?.Index);
}

public class ClipboardEchoTests
{
    [Fact]
    public void A_copy_goes_once_and_never_bounces_back()
    {
        var echo = new ClipboardEcho();
        echo.Received("already there at start");
        Assert.False(echo.ShouldSend("already there at start"));
        Assert.True(echo.ShouldSend("new copy"));
        Assert.False(echo.ShouldSend("new copy")); // the same copy seen again
        echo.Received("from the phone");
        Assert.False(echo.ShouldSend("from the phone")); // set by us, not copied by the user
        Assert.True(echo.ShouldSend("new copy")); // copied again after something else: send
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_to_send(string? text) => Assert.False(new ClipboardEcho().ShouldSend(text));

    [Fact]
    public void Too_long_to_send() => Assert.False(new ClipboardEcho().ShouldSend(new string('x', 50_001)));
}

public class MprisParseTests
{
    [Fact]
    public void Finds_the_players_in_ListNames()
    {
        const string names = "(['org.freedesktop.DBus', ':1.7', 'org.mpris.MediaPlayer2.spotify', 'org.gnome.Shell', 'org.mpris.MediaPlayer2.firefox.instance_1_23'],)";
        Assert.Equal(["org.mpris.MediaPlayer2.spotify", "org.mpris.MediaPlayer2.firefox.instance_1_23"], Mpris.BusNames(names));
    }

    [Theory]
    [InlineData("(<'Playing'>,)", "Playing")]
    [InlineData("(<'Mozilla Firefox'>,)", "Mozilla Firefox")]
    [InlineData("(<\"Don't Stop\">,)", "Don't Stop")]
    [InlineData("(<'It\\'s'>,)", "It's")]
    [InlineData("(<uint32 5>,)", null)]
    public void Reads_a_string_property(string answer, string? expected) => Assert.Equal(expected, Mpris.FirstString(answer));

    [Fact]
    public void Reads_title_and_first_artist()
    {
        const string metadata = "(<{'mpris:trackid': <objectpath '/org/mpris/MediaPlayer2/Track/1'>, 'xesam:title': <\"Don't Look Back\">, " +
                                "'xesam:artist': <['Mika', 'Someone']>, 'mpris:length': <int64 200000000>}>,)";
        Assert.Equal(("Don't Look Back", "Mika"), Mpris.TitleAndArtist(metadata));
        Assert.Equal((null, null), Mpris.TitleAndArtist("(<@a{sv} {}>,)"));
    }
}

public class VolumeParseTests
{
    [Theory]
    [InlineData("Volume: front-left: 26214 /  40% / -23.87 dB,   front-right: 26214 /  40% / -23.87 dB\n        balance 0.00", "Mute: no", 40, false)]
    [InlineData("Volume: mono: 98304 / 150% / 10.57 dB", "Mute: yes", 100, true)]
    public void Reads_pactl(string volume, string mute, int level, bool muted) =>
        Assert.Equal((level, muted), Volume.Parse(volume, mute));

    [Fact]
    public void Nothing_without_a_percentage() => Assert.Null(Volume.Parse("Connection failure: Connection refused", ""));
}

public class KeysymTests
{
    [Theory]
    [InlineData("a", 0x61u)]
    [InlineData("ñ", 0xf1u)]
    [InlineData("€", 0x010020acu)]
    [InlineData("😀", 0x0101f600u)]
    [InlineData("\n", 0xff0du)]
    public void Characters_become_X_keysyms(string text, uint keysym) =>
        Assert.Equal(keysym, XTest.KeysymFor(text.EnumerateRunes().First()));
}

public class IdentityFileTests
{
    [Fact]
    public void Creates_a_private_key_once_and_reloads_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "palwyn-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = IdentityFile.Load(dir);
            using var second = IdentityFile.Load(dir);
            Assert.True(second.HasPrivateKey);
            Assert.Equal(Fingerprint.Of(first), Fingerprint.Of(second));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "identity.key")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
