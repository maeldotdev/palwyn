using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Palwyn.Core;
using Palwyn.Core.Emergency;

namespace Palwyn.Linux;

/// <summary>Rescue files (Core Emergency/Rescue.cs), as on Windows: pick a folder, copy, see what was copied.</summary>
public sealed class RescueWindow : Window
{
    static RescueWindow? _current;

    readonly AdbDevice _device;
    string _folder = Rescue.DefaultFolder();
    CancellationTokenSource? _run;
    readonly TextBlock _folderText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1 };
    readonly Button _start = new() { Content = "Start", Classes = { "accent" } };
    readonly Button _change = new() { Content = "Change folder…" };
    readonly Button _cancel = new() { Content = "Stop", IsVisible = false };

    public static void Open(AdbDevice device)
    {
        if (_current is { } open && open._device.Serial != device.Serial && open._run is null) open.Close();
        _current ??= new RescueWindow(device);
        _current.Show();
        _current.Activate();
    }

    RescueWindow(AdbDevice device)
    {
        _device = device;
        Title = "Rescue files";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        FontFamily = App.Mono;
        _folderText.Text = _folder;
        _change.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Copy the phone's files to" });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) _folderText.Text = _folder = path;
        };
        _start.Click += (_, _) => _ = StartAsync();
        _cancel.Click += (_, _) => _run?.Cancel();
        var open = new Button { Content = "Open folder" };
        open.Click += (_, _) => Paths.Open(_folder);
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Rescue files from {device.Model?.Replace('_', ' ') ?? "your phone"}", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Copies photos, videos, music, downloads, documents and app media (like WhatsApp's) from the phone to this PC over adb, without using the phone's screen. Run it again to copy only what's new. Other apps' private data can't be copied.",
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                },
                _folderText,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, _change, _cancel, open } },
                _progress,
                _status,
            },
        };
        Closed += (_, _) =>
        {
            _run?.Cancel();
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    async Task StartAsync()
    {
        var run = _run = new CancellationTokenSource();
        _start.IsEnabled = _change.IsEnabled = false;
        _cancel.IsVisible = true;
        _status.Text = "Looking for files on the phone…";
        _progress.IsIndeterminate = true;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            _progress.IsIndeterminate = false;
            _progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            _status.Text = $"{p.Done} of {p.Total} files";
        });
        try
        {
            var r = await Rescue.RunAsync(_device.Serial, _folder, progress, run.Token);
            _status.Text = $"Copied {r.Copied}, skipped {r.Skipped}, failed {r.Failed}.";
        }
        catch (OperationCanceledException) { _status.Text = "Stopped. Files copied so far are kept; run it again to continue."; }
        catch (EmergencyException ex) { _status.Text = ex.Message; }
        finally
        {
            _run = null;
            _progress.IsIndeterminate = false;
            _start.IsEnabled = _change.IsEnabled = true;
            _cancel.IsVisible = false;
        }
    }
}
