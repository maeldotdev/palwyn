using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Search;
using Palwyn.Core;
using Windows.System;
using WinRT.Interop;

namespace Palwyn.App;

/// <summary>
/// Quick Drop, PC to phone: drop or pick files, type text or a link, or "Share > Palwyn" from any Windows app.
/// Items wait in the list while the phone is away and go out when it reconnects.
/// </summary>
public sealed partial class QuickDropWindow : Window
{
    readonly ObservableCollection<DropJob> _jobs = [];
    readonly CancellationTokenSource _closing = new();
    readonly Win32.WndProc _proc; // kept alive: Windows calls it for as long as the window exists
    readonly IntPtr _previousProc;
    bool _working;

    public QuickDropWindow()
    {
        InitializeComponent();
        Queue.ItemsSource = _jobs;
        var hwnd = WindowNative.GetWindowHandle(this);
        Win32.AcceptFileDrops(hwnd);
        _proc = WndProc;
        _previousProc = Win32.SetWindowLongPtr(hwnd, Win32.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc));
        GlassBackdrop.Follow(this, () => new MicaBackdrop());
        WindowIcon.Follow(AppWindow, Root);
        double scale = Win32.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(460 * scale), (int)(560 * scale));
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2, size.Width, size.Height));

        App.Current.StatusChanged += OnStatus;
        AppSettings.ThemeChanged += ApplyTheme;
        Closed += (_, _) =>
        {
            App.Current.StatusChanged -= OnStatus;
            AppSettings.ThemeChanged -= ApplyTheme;
            _closing.Cancel(); // a copy in progress stops; the phone deletes the half-written file
        };
        ApplyTheme();
        Render();
    }

    static bool CanSend => App.Current.Link.IsConnected && App.Current.Link.Capabilities.Contains("drop");

    void OnStatus()
    {
        Render();
        Pump(); // the phone came back: send what's waiting
    }

    void Render()
    {
        Heading.Text = $"Send to {App.Current.Status.PhoneName ?? "your phone"}";
        var why = App.Current.Link.Paired is null ? "Pair a phone first."
            : !CanSend ? "Your phone isn't connected. Items you add wait here and go out when it reconnects."
            : null;
        Problem.Text = why ?? "";
        Problem.Visibility = why is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void ApplyTheme() => Root.RequestedTheme = AppSettings.Theme;

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_DROPFILES)
        {
            _ = AddPaths(Win32.DroppedFiles(wParam));
            return IntPtr.Zero;
        }
        return Win32.CallWindowProcW(_previousProc, hWnd, msg, wParam, lParam);
    }

    async Task AddPaths(List<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try { items.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p)); }
            catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException) { }
        }
        await AddItems(items);
    }

    // ---- Adding ----

    /// <summary>"Share > Palwyn" from another app. The data must be read before the share UI is released.</summary>
    public async Task AddShared(ShareOperation share)
    {
        try
        {
            var data = share.Data;
            if (data.Contains(StandardDataFormats.StorageItems)) await AddItems(await data.GetStorageItemsAsync());
            else if (data.Contains(StandardDataFormats.WebLink)) AddText((await data.GetWebLinkAsync()).ToString());
            else if (data.Contains(StandardDataFormats.Text)) AddText(await data.GetTextAsync());
        }
        finally
        {
            share.ReportCompleted();
        }
    }

    const int MaxFolderFiles = 1000;

    /// <summary>Files, and folders with everything in them (kept in the same folders on the phone).</summary>
    async Task AddItems(IEnumerable<IStorageItem> items)
    {
        foreach (var item in items)
        {
            if (item is StorageFile file) await AddFile(file, null);
            else if (item is StorageFolder folder)
            {
                var files = await folder.CreateFileQueryWithOptions(new QueryOptions { FolderDepth = FolderDepth.Deep }).GetFilesAsync();
                if (files.Count > MaxFolderFiles)
                {
                    Problem.Text = $"“{folder.Name}” has {files.Count:N0} files; only folders up to {MaxFolderFiles:N0} can be sent. Send its subfolders one at a time.";
                    Problem.Visibility = Visibility.Visible;
                    continue;
                }
                foreach (var f in files) await AddFile(f, DropFolders.RelativeFolder(folder.Path, f.Path));
            }
        }
        Pump();
    }

    async Task AddFile(StorageFile file, string? folder)
    {
        var props = await file.GetBasicPropertiesAsync();
        _jobs.Add(DropJob.ForFile(file.Name, string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            (long)props.Size, () => file.OpenStreamForReadAsync(), folder));
    }

    void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _jobs.Add(DropJob.ForText(text.Trim()));
        Pump();
    }

    async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await AddItems(files);
    }

    async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        // The Windows App SDK picker, like Settings' folder choice: it also works when Palwyn runs as administrator.
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
        if (await picker.PickSingleFolderAsync() is { } picked) await AddItems([await StorageFolder.GetFolderFromPathAsync(picked.Path)]);
    }

    void DropZone_DragOver(object sender, DragEventArgs e)
    {
        var d = e.DataView;
        if (!d.Contains(StandardDataFormats.StorageItems) && !d.Contains(StandardDataFormats.Text) && !d.Contains(StandardDataFormats.WebLink)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Send to phone";
        DropOutline.Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    void DropZone_DragLeave(object sender, DragEventArgs e) =>
        DropOutline.Stroke = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];

    async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        var d = e.DataView;
        var deferral = e.GetDeferral();
        try
        {
            if (d.Contains(StandardDataFormats.StorageItems)) await AddItems(await d.GetStorageItemsAsync());
            else if (d.Contains(StandardDataFormats.WebLink)) AddText((await d.GetWebLinkAsync()).ToString());
            else if (d.Contains(StandardDataFormats.Text)) AddText(await d.GetTextAsync());
        }
        finally
        {
            deferral.Complete();
        }
    }

    void TextInput_TextChanged(object sender, TextChangedEventArgs e) => SendTextButton.IsEnabled = TextInput.Text.Trim().Length > 0;

    void TextInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SendText_Click(sender, e);
    }

    void SendText_Click(object sender, RoutedEventArgs e)
    {
        AddText(TextInput.Text);
        TextInput.Text = "";
    }

    // ---- Sending ----

    /// <summary>Sends waiting items one at a time, while the phone is connected.</summary>
    async void Pump()
    {
        if (_working) return;
        _working = true;
        try
        {
            while (CanSend && !_closing.IsCancellationRequested && _jobs.FirstOrDefault(j => j.State == DropState.Waiting) is { } job)
                await Send(job);
        }
        finally
        {
            _working = false;
        }
    }

    async Task Send(DropJob job)
    {
        job.Start();
        try
        {
            if (job.Text is { } text) await App.Current.Link.SendTextAsync(text);
            else
            {
                await using var stream = await job.Open!();
                await App.Current.Link.SendFileAsync(job.FileName, job.Mime, stream, job.Size,
                    new Progress<long>(done => job.Progress = job.Size > 0 ? (double)done / job.Size : 1), _closing.Token, job.Folder);
            }
            job.Finish(ok: true);
            RecentActivity.Add("", job.Text is null ? $"Sent {job.Name}" : "Sent text to your phone");
        }
        catch (Exception e) when (e is PhoneErrorException or ProtocolException or TimeoutException or IOException
                                  or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            Log.Info($"Quick Drop failed: {e.GetType().Name}: {e.Message}");
            job.Finish(ok: false);
        }
    }
}

public enum DropState { Waiting, Running, Done, Failed }

public sealed partial class DropJob : INotifyPropertyChanged
{
    double _progress;
    DropState _state;

    DropJob(string name, string glyph) { Name = name; Glyph = glyph; }

    /// <param name="folder">Where it goes inside Download/Palwyn ("Trip/Day 1"), when sending a folder.</param>
    public static DropJob ForFile(string name, string mime, long size, Func<Task<Stream>> open, string? folder = null) =>
        new(folder is null ? name : $"{folder}/{name}", "") { Mime = mime, Size = size, Open = open, FileName = name, Folder = folder };

    public static DropJob ForText(string text) =>
        new(text.ReplaceLineEndings(" "), Uri.IsWellFormedUriString(text, UriKind.Absolute) ? "" : "") { Text = text };

    public string Name { get; }
    public string Glyph { get; }
    public string Mime { get; private init; } = "";
    /// <summary>The file's own name; <see cref="Name"/> also shows its folder.</summary>
    public string FileName { get; private init; } = "";
    public string? Folder { get; private init; }
    public long Size { get; private init; }
    public Func<Task<Stream>>? Open { get; private init; }
    public string? Text { get; private init; }
    public DropState State => _state;
    public bool Running => _state == DropState.Running && Text is null;

    public double Progress
    {
        get => _progress;
        set { _progress = value; Changed(nameof(Progress)); }
    }

    public string Status => _state switch
    {
        DropState.Waiting => "Waiting for your phone",
        DropState.Running => "Sending…",
        DropState.Done => Text is null ? "Sent to Download > Palwyn" : "Sent. It's in your phone's notifications.",
        _ => "Couldn't send. Check the phone's storage and connection, then add it again.",
    };

    public void Start() => SetState(DropState.Running);
    public void Finish(bool ok) => SetState(ok ? DropState.Done : DropState.Failed);

    void SetState(DropState s)
    {
        _state = s;
        Changed(nameof(Status));
        Changed(nameof(Running));
    }

    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
