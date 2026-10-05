using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Palwyn.Core;

namespace Palwyn.Linux;

/// <summary>Photos, Send to phone (Quick Drop) and Contacts: the Windows app's hub requests and wording.</summary>
public sealed partial class MainWindow
{
    // ---- Photos ----

    readonly List<PhonePhoto> _photos = [];
    readonly Dictionary<string, Image> _thumbs = [];
    readonly HashSet<string> _selected = [];
    readonly SemaphoreSlim _thumbSlots = new(4); // previews load 4 at a time, as on Windows
    IReadOnlyList<PhotoAlbum>? _albums;
    string? _album;
    bool _morePhotos;
    string? _photoStatus;

    async Task LoadPhotosAsync(bool more = false)
    {
        if (!Link.IsConnected) return;
        try
        {
            _albums ??= await Link.AlbumsAsync();
            if (!more) { _photos.Clear(); _selected.Clear(); }
            var page = await Link.PhotosAsync(60, more ? _photos.LastOrDefault()?.Id : null, _album);
            _photos.AddRange(page);
            _morePhotos = page.Count == 60;
            _loadError = null;
            foreach (var p in page) _ = LoadThumbAsync(p);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _loadError = Failure(e);
        }
        RefreshIf("photos");
    }

    async Task LoadThumbAsync(PhonePhoto p)
    {
        if (_thumbs.TryGetValue(p.Id, out var known) && known.Source is not null) return;
        var image = _thumbs[p.Id] = known ?? new Image { Width = 132, Height = 132, Stretch = Stretch.UniformToFill };
        await _thumbSlots.WaitAsync();
        try { image.Source = new Bitmap(new MemoryStream(await Link.PhotoThumbnailAsync(p.Id, p.Video))); }
        catch (Exception e) when (e is not OutOfMemoryException) { } // stays blank; the photo can still be saved
        finally { _thumbSlots.Release(); }
    }

    Control PhotosPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(Heading("Photos"));
        if (NotConnected("your photos") is { } notice) return Add(page, notice);
        if (_loadError is { } error) page.Children.Add(Text(error, opacity: 0.8));

        var bar = new WrapPanel();
        if (_albums is { Count: > 0 } albums)
        {
            var pick = new ComboBox { ItemsSource = albums.Select(a => $"{a.Name} ({a.Count})").Prepend("All photos").ToList(), Margin = new Thickness(0, 0, 8, 8) };
            pick.SelectedIndex = _album is null ? 0 : albums.ToList().FindIndex(a => a.Id == _album) + 1;
            pick.SelectionChanged += (_, _) =>
            {
                var chosen = pick.SelectedIndex <= 0 ? null : albums[pick.SelectedIndex - 1].Id;
                if (chosen == _album) return;
                _album = chosen;
                _ = LoadPhotosAsync();
            };
            bar.Children.Add(pick);
        }
        var save = new Button { Content = _selected.Count == 0 ? "Select photos to save" : $"Save {_selected.Count} to Pictures", IsEnabled = _selected.Count > 0, Classes = { "accent" }, Margin = new Thickness(0, 0, 8, 8) };
        save.Click += (_, _) => _ = SavePhotosAsync();
        bar.Children.Add(save);
        var camera = new Button { Content = "Take a photo with the phone", Margin = new Thickness(0, 0, 8, 8) };
        camera.Click += async (_, _) =>
        {
            try
            {
                await Link.RequestCameraAsync();
                _photoStatus = "Tap the notification on your phone and take the photo. It's saved in Pictures/Palwyn.";
            }
            catch (Exception e) when (e is not OutOfMemoryException) { _photoStatus = Failure(e); }
            Refresh();
        };
        bar.Children.Add(camera);
        var open = new Button { Content = "Open Pictures/Palwyn", Margin = new Thickness(0, 0, 8, 8) };
        open.Click += (_, _) => Paths.Open(Paths.Pictures);
        bar.Children.Add(open);
        page.Children.Add(bar);
        if (_photoStatus is { } status) page.Children.Add(Text(status, 13, opacity: 0.8));

        var grid = new WrapPanel();
        foreach (var p in _photos)
        {
            var thumb = _thumbs.TryGetValue(p.Id, out var t) ? Keep(t) : _thumbs[p.Id] = new Image { Width = 132, Height = 132, Stretch = Stretch.UniformToFill };
            var cell = new Grid { Children = { thumb } };
            if (p.Video)
                cell.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#99000000")), Padding = new Thickness(4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new TextBlock { Text = p.Duration is { } d ? $"▶ {(int)d.TotalMinutes}:{d.Seconds:00}" : "▶", Foreground = Brushes.White, FontSize = 11 },
                });
            var tile = new Button
            {
                Content = cell, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(3),
                BorderBrush = _selected.Contains(p.Id) ? Mine : Brushes.Transparent,
            };
            ToolTip.SetTip(tile, $"{p.Name} · {p.Date.ToLocalTime():d MMM yyyy}");
            tile.Click += (_, _) =>
            {
                if (!_selected.Remove(p.Id)) _selected.Add(p.Id);
                Refresh();
            };
            grid.Children.Add(tile);
        }
        page.Children.Add(grid);
        if (_photos.Count == 0) page.Children.Add(Text(_albums is null ? "Loading…" : "No photos here.", opacity: 0.7));
        if (_morePhotos)
        {
            var more = new Button { Content = "Show more" };
            more.Click += (_, _) => _ = LoadPhotosAsync(more: true);
            page.Children.Add(more);
        }
        return page;
    }

    async Task SavePhotosAsync()
    {
        var chosen = _photos.Where(p => _selected.Contains(p.Id)).ToList();
        var taken = new HashSet<string>();
        int done = 0;
        try
        {
            Directory.CreateDirectory(Paths.Pictures);
            foreach (var p in chosen)
            {
                _photoStatus = $"Saving {done + 1} of {chosen.Count}…";
                Refresh();
                var path = PhonePhoto.UniquePath(Paths.Pictures, PhonePhoto.SafeFileName(p.Name, p.Mime), taken);
                await Link.DownloadAsync(p.Video ? "video" : "photo", p.Id, path, null, CancellationToken.None);
                done++;
            }
            _selected.Clear();
            _photoStatus = $"Saved {done} to {Paths.Pictures}.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _photoStatus = $"Saved {done} of {chosen.Count}. {Failure(e)}";
        }
        Refresh();
    }

    // ---- Send to phone ----

    readonly TextBox _sendText = new() { PlaceholderText = "Text or a link", MinWidth = 320 };
    readonly List<string> _pending = [];
    string? _sendStatus;
    bool _busy;

    /// <summary>Files from "palwyn-linux send", the file picker or a drop: listed, then sent when the phone is there.</summary>
    public void QueueFiles(IEnumerable<string> paths)
    {
        _pending.AddRange(paths.Where(p => File.Exists(p) || Directory.Exists(p)));
        if (Link.IsConnected) _ = SendPendingAsync();
        else Refresh();
    }

    async Task SendPendingAsync()
    {
        if (_busy || _pending.Count == 0) return;
        _busy = true;
        var paths = _pending.ToList();
        _pending.Clear();
        // Folders keep their structure under Download/Palwyn on the phone (at most 1,000 files per folder).
        var files = paths.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Take(1000).Select(f => (File: f, Folder: (string?)DropFolders.RelativeFolder(p, f)))
            : [(File: p, Folder: null)]).ToList();
        int done = 0;
        try
        {
            foreach (var (file, folder) in files)
            {
                _sendStatus = $"Sending {done + 1} of {files.Count}: {Path.GetFileName(file)}";
                RefreshIf("send");
                await using var stream = File.OpenRead(file);
                await Link.SendFileAsync(Path.GetFileName(file), MimeFor(file), stream, stream.Length, null, CancellationToken.None, folder);
                done++;
            }
            _sendStatus = files.Count == 1 ? $"Sent {Path.GetFileName(files[0].File)} to Download/Palwyn on your phone." : $"Sent {done} files to Download/Palwyn on your phone.";
            App.Host.OnActivity("", files.Count == 1 ? $"Sent {Path.GetFileName(files[0].File)}" : $"Sent {done} files");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _sendStatus = $"Sent {done} of {files.Count}. {Failure(e)}";
            _pending.AddRange(files.Skip(done).Select(f => f.File)); // try the rest again
        }
        _busy = false;
        RefreshIf("send");
    }

    /// <summary>The phone uses the type to pick an app for the file; anything unknown is plain data.</summary>
    static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp",
        ".heic" => "image/heic", ".mp4" => "video/mp4", ".mkv" => "video/x-matroska", ".webm" => "video/webm",
        ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".pdf" => "application/pdf", ".txt" => "text/plain",
        ".zip" => "application/zip", ".apk" => "application/vnd.android.package-archive",
        _ => "application/octet-stream",
    };

    Control SendPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(Heading("Send to phone"));
        page.Children.Add(Text("Files go to Download/Palwyn on your phone. From a terminal or a file manager's \"Run\" action: palwyn-linux send FILE…", 13, opacity: 0.8));
        if (NotConnected("send to it") is { } notice) page.Children.Add(notice);

        var drop = new Border
        {
            Background = Tile, CornerRadius = new CornerRadius(12), Padding = new Thickness(24), MinHeight = 120,
            Child = new StackPanel
            {
                Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                Children = { Text("Drop files or folders here", 16, FontWeight.SemiBold), Text(_busy ? "Sending…" : "Or choose them:", 13, opacity: 0.7) },
            },
        };
        DragDrop.SetAllowDrop(drop, true);
        drop.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is { } items) QueueFiles(items.Select(i => i.TryGetLocalPath()).OfType<string>());
        });
        page.Children.Add(drop);

        var buttons = new WrapPanel();
        var files = new Button { Content = "Choose files…", Margin = new Thickness(0, 0, 8, 8) };
        files.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "Send to phone" });
            QueueFiles(picked.Select(f => f.TryGetLocalPath()).OfType<string>());
        };
        var folder = new Button { Content = "Choose a folder…", Margin = new Thickness(0, 0, 8, 8) };
        folder.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Send a folder to phone" });
            QueueFiles(picked.Select(f => f.TryGetLocalPath()).OfType<string>());
        };
        var clip = new Button { Content = "Send clipboard", Margin = new Thickness(0, 0, 8, 8) };
        clip.Click += async (_, _) =>
        {
            try { _sendStatus = App.Host.Clipboard is { } c ? await c.SendNowAsync() : "The clipboard isn't available"; }
            catch (Exception e) when (e is not OutOfMemoryException) { _sendStatus = Failure(e); }
            Refresh();
        };
        buttons.Children.Add(files);
        buttons.Children.Add(folder);
        buttons.Children.Add(clip);
        page.Children.Add(buttons);

        var sendText = new Button { Content = "Send text", Classes = { "accent" } };
        sendText.Click += async (_, _) =>
        {
            if ((_sendText.Text ?? "").Trim() is not { Length: > 0 } text) return;
            try
            {
                await Link.SendTextAsync(text);
                _sendText.Text = "";
                _sendStatus = "Sent. It shows as a notification on your phone.";
            }
            catch (Exception e) when (e is not OutOfMemoryException) { _sendStatus = Failure(e); }
            Refresh();
        };
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Keep(_sendText), sendText } });

        if (_sendStatus is { } status) page.Children.Add(Text(status, 13, opacity: 0.8));
        if (_pending.Count > 0)
        {
            page.Children.Add(Text($"Waiting to send: {string.Join(", ", _pending.Select(Path.GetFileName))}", 13));
            var retry = new Button { Content = "Send now" };
            retry.Click += (_, _) => _ = SendPendingAsync();
            page.Children.Add(retry);
        }
        return page;
    }

    // ---- Contacts ----

    List<PhoneContact>? _contacts;
    readonly TextBox _contactSearch = new() { PlaceholderText = "Search contacts", MinWidth = 300 };
    PhoneContact? _editing;
    readonly TextBox _editName = new() { PlaceholderText = "Name", MinWidth = 320 };
    readonly TextBox _editNumbers = new() { PlaceholderText = "Phone numbers, one per line", AcceptsReturn = true, MinHeight = 70, MinWidth = 320 };
    readonly TextBox _editEmails = new() { PlaceholderText = "Email addresses, one per line", AcceptsReturn = true, MinHeight = 50, MinWidth = 320 };
    bool _confirmDelete;
    string? _contactStatus;

    async Task LoadContactsAsync()
    {
        if (!Link.IsConnected) return;
        try
        {
            var all = new List<PhoneContact>();
            bool more = true;
            while (more && all.Count < 5000) // ponytail: everything in memory, fine for a phone's address book
            {
                (var page, more) = await Link.ContactsAsync(200, all.Count);
                all.AddRange(page);
                if (page.Count == 0) break;
            }
            _contacts = [.. all.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
            _loadError = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _loadError = Failure(e);
        }
        RefreshIf("contacts");
    }

    void Edit(PhoneContact? contact)
    {
        _editing = contact ?? new PhoneContact("", "", [], []);
        _editName.Text = _editing.Name;
        _editNumbers.Text = string.Join("\n", _editing.Numbers.Select(n => n.Number));
        _editEmails.Text = string.Join("\n", _editing.Emails);
        _confirmDelete = false;
        _contactStatus = null;
        Refresh();
    }

    Control ContactsPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(Heading("Contacts"));
        if (NotConnected("your contacts") is { } notice) return Add(page, notice);
        if (_editing is { } editing) return ContactEditor(page, editing);
        if (_loadError is { } error) page.Children.Add(Text(error, opacity: 0.8));
        var add = new Button { Content = "Add a contact", Classes = { "accent" } };
        add.Click += (_, _) => Edit(null);
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Keep(_contactSearch), add } });
        if (_contactStatus is { } status) page.Children.Add(Text(status, 13, opacity: 0.8));
        if (_contacts is null) return Add(page, Text("Loading…", opacity: 0.7));
        var query = (_contactSearch.Text ?? "").Trim();
        var shown = _contacts.Where(c => c.Matches(query)).Take(300).ToList();
        if (shown.Count == 0) page.Children.Add(Text(query.Length > 0 ? "No contact matches." : "No contacts yet.", opacity: 0.7));
        foreach (var c in shown)
        {
            var row = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel { Children = { Text(c.Name), Text(string.Join(" · ", c.Numbers.Select(n => n.Number)), 12, opacity: 0.7) } },
            };
            row.Click += (_, _) => Edit(c);
            page.Children.Add(row);
        }
        return page;
    }

    Control ContactEditor(StackPanel page, PhoneContact editing)
    {
        page.Children.Add(Text(editing.Id.Length == 0 ? "New contact" : "Edit contact", 16, FontWeight.SemiBold));
        page.Children.Add(Keep(_editName));
        page.Children.Add(Keep(_editNumbers));
        page.Children.Add(Keep(_editEmails));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "Save", Classes = { "accent" } };
        save.Click += (_, _) => _ = SaveContactAsync(editing);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => { _editing = null; Refresh(); };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        if (editing.Id.Length > 0)
        {
            var delete = new Button { Content = _confirmDelete ? "Delete? Click again" : "Delete" };
            delete.Click += (_, _) => _ = DeleteContactAsync(editing);
            buttons.Children.Add(delete);
        }
        page.Children.Add(buttons);
        if (_contactStatus is { } status) page.Children.Add(Text(status, 13, opacity: 0.8));
        return page;
    }

    static string[] Lines(string? text) => (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    async Task SaveContactAsync(PhoneContact editing)
    {
        var name = (_editName.Text ?? "").Trim();
        if (name.Length == 0)
        {
            _contactStatus = "Give the contact a name.";
            Refresh();
            return;
        }
        // Numbers that were there keep their type (home, work…); new ones are mobile, as on Windows.
        var numbers = Lines(_editNumbers.Text).Select(n => editing.Numbers.FirstOrDefault(o => PhoneContact.SameNumber(o.Number, n)) is { } old
            ? old with { Number = n } : new ContactNumber(n, "mobile")).ToList();
        var contact = editing with { Name = name, Numbers = numbers, Emails = Lines(_editEmails.Text) };
        try
        {
            await Link.SaveContactAsync(contact);
            _editing = null;
            _contactStatus = $"Saved {name}.";
            _ = LoadContactsAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _contactStatus = $"Not saved. {Failure(e)}";
        }
        Refresh();
    }

    async Task DeleteContactAsync(PhoneContact editing)
    {
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            Refresh();
            return;
        }
        try
        {
            await Link.DeleteContactAsync(editing.Id);
            _editing = null;
            _contactStatus = $"Deleted {editing.Name}.";
            _ = LoadContactsAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _contactStatus = $"Not deleted. {Failure(e)}";
        }
        Refresh();
    }
}
