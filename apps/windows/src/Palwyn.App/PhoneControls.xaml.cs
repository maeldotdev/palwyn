using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Palwyn.App;

/// <summary>Now playing on the phone, plus <see cref="QuickActions"/>. Collapses itself when there's nothing to show.</summary>
public sealed partial class PhoneControls : UserControl
{
    CancellationTokenSource? _resultTimer;

    /// <summary>Action buttons per row: 2 in the narrow tray popup, 5 on Home.</summary>
    public int Columns { get; set; } = 2;

    /// <summary>Taller buttons with the icon above the label, for the Home tiles. Send files is the accent.</summary>
    public bool TileButtons { get; set; }

    public PhoneControls()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Current.StatusChanged += Render;
            App.Current.Link.DashboardChanged += Render;
            QuickActions.Changed += Render;
            Render();
        };
        Unloaded += (_, _) =>
        {
            App.Current.StatusChanged -= Render;
            App.Current.Link.DashboardChanged -= Render;
            QuickActions.Changed -= Render;
        };
    }

    void Render()
    {
        var link = App.Current.Link;
        var now = link.IsConnected ? link.NowPlaying : null;
        MediaCard.Visibility = now is null ? Visibility.Collapsed : Visibility.Visible;
        if (now is not null)
        {
            MediaApp.Text = now.App;
            MediaTitle.Text = now.Title ?? "Playing";
            MediaArtist.Text = now.Artist ?? "";
            MediaArtist.Visibility = now.Artist is null ? Visibility.Collapsed : Visibility.Visible;
            PlayPauseGlyph.Glyph = now.Playing ? "" : "";
            ToolTipService.SetToolTip(PlayPause, now.Playing ? "Pause" : "Play");
        }

        Actions.Children.Clear();
        Actions.RowDefinitions.Clear();
        if (Actions.ColumnDefinitions.Count != Columns)
        {
            Actions.ColumnDefinitions.Clear();
            for (int c = 0; c < Columns; c++) Actions.ColumnDefinitions.Add(new ColumnDefinition());
        }
        int i = 0;
        var available = QuickActions.Available().ToList();
        foreach (var action in available)
        {
            if (i % Columns == 0) Actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = TileButtons ? Orientation.Vertical : Orientation.Horizontal,
                    Spacing = TileButtons ? 14 : 10,
                    Children =
                    {
                        new FontIcon { Glyph = action.Glyph, FontSize = TileButtons ? 20 : 16, HorizontalAlignment = HorizontalAlignment.Left },
                        new TextBlock
                        {
                            Text = action.Title(),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            TextWrapping = TileButtons ? TextWrapping.Wrap : TextWrapping.NoWrap,
                            MaxLines = TileButtons ? 2 : 1,
                        },
                    },
                },
            };
            if (TileButtons)
            {
                button.Padding = new Thickness(14, 14, 14, 12);
                button.CornerRadius = new CornerRadius(10);
                if (action.Id == "files") button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                var message = await QuickActions.RunAsync(action);
                button.IsEnabled = true;
                if (message is not null) Show(message);
            };
            if (action.Hint is { } hint) ToolTipService.SetToolTip(button, hint);
            Grid.SetRow(button, i / Columns);
            Grid.SetColumn(button, i % Columns);
            // The last button fills what's left of its row, so the grid never ends with a gap.
            if (i == available.Count - 1) Grid.SetColumnSpan(button, Columns - i % Columns);
            Actions.Children.Add(button);
            i++;
        }
        Actions.Visibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
        Visibility = i == 0 && now is null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Show(string message)
    {
        _resultTimer?.Cancel();
        var cts = _resultTimer = new CancellationTokenSource();
        Result.Text = message;
        Result.Visibility = Visibility.Visible;
        try { await Task.Delay(4000, cts.Token); }
        catch (OperationCanceledException) { return; }
        Result.Visibility = Visibility.Collapsed;
    }

    async void Media_Click(object sender, RoutedEventArgs e) => await Media((string)((Button)sender).Tag);

    async void PlayPause_Click(object sender, RoutedEventArgs e) =>
        await Media(App.Current.Link.NowPlaying?.Playing == true ? "pause" : "play");

    async Task Media(string action)
    {
        try
        {
            await App.Current.Link.MediaAsync(action);
        }
        catch (Exception e) when (e is Core.Link.PhoneErrorException or Core.Protocol.ProtocolException or TimeoutException
                                  or IOException or InvalidOperationException)
        {
            Show("Your phone didn't respond. Check that it's connected.");
        }
    }
}
