using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Palwyn.App;

/// <summary>
/// The phone's photos, newest first. Previews come over the session; full-size photos are copied only when
/// the user opens or saves them, each over its own connection.
/// </summary>
public sealed partial class PhotosPage : Page
{
    const int PageSize = 100;
    /// <summary>Where "Open" puts full-size copies; emptied every time Palwyn starts.</summary>
    public static string OpenFolder => Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "photos");
    static string SaveFolder => AppSettings.PhotosFolder;

    // ponytail: previews live in memory for this run, dropped wholesale past 600; a disk cache if reopening feels slow.
    static readonly Dictionary<string, BitmapImage> Previews = [];
    static readonly SemaphoreSlim PreviewSlots = new(4); // a few at a time: the session also carries calls and messages

    readonly ObservableCollection<PhotoRow> _rows = [];
    bool _loading, _loaded, _albumsLoaded, _pickingAlbum;
    string? _album; // null = all photos
    CancellationTokenSource? _transfer;

    public PhotosPage()
    {
        InitializeComponent();
        State.ActionClicked += () => _ = Load();
        Grid.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            App.Current.StatusChanged += OnStatus;
            _ = Load();
        };
        Unloaded += (_, _) =>
        {
            App.Current.StatusChanged -= OnStatus;
            _transfer?.Cancel();
        };
    }

    static PageState? Blocked()
    {
        var link = App.Current.Link;
        if (link.Paired is null) return PageState.Empty("", "No phone yet", "Pair your phone to see its photos here.");
        if (!link.IsConnected) return PageState.Offline("Photos and videos");
        if (!link.Capabilities.Contains("photos.read")) return PageState.NeedsPermission("photos", "Photos and videos on your PC");
        return null;
    }

    void OnStatus()
    {
        if (!_loaded && !_loading && Blocked() is null) _ = Load();
        UpdateCommands();
    }

    // ---- List ----

    async Task Load(bool older = false)
    {
        if (_loading) return;
        if (Blocked() is { } why)
        {
            _loaded = false;
            ShowMessage(why);
            return;
        }
        _loading = true;
        if (_rows.Count == 0)
        {
            State.Show(null);
            Placeholder.Visibility = Visibility.Visible;
        }
        else ListLoading.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = MoreButton.IsEnabled = false;
        try
        {
            var page = await App.Current.Link.PhotosAsync(PageSize, older && _rows.Count > 0 ? _rows[^1].Photo.Id : null, _album);
            if (!_albumsLoaded) _ = LoadAlbums();
            if (!older) _rows.Clear();
            foreach (var p in page) _rows.Add(new PhotoRow(p));
            _loaded = true;
            MoreButton.Visibility = page.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count == 0) ShowMessage(PageState.Empty("", "No photos yet", "Photos and videos on your phone will show up here."));
            else
            {
                State.Show(null);
                Grid.Visibility = Visibility.Visible;
            }
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Photos failed: {e.Message}");
            _loaded = false;
            ShowMessage(e is PhoneErrorException { Code: "PERMISSION_DENIED" }
                ? PageState.NeedsPermission("photos", "Photos and videos on your PC")
                : PageState.Failed("photos"));
        }
        finally
        {
            _loading = false;
            ListLoading.Visibility = Placeholder.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = MoreButton.IsEnabled = true;
            UpdateCommands();
        }
    }

    async Task LoadAlbums()
    {
        _albumsLoaded = true;
        try
        {
            var albums = await App.Current.Link.AlbumsAsync();
            var items = albums.Select(a => new AlbumItem(a.Id, $"{a.Name} ({a.Count:N0})")).Prepend(new AlbumItem(null, "All photos")).ToList();
            _pickingAlbum = true;
            AlbumBox.ItemsSource = items;
            AlbumBox.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Id == _album));
            _pickingAlbum = false;
            AlbumBox.Visibility = albums.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            _albumsLoaded = false; // try again with the next load
            Log.Info($"Albums failed: {e.Message}");
        }
    }

    async void AlbumBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pickingAlbum || AlbumBox.SelectedItem is not AlbumItem item || item.Id == _album) return;
        _album = item.Id;
        _rows.Clear(); // a different set: show the placeholder, not the old album
        await Load();
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => Grid.SelectAll();

    async void Camera_Click(object sender, RoutedEventArgs e)
    {
        CameraButton.IsEnabled = false;
        try
        {
            await App.Current.Link.RequestCameraAsync();
            Result.Severity = InfoBarSeverity.Informational;
            Result.Title = "Check your phone";
            Result.Message = $"Tap the notification on your phone to take the photo. It'll be saved on this PC in {AppSettings.Describe(SaveFolder)}.";
        }
        catch (Exception ex) when (ex is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Camera request failed: {ex.Message}");
            Result.Severity = InfoBarSeverity.Error;
            Result.Title = "Couldn't reach your phone";
            Result.Message = "";
        }
        OpenFolderButton.Visibility = Visibility.Collapsed;
        Result.IsOpen = true;
        CameraButton.IsEnabled = true;
    }

    void ShowMessage(PageState state)
    {
        State.Show(state);
        Grid.Visibility = Visibility.Collapsed;
    }

    // Previews load as tiles scroll into view, not all at once.
    void Grid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PhotoRow row) LoadPreview(row);
    }

    static async void LoadPreview(PhotoRow row)
    {
        if (row.Thumb is null && await PreviewAsync(row.Photo) is { } image) row.Thumb = image;
    }

    /// <summary>A photo's preview, cached for this run; null if the phone couldn't make one (the tile stays blank).</summary>
    public static async Task<BitmapImage?> PreviewAsync(PhonePhoto photo)
    {
        var key = App.Current.Link.Paired?.DeviceId + "/" + photo.Id; // ids repeat across phones
        if (Previews.TryGetValue(key, out var cached)) return cached;
        await PreviewSlots.WaitAsync();
        try
        {
            if (Previews.TryGetValue(key, out cached)) return cached;
            var jpeg = await App.Current.Link.PhotoThumbnailAsync(photo.Id, photo.Video);
            var image = new BitmapImage { DecodePixelWidth = 300 };
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(jpeg.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            if (Previews.Count > 600) Previews.Clear();
            Previews[key] = image;
            return image;
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException or FormatException or COMException)
        {
            return null;
        }
        finally
        {
            PreviewSlots.Release();
        }
    }

    // ---- Open and save ----

    List<PhotoRow> Selected => Grid.SelectedItems.OfType<PhotoRow>().ToList();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommands();

    void UpdateCommands()
    {
        int n = Grid.SelectedItems.Count;
        bool idle = _transfer is null;
        OpenButton.IsEnabled = idle && n == 1;
        SaveButton.IsEnabled = idle && n > 0;
        SelectAllButton.IsEnabled = idle && _rows.Count > 0 && n < _rows.Count;
        var link = App.Current.Link;
        CameraButton.Visibility = link.IsConnected && link.Capabilities.Contains("camera") ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = n == 0 ? "" : n == 1 ? "1 selected" : $"{n} selected";
    }

    async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelected();

    async void Grid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await OpenSelected();

    async void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Grid.SelectedItems.Count != 1) return;
        e.Handled = true;
        await OpenSelected();
    }

    async Task OpenSelected()
    {
        if (Selected is not [var row] || _transfer is not null) return;
        Directory.CreateDirectory(OpenFolder);
        var path = Path.Combine(OpenFolder, row.Photo.Id + "-" + PhonePhoto.SafeFileName(row.Photo.Name, row.Photo.Mime));
        if (!File.Exists(path) && !await Copy([(row, path)], "Opening")) return;
        await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected;
        if (rows.Count == 0 || _transfer is not null) return;
        Directory.CreateDirectory(SaveFolder);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = rows.Select(r => (r, PhonePhoto.UniquePath(SaveFolder, PhonePhoto.SafeFileName(r.Photo.Name, r.Photo.Mime), taken))).ToList();
        if (!await Copy(jobs, "Saving")) return;
        Result.Severity = InfoBarSeverity.Success;
        Result.Title = jobs.Count == 1 ? $"Saved to {AppSettings.Describe(SaveFolder)}" : $"Saved {jobs.Count} photos to {AppSettings.Describe(SaveFolder)}";
        Result.Message = "";
        OpenFolderButton.Visibility = Visibility.Visible;
        Result.IsOpen = true;
    }

    /// <returns>false if cancelled or failed (the error is shown).</returns>
    async Task<bool> Copy(List<(PhotoRow Row, string Path)> jobs, string verb)
    {
        _transfer = new CancellationTokenSource();
        Result.IsOpen = false;
        TransferPanel.Visibility = Visibility.Visible;
        UpdateCommands();
        try
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                var (row, path) = jobs[i];
                TransferText.Text = jobs.Count == 1 ? $"{verb} {row.Label}…" : $"{verb} {i + 1} of {jobs.Count}…";
                TransferBar.Value = 0;
                long size = Math.Max(1, row.Photo.Size);
                await App.Current.Link.DownloadAsync(row.Photo.Video ? "video" : "photo", row.Photo.Id, path,
                    new Progress<long>(done => TransferBar.Value = Math.Min(1, (double)done / size)), _transfer.Token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is ProtocolException or TimeoutException or IOException or InvalidOperationException
                                  or System.Security.Authentication.AuthenticationException or UnauthorizedAccessException)
        {
            Log.Info($"Photo copy failed: {e.GetType().Name}: {e.Message}");
            Result.Severity = InfoBarSeverity.Error;
            Result.Title = "Couldn't copy the photo";
            Result.Message = e is UnauthorizedAccessException or IOException { HResult: unchecked((int)0x80070070) }
                ? "Windows didn't allow writing the file. Check free space and the Pictures folder."
                : "It may have been deleted on your phone, or the connection dropped. Try again.";
            OpenFolderButton.Visibility = Visibility.Collapsed;
            Result.IsOpen = true;
            return false;
        }
        finally
        {
            _transfer.Dispose();
            _transfer = null;
            TransferPanel.Visibility = Visibility.Collapsed;
            UpdateCommands();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _transfer?.Cancel();

    async void OpenFolder_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchFolderPathAsync(SaveFolder);

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Load();
    async void More_Click(object sender, RoutedEventArgs e) => await Load(older: true);
}

/// <param name="Id">null = all photos.</param>
public sealed record AlbumItem(string? Id, string Label);

public sealed partial class PhotoRow(PhonePhoto photo) : INotifyPropertyChanged
{
    BitmapImage? _thumb;

    public PhonePhoto Photo => photo;
    public bool IsVideo => photo.Video;
    public string Duration => photo.Duration is { } d ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss")) : "";
    public string Label => $"{photo.Name ?? "Photo"}, {photo.Date.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)}";

    public BitmapImage? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
