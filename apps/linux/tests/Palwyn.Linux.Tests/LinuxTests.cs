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
