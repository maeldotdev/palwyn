using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace Palwyn.App;

public sealed partial class SettingsPage : Page
{
    const string StartupTaskId = "PalwynStartup";
    bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var v = Package.Current.Id.Version;
        VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}. Everything stays on your devices.";
        RenderAppearance();
        _ = LoadStartupAsync();
        ToastsToggle.IsOn = AppSettings.NotificationToasts;
        ClipboardToggle.IsOn = AppSettings.ClipboardToPhone;
        RemoteInputToggle.IsOn = AppSettings.RemoteInput;
        RemoteMediaToggle.IsOn = AppSettings.RemoteMedia;
        RenderCommands();
        PauseMediaToggle.IsOn = AppSettings.PauseMediaDuringCalls;
        KeepAwakeToggle.IsOn = AppSettings.KeepPcAwake;
        HistoryToggle.IsOn = AppSettings.NotificationHistory;
        PcNotificationsToggle.IsOn = AppSettings.PcNotificationsToPhone && PcNotificationForwarder.Allowed;
        if (AppSettings.Transparency == 0) TransparencyHint.Text = "Off: the standard Windows material. Slide right for see-through glass.";
        RenderMuted();
        RenderPhone();
        RenderFolders();
        AppSettings.NotificationSettingsChanged += RenderMuted;
        App.Current.StatusChanged += RenderPhone;
        // The tray popup can change these too.
        AppSettings.ThemeChanged += RenderAppearance;
        AppSettings.TransparencyChanged += RenderAppearance;
        Unloaded += (_, _) =>
        {
            AppSettings.NotificationSettingsChanged -= RenderMuted;
            App.Current.StatusChanged -= RenderPhone;
            AppSettings.ThemeChanged -= RenderAppearance;
            AppSettings.TransparencyChanged -= RenderAppearance;
        };
        _loading = false;
    }

    void RenderAppearance()
    {
        ThemeBox.SelectedIndex = AppSettings.Theme switch
        {
            ElementTheme.Dark => 0,
            ElementTheme.Light => 1,
            _ => 2,
        };
        TransparencySlider.Value = AppSettings.Transparency;
    }

    void RenderFolders()
    {
        FilesPath.Text = AppSettings.FilesFolder;
        PhotosPath.Text = AppSettings.PhotosFolder;
        ToolTipService.SetToolTip(FilesPath, FilesPath.Text);
        ToolTipService.SetToolTip(PhotosPath, PhotosPath.Text);
    }

    static string FolderFor(object sender) => (string)((Button)sender).Tag == "files" ? AppSettings.FilesFolder : AppSettings.PhotosFolder;

    async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderFor(sender);
        Directory.CreateDirectory(folder); // nothing saved there yet
        await Windows.System.Launcher.LaunchFolderPathAsync(folder);
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        // The Windows App SDK picker, unlike the UWP one, also works when Palwyn runs as administrator.
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        var picked = await picker.PickSingleFolderAsync();
        if (picked is null) return;
        if ((string)((Button)sender).Tag == "files") AppSettings.FilesFolder = picked.Path;
        else AppSettings.PhotosFolder = picked.Path;
        Log.Info("Save folder changed"); // not the path: it can contain the user's name
        RenderFolders();
    }

    void RenderPhone()
    {
        var link = App.Current.Link;
        var active = link.Paired;
        var phones = link.Phones.All();
        PhoneList.ItemsSource = phones.Select(p => new PhoneRow(p, p.DeviceId == active?.DeviceId,
            p.DeviceId == active?.DeviceId ? App.Current.Status.PhoneName : null, link.IsConnected)).ToList();
        SecurityRow.Visibility = phones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddPhoneTitle.Text = phones.Count == 0 ? "No phone paired" : "Another phone";
        AddPhoneInfo.Text = phones.Count == 0 ? "Pair your Android phone to use Palwyn."
            : "Pair more phones and switch between them here. One phone is connected at a time.";
    }

    static PhoneRow RowOf(object sender) => (PhoneRow)((FrameworkElement)sender).DataContext;

    void AddPhone_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain("add");

    async void UsePhone_Click(object sender, RoutedEventArgs e) => await App.Current.Link.SwitchToAsync(RowOf(sender).Phone.DeviceId);

    async void RemovePhone_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Remove {row.Name}?",
            Content = row.Active && App.Current.Link.IsConnected
                ? "Palwyn will forget this phone, and the phone will forget this PC. You can pair again any time."
                : "Palwyn will forget this phone. It isn't connected, so it will find out the next time this PC tries to reach it; you can also remove this PC on the phone. You can pair again any time.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await App.Current.Link.RemoveAsync(row.Phone.DeviceId);
    }

    async void Address_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        var box = new TextBox { Text = row.Phone.Address ?? "", PlaceholderText = "For example 100.64.12.34", Header = "Phone's IP address (and :port if not 47800)" };
        var problem = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], Visibility = Visibility.Collapsed };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Connect to {row.Name} by address",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "For when this PC can't find the phone by itself: a VPN such as Tailscale or ZeroTier, or a network that blocks "
                             + "discovery. The link stays encrypted and pinned to this phone. Leave empty to find it automatically again.\n\n"
                             + "Or use a USB cable: turn on USB debugging in the phone's Developer options and plug it in. This PC then "
                             + "connects over the cable by itself, if it has adb (Android SDK Platform Tools, free from Google).",
                    },
                    box, problem,
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var text = box.Text.Trim();
            if (text.Length == 0 || Core.Link.PairedPhone.ParseAddress(text, row.Phone.Port) is not null) return;
            problem.Text = "That isn't an address. Use something like 100.64.12.34 or phone.tailnet:47800.";
            problem.Visibility = Visibility.Visible;
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await App.Current.Link.SetAddressAsync(row.Phone.DeviceId, box.Text.Trim() is { Length: > 0 } a ? a : null);
    }

    void RenderMuted()
    {
        var apps = AppSettings.MutedApps.Select(p => new MutedApp(p.Key, p.Value)).OrderBy(a => a.Name).ToList();
        MutedList.ItemsSource = apps;
        MutedHint.Text = apps.Count == 0
            ? "None. To turn one off, right-click its notification on the Notifications page."
            : "Their notifications stay on your phone and don't show on this PC.";
    }

    void ClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) AppSettings.ClipboardToPhone = ClipboardToggle.IsOn;
    }

    void RemoteInputToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) AppSettings.RemoteInput = RemoteInputToggle.IsOn;
    }

    void RemoteMediaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) AppSettings.RemoteMedia = RemoteMediaToggle.IsOn;
    }

    void RenderCommands() => CommandList.ItemsSource = AppSettings.RemoteCommands;

    void RemoveCommand_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        AppSettings.SetRemoteCommands(AppSettings.RemoteCommands.Where(c => c.Id != id));
        RenderCommands();
    }

    async void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Name on your phone", PlaceholderText = "For example Shut down", MaxLength = 64 };
        var command = new TextBox { Header = "Command", PlaceholderText = "For example shutdown /s /t 60", FontFamily = new("Consolas") };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Add a command",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Runs in Command Prompt as you, without a window, when you tap it on your phone. "
                             + "Anything you could type there works, like opening an app or a website.",
                    },
                    name, command,
                },
            },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
        };
        void Check(object s, TextChangedEventArgs a) =>
            dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 && command.Text.Trim().Length > 0;
        dialog.IsPrimaryButtonEnabled = false;
        name.TextChanged += Check;
        command.TextChanged += Check;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        AppSettings.SetRemoteCommands([.. AppSettings.RemoteCommands,
            new RemoteCommand(Guid.NewGuid().ToString("N")[..12], name.Text.Trim(), command.Text.Trim())]);
        RenderCommands();
    }

    void PauseMediaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) AppSettings.PauseMediaDuringCalls = PauseMediaToggle.IsOn;
    }

    void KeepAwakeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.KeepPcAwake = KeepAwakeToggle.IsOn;
        KeepPcAwake.Update(App.Current.Status.State == Core.ConnectionState.Connected);
    }

    void HistoryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.NotificationHistory = HistoryToggle.IsOn;
        _ = App.Current.History; // off deletes it
    }

    async void PcNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = PcNotificationsToggle.IsOn;
        if (on && !await PcNotificationForwarder.RequestAccessAsync())
        {
            _loading = true;
            PcNotificationsToggle.IsOn = on = false;
            _loading = false;
            PcNotificationsHint.Text = "Windows didn't allow Palwyn to read notifications. Allow it in Windows Settings > Privacy & security > Notifications, then try again.";
        }
        AppSettings.PcNotificationsToPhone = on;
        PcNotificationForwarder.Update();
    }

    void ToastsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) AppSettings.NotificationToasts = ToastsToggle.IsOn;
    }

    void Unmute_Click(object sender, RoutedEventArgs e)
    {
        var app = (MutedApp)((FrameworkElement)sender).DataContext;
        AppSettings.SetMuted(app.Package, app.Name, false);
    }

    async Task LoadStartupAsync()
    {
        var task = await StartupTask.GetAsync(StartupTaskId);
        _loading = true;
        StartupToggle.IsOn = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        StartupToggle.IsEnabled = task.State is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupHint.Text = task.State switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Windows Settings > Apps > Startup. Turn it back on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organisation.",
            _ => "Starts in the notification area when you sign in.",
        };
        _loading = false;
    }

    async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var task = await StartupTask.GetAsync(StartupTaskId);
        if (StartupToggle.IsOn)
            Log.Info($"Start with Windows: {await task.RequestEnableAsync()}");
        else
        {
            task.Disable();
            Log.Info("Start with Windows: disabled");
        }
        await LoadStartupAsync();
    }

    void TransparencySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int level = (int)e.NewValue;
        TransparencyHint.Text = level == 0
            ? "Off: the standard Windows material. Slide right for see-through glass."
            : $"{level}%. The desktop shows through the Palwyn window, tray popup and Send to phone window.";
        if (!_loading) AppSettings.Transparency = level;
    }

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<ElementTheme>(tag, out var theme)
            && theme != AppSettings.Theme)
            AppSettings.Theme = theme;
    }

    async void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Folder);
        await Windows.System.Launcher.LaunchFolderPathAsync(Log.Folder);
    }
}

public sealed record MutedApp(string Package, string Name);

/// <param name="LiveName">The in-use phone's current name from the link, if connected.</param>
public sealed record PhoneRow(Core.Link.PairedPhone Phone, bool Active, string? LiveName, bool Connected)
{
    public string Name => LiveName ?? Phone.Name;
    public bool CanUse => !Active;
    public string Info =>
        (Active ? Connected ? "In use, connected. " : "In use, not connected. " : "Not in use. ")
        + $"Paired {Phone.PairedAt.ToLocalTime():d MMM yyyy}. Phone ID {Core.Fingerprint.Display(Phone.DeviceId)}, shown on the phone too."
        + (Phone.Address is { } a ? $" Connects to {a}." : "");
}
