namespace Palwyn.Core;

/// <summary>Blocked = retrying can't help (phone no longer trusts this PC, or incompatible versions); see Detail.</summary>
public enum ConnectionState { NotPaired, Disconnected, Connecting, Connected, Blocked }

public sealed record PhoneStatus(
    ConnectionState State, string? PhoneName = null, int? BatteryPercent = null, bool Charging = false, string? Detail = null)
{
    public static readonly PhoneStatus NotPaired = new(ConnectionState.NotPaired);
}
