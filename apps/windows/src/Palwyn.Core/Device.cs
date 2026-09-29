using System.Text.Json.Nodes;

namespace Palwyn.Core;

/// <summary>DEVICE_INFO: fixed facts, sent once per connection.</summary>
public sealed record DeviceInfo(string Manufacturer, string Model, string AndroidVersion, long StorageTotal, long StorageFree)
{
    public static DeviceInfo From(JsonObject p) => new(
        p["manufacturer"]!.GetValue<string>(), p["model"]!.GetValue<string>(), p["androidVersion"]!.GetValue<string>(),
        Node.Long(p["storageTotal"]), Node.Long(p["storageFree"]));
}

/// <summary>DEVICE_STATUS: what changes, sent when it does.</summary>
/// <param name="CellSignal">Mobile network bars 0..4; null without a usable SIM.</param>
public sealed record DeviceStatus(bool Wifi, int? WifiSignal, bool? Bluetooth, long StorageFree,
    int? CellSignal = null, string? CellNetwork = null, string? Carrier = null)
{
    public static DeviceStatus From(JsonObject p) => new(
        p["wifi"]!.GetValue<bool>(), p["wifiSignal"] is { } s ? (int)Node.Long(s) : null,
        p["bluetooth"]?.GetValue<bool>(), Node.Long(p["storageFree"]),
        p["cellSignal"] is { } c ? (int)Node.Long(c) : null, p["cellNetwork"]?.GetValue<string>(), p["carrier"]?.GetValue<string>());

    public string CellText => CellSignal switch
    {
        null => "No SIM or no service",
        0 => "No signal",
        >= 3 => "Strong signal",
        2 => "Good signal",
        _ => "Weak signal",
    };

    public string WifiText => !Wifi ? "Not on Wi-Fi" : WifiSignal switch
    {
        null => "Connected",
        >= 3 => "Strong signal",
        2 => "Good signal",
        _ => "Weak signal",
    };
}

/// <summary>MEDIA_STATE: what the phone is playing; null = nothing.</summary>
public sealed record NowPlaying(string App, string? Title, string? Artist, bool Playing)
{
    public static NowPlaying? From(JsonObject p) => p["active"]!.GetValue<bool>()
        ? new(p["app"]?.GetValue<string>() ?? "", p["title"]?.GetValue<string>(), p["artist"]?.GetValue<string>(),
              p["playing"]?.GetValue<bool>() ?? false)
        : null;
}
