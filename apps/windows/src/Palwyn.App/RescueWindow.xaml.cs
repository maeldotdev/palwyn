using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Palwyn.Core.Emergency;
using Palwyn.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace Palwyn.App;

/// <summary>Rescue files (Emergency/Rescue.cs): pick a folder, copy, see what was copied.</summary>
public sealed partial class RescueWindow : Window
{
    static RescueWindow? _current;

    readonly AdbDevice _device;
    string _folder = Rescue.DefaultFolder();
    CancellationTokenSource? _run;

    public static void Open(AdbDevice device)
    {
        if (_current is { } open && open._device.Serial != device.Serial && open._run is null) open.Close();
        _current ??= new RescueWindow(device);
        _current.Activate();
        Win32.SetForegroundWindow(WindowNative.GetWindowHandle(_current));
    }

    RescueWindow(AdbDevice device)
    {
        _device = device;
        InitializeComponent();
        Heading.Text = $"Rescue files from {device.Model?.Replace('_', ' ') ?? "your phone"}";
        FolderText.Text = _folder;
        GlassBackdrop.Follow(this, () => new MicaBackdrop());
        WindowIcon.Follow(AppWindow, Root);
        double scale = Win32.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(520 * scale), (int)(340 * scale));
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2, size.Width, size.Height));
        AppSettings.ThemeChanged += ApplyTheme;
        Closed += (_, _) =>
        {
            _run?.Cancel();
            AppSettings.ThemeChanged -= ApplyTheme;
            if (ReferenceEquals(_current, this)) _current = null;
        };
        ApplyTheme();
    }

    void ApplyTheme() => Root.RequestedTheme = AppSettings.Theme;

    async void Change_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
        if (await picker.PickSingleFolderAsync() is { } picked) FolderText.Text = _folder = picked.Path;
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var run = _run = new CancellationTokenSource();
        StartButton.IsEnabled = ChangeButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "Looking for files on the phone…";
        Progress.IsIndeterminate = true;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            StatusText.Text = $"{p.Done} of {p.Total} files";
        });
        try
        {
            var r = await Rescue.RunAsync(_device.Serial, _folder, progress, run.Token);
            StatusText.Text = $"Copied {r.Copied}, skipped {r.Skipped}, failed {r.Failed}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Stopped. Files copied so far are kept; run it again to continue."; }
        catch (EmergencyException ex) { StatusText.Text = ex.Message; }
        finally
        {
            _run = null;
            Progress.IsIndeterminate = false;
            StartButton.IsEnabled = ChangeButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _run?.Cancel();
}
