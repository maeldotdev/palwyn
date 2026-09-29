using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace Palwyn.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        GlassBackdrop.Follow(this, () => new MicaBackdrop());
        WindowIcon.Follow(AppWindow, Root);

        double scale = Win32.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(700 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = (int)(720 * scale);
            p.PreferredMinimumHeight = (int)(520 * scale);
        }

        // Closing releases the window (Palwyn keeps running in the tray); it is recreated on demand.
        Closed += (_, _) =>
        {
            AppSettings.ThemeChanged -= ApplyTheme;
            App.Current.NotificationPosted -= OnNotification;
            App.Current.NotificationRemoved -= OnNotificationRemoved;
            App.Current.NotificationsCleared -= UpdateBadge;
        };
        AppSettings.ThemeChanged += ApplyTheme;
        App.Current.NotificationPosted += OnNotification;
        App.Current.NotificationRemoved += OnNotificationRemoved;
        App.Current.NotificationsCleared += UpdateBadge;
        ApplyTheme();
        UpdateBadge();
        Nav.SelectedItem = HomeItem;
    }

    public void Navigate(string page)
    {
        if (page == "add")
        {
            Nav.SelectedItem = null;
            ContentFrame.Navigate(typeof(AddPhonePage), null, new EntranceNavigationTransitionInfo());
        }
        else if (page == "calls") Nav.SelectedItem = CallsItem;
        else if (page == "contacts") Nav.SelectedItem = ContactsItem;
        else if (page == "notifications") Nav.SelectedItem = NotificationsItem;
        else if (page == "photos") Nav.SelectedItem = PhotosItem;
        else if (page == "messages" || page.StartsWith("messages:"))
        {
            // Navigate first, with the conversation to open; selecting the item then finds the page already shown.
            ContentFrame.Navigate(typeof(MessagesPage), page.Length > 9 ? page[9..] : null, new EntranceNavigationTransitionInfo());
            Nav.SelectedItem = MessagesItem;
        }
        else if (page != "settings") Nav.SelectedItem = HomeItem;
        else if (Nav.SettingsItem is not null) Nav.SelectedItem = Nav.SettingsItem;
        else Nav.Loaded += SelectSettingsOnce; // settings item exists only after the template loads
    }

    void SelectSettingsOnce(object sender, RoutedEventArgs e)
    {
        Nav.Loaded -= SelectSettingsOnce;
        Nav.SelectedItem = Nav.SettingsItem;
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is null && !args.IsSettingsSelected) return; // pages outside the menu, e.g. Add phone
        var target = args.IsSettingsSelected ? typeof(SettingsPage)
            : ReferenceEquals(args.SelectedItem, CallsItem) ? typeof(CallsPage)
            : ReferenceEquals(args.SelectedItem, ContactsItem) ? typeof(ContactsPage)
            : ReferenceEquals(args.SelectedItem, MessagesItem) ? typeof(MessagesPage)
            : ReferenceEquals(args.SelectedItem, NotificationsItem) ? typeof(NotificationsPage)
            : ReferenceEquals(args.SelectedItem, PhotosItem) ? typeof(PhotosPage)
            : typeof(HomePage);
        if (ContentFrame.CurrentSourcePageType != target)
            ContentFrame.Navigate(target, null, new EntranceNavigationTransitionInfo());
    }

    void OnNotification(Core.PhoneNotification _) => UpdateBadge();
    void OnNotificationRemoved(string _) => UpdateBadge();

    /// <summary>How many notifications are in the phone's shade right now (muted apps left out).</summary>
    void UpdateBadge()
    {
        int count = App.Current.PhoneNotifications.Values.Count(n => !AppSettings.IsMuted(n.Package));
        NotificationsItem.InfoBadge = count == 0 ? null : new InfoBadge { Value = count };
    }

    void ApplyTheme()
    {
        Root.RequestedTheme = AppSettings.Theme;
        AppWindow.TitleBar.PreferredTheme = AppSettings.Theme switch
        {
            ElementTheme.Dark => TitleBarTheme.Dark,
            ElementTheme.Light => TitleBarTheme.Light,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }
}
