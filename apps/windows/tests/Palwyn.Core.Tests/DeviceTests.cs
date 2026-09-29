using System.Text.Json.Nodes;
using Palwyn.Core;

public class DeviceTests
{
    [Fact]
    public void Status_parses_optional_fields_and_describes_signal()
    {
        var s = DeviceStatus.From(JsonNode.Parse("""{"wifi":true,"wifiSignal":2,"bluetooth":false,"storageFree":5000000000}""")!.AsObject());
        Assert.Equal(new DeviceStatus(true, 2, false, 5_000_000_000), s);
        Assert.Equal("Good signal", s.WifiText);

        var bare = DeviceStatus.From(JsonNode.Parse("""{"wifi":false,"storageFree":1}""")!.AsObject());
        Assert.Null(bare.WifiSignal);
        Assert.Null(bare.Bluetooth);
        Assert.Equal("Not on Wi-Fi", bare.WifiText);
    }

    [Fact]
    public void Media_state_inactive_is_null()
    {
        Assert.Null(NowPlaying.From(JsonNode.Parse("""{"active":false}""")!.AsObject()));
        var now = NowPlaying.From(JsonNode.Parse("""{"active":true,"app":"Spotify","title":"Song","playing":true}""")!.AsObject());
        Assert.Equal(new NowPlaying("Spotify", "Song", null, true), now);
    }
}
