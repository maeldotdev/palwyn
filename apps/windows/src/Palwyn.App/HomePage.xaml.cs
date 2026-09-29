using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Palwyn.Core;

namespace Palwyn.App;

/// <summary>One dashboard fact. <paramref name="Fill"/> (0..1) draws a bar under the value.</summary>
public sealed record DashTile(string Glyph, string Label, string Value, string Detail, double? Fill = null)
{
    public Visibility HasFill => Fill is null ? Visibility.Collapsed : Visibility.Visible;
    public double FillValue => Fill ?? 0;
    public string LabelCaps => Label.ToUpperInvariant();
}

/// <summary>The phone at a glance: what's happening with it right now, quick actions and recent activity.</summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = RecentActivity.Items;
        SizeChanged += (_, _) => Arrange();
        Controls.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Arrange());
        Loaded += (_, _) =>
        {
            App.Current.StatusChanged += Render;
            App.Current.Link.DashboardChanged += Render;
            RecentActivity.Items.CollectionChanged += OnActivity;
            App.Current.NotificationPosted += OnNotification;
            App.Current.NotificationRemoved += OnNotificationRemoved;
            App.Current.NotificationsCleared += RenderNotifications;
            Render();
            RenderNotifications();
        };
        Unloaded += (_, _) =>
        {
            App.Current.StatusChanged -= Render;
            App.Current.Link.DashboardChanged -= Render;
            RecentActivity.Items.CollectionChanged -= OnActivity;
            App.Current.NotificationPosted -= OnNotification;
            App.Current.NotificationRemoved -= OnNotificationRemoved;
            App.Current.NotificationsCleared -= RenderNotifications;
        };
    }

    void OnActivity(object? sender, NotifyCollectionChangedEventArgs e) => Render();
    void OnNotification(PhoneNotification _) => RenderNotifications();
    void OnNotificationRemoved(string _) => RenderNotifications();

    // ---- Notifications: the newest three in the phone's shade ----

    void RenderNotifications()
    {
        var latest = App.Current.PhoneNotifications.Values
            .Where(n => !AppSettings.IsMuted(n.Package) && (n.Title ?? n.Text) is not null)
            .OrderByDescending(n => n.PostedAt).Take(3).ToList();
        NotificationsSection.Visibility = latest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Arrange();
        RecentNotifications.Children.Clear();
        foreach (var n in latest)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(AppIcons.Has(n.Package)
                ? new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(AppIcons.UriFor(n.Package)) }
                : new FontIcon { Glyph = "", FontSize = 18, VerticalAlignment = VerticalAlignment.Top });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(Line($"{n.AppName}, {n.PostedAt.ToLocalTime():t}", "CaptionTextBlockStyle", "TextFillColorSecondaryBrush"));
            if (n.Title is not null) text.Children.Add(Line(n.Title, "BodyStrongTextBlockStyle"));
            if (n.Text is not null) text.Children.Add(Line(n.Text, "BodyTextBlockStyle", "TextFillColorSecondaryBrush", reading: true));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var button = new Button
            {
                Content = row,
                Padding = new Thickness(12, 10, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += AllNotifications_Click;
            RecentNotifications.Children.Add(button);
        }
    }

    static TextBlock Line(string text, string style, string? brush = null, bool reading = false)
    {
        var t = new TextBlock
        {
            Text = text.ReplaceLineEndings(" "),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources[style],
        };
        if (brush is not null) t.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush];
        if (reading) t.FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["ReadingFontFamily"];
        return t;
    }

    // ---- Photos: the newest six, fetched once per connection ----

    static DateTimeOffset? _photosFor;
    static List<PhonePhoto> _photos = [];

    async void RenderPhotos(bool connected)
    {
        var link = App.Current.Link;
        bool can = connected && link.Capabilities.Contains("photos.read");
        if (can && _photosFor != link.ConnectedSince)
        {
            _photosFor = link.ConnectedSince;
            try { _photos = [.. await link.PhotosAsync(6)]; }
            catch (Exception e) when (e is Core.Link.PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
            {
                _photos = [];
                _photosFor = null; // try again next time
            }
        }
        PhotosSection.Visibility = can && _photos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Arrange();
        // Tiles are added without awaiting, so a Render arriving mid-way can't interleave; previews fill in after.
        if (RecentPhotos.Tag == _photos) return;
        RecentPhotos.Tag = _photos;
        RecentPhotos.Children.Clear();
        foreach (var photo in _photos)
        {
            var image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            var tile = new Button
            {
                Width = 96,
                Height = 96,
                Padding = new Thickness(0),
                CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"],
                Content = image,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, photo.Name ?? "Photo");
            tile.Click += AllPhotos_Click;
            RecentPhotos.Children.Add(tile);
            Fill(image, photo);
        }

        static async void Fill(Image image, PhonePhoto photo) => image.Source = await PhotosPage.PreviewAsync(photo);
    }

    void AllPhotos_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain("photos");
    void AllNotifications_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain("notifications");

    void Render()
    {
        var s = App.Current.Status;
        var link = App.Current.Link;
        Header.Update(s);
        EmptyHeader.Update(s);
        bool hasBattery = s.State == ConnectionState.Connected && s.BatteryPercent is not null;
        BatteryBlock.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (s.BatteryPercent is int level)
        {
            BatteryBig.Text = $"{level}%";
            BatteryBar.Value = level;
            BatteryDetail.Text = s.Charging ? "Charging" : "On battery";
        }
        bool paired = s.State != ConnectionState.NotPaired;
        EmptyState.Visibility = paired ? Visibility.Collapsed : Visibility.Visible;
        PairedState.Visibility = paired ? Visibility.Visible : Visibility.Collapsed;
        NoActivity.Visibility = RecentActivity.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Problem.Text = s.State == ConnectionState.Blocked ? s.Detail ?? "" : "";
        Problem.Visibility = Problem.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Tiles.ItemsSource = paired ? BuildTiles(s, link) : null;
        RenderPhotos(s.State == ConnectionState.Connected);
        Arrange();
    }

    /// <summary>
    /// Lays the tiles out in rows: side by side when the window is wide, stacked when narrow.
    /// A hidden tile gives its space to its row partner, so rows never end with a gap.
    /// </summary>
    void Arrange()
    {
        bool wide = ActualWidth is 0 or >= 760;
        Bento.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Bento.ColumnSpacing = wide ? 12 : 0;
        Bento.RowDefinitions.Clear();
        int row = 0;
        FrameworkElement[][] rows = [[DeviceTile, Controls], [Tiles], [NotificationsSection, PhotosSection], [ActivityTile]];
        foreach (var group in rows)
        {
            var shown = group.Where(t => t.Visibility == Visibility.Visible).ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                if (i == 0 || !wide) Bento.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(shown[i], row + (wide ? 0 : i));
                Grid.SetColumn(shown[i], wide ? i : 0);
                Grid.SetColumnSpan(shown[i], wide ? 2 / shown.Count : 1);
            }
            row += wide ? Math.Min(shown.Count, 1) : shown.Count;
        }
    }

    static List<DashTile> BuildTiles(PhoneStatus s, Link.LinkManager link)
    {
        var tiles = new List<DashTile>();
        bool connected = s.State == ConnectionState.Connected;

        tiles.Add(connected
            ? new("", "Connection", $"Connected since {link.ConnectedSince?.ToLocalTime():t}",
                link.OverUsb ? "Live over the USB cable, encrypted"
                : link.Paired?.Address is null ? "Live over your Wi-Fi, encrypted" : "Live by address (VPN or other network), encrypted")
            : new("", "Connection", "Not connected",
                link.LastSeen is { } seen ? $"Last connected {seen.ToLocalTime():t}, {seen.ToLocalTime():d MMM}" : "Waiting for your phone"));

        if (connected && link.DeviceStatus is { } st)
        {
            // Segoe Fluent Icons: Wifi1..3 = E872..E874, full Wifi = E701.
            string wifiGlyph = !st.Wifi ? "" : st.WifiSignal switch { >= 4 or null => "", 3 => "", 2 => "", _ => "" };
            tiles.Add(new(wifiGlyph, "Wi-Fi", st.WifiText, st.WifiSignal is int bars ? $"{bars} of 4 bars" : ""));
            // Segoe Fluent Icons: SignalBars1..5 = E86C..E870, no signal = E871.
            tiles.Add(new(st.CellSignal is int cell ? ((char)(0xE86C + cell)).ToString() : "", "Mobile", st.CellText,
                string.Join(" · ", new[] { st.Carrier, st.CellNetwork, st.CellSignal is int b ? $"{b} of 4 bars" : null }.Where(x => x is not null))));
            tiles.Add(new("", "Bluetooth", st.Bluetooth switch { true => "On", false => "Off", null => "Not available" }, ""));
        }

        if (link.Device is { } d)
        {
            long free = connected && link.DeviceStatus is { } status ? status.StorageFree : d.StorageFree;
            if (connected && d.StorageTotal > 0)
                tiles.Add(new("", "Storage", $"{Size(free)} free", $"{Size(d.StorageTotal - free)} of {Size(d.StorageTotal)} used",
                    Math.Clamp(1 - (double)free / d.StorageTotal, 0, 1)));
            tiles.Add(new("", "Phone", $"{d.Manufacturer} {d.Model}", $"Android {d.AndroidVersion}"));
        }
        return tiles;
    }

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0} MB";

    void Add_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain("add");
}
