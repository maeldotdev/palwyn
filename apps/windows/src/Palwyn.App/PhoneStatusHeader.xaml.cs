using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Palwyn.Core;

namespace Palwyn.App;

public sealed partial class PhoneStatusHeader : UserControl
{
    public PhoneStatusHeader()
    {
        InitializeComponent();
        Loaded += (_, _) => VisualStateManager.GoToState(this, IsLarge ? "Large" : "Compact", false);
    }

    public bool IsLarge { get; set; }

    /// <summary>False where the page shows the battery itself (the Home device tile).</summary>
    public bool ShowBattery { get; set; } = true;

    // Segoe Fluent Icons: Battery0..9 = E850..E859, Battery10 = E83F; charging shows BatteryCharging9 (E83E).
    public static string Glyph(int level, bool charging) =>
        charging ? "" : level >= 95 ? "" : ((char)(0xE850 + level / 10)).ToString();

    public void Update(PhoneStatus s)
    {
        Title.Text = s.State == ConnectionState.NotPaired ? "No phone paired" : s.PhoneName ?? "Your phone";
        Subtitle.Text = s.State switch
        {
            ConnectionState.Connected => App.Current.Link.OverUsb ? "Connected over USB"
                : App.Current.Link.Paired?.Address is null ? "Connected over Wi-Fi" : "Connected by address",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Disconnected => "Not connected",
            ConnectionState.Blocked => "Needs attention",
            _ => "Not set up yet",
        };

        bool connected = s.State == ConnectionState.Connected;
        VisualStateManager.GoToState(this, connected ? "Connected" : "Idle", true);

        if (ShowBattery && connected && s.BatteryPercent is int level)
        {
            Battery.Visibility = Visibility.Visible;
            BatteryText.Text = $"{level}%";
            BatteryGlyph.Glyph = Glyph(level, s.Charging);
            AutomationProperties.SetName(Battery, $"Battery {level} percent{(s.Charging ? ", charging" : "")}");
        }
        else
        {
            Battery.Visibility = Visibility.Collapsed;
        }
    }
}
