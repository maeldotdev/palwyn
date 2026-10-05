using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Palwyn.Linux;

/// <summary>
/// Clipboard between this PC and the phone, as on Windows. Phone to PC: whenever the user sends it from the phone.
/// PC to phone: every text copy, only with the setting on (off by default), skipping copies a password manager marks
/// private (KDE's x-kde-passwordManagerHint). Watching the clipboard from the background works on X11 and, through
/// wl-paste (wl-clipboard), on Wayland desktops with the data-control protocol (KDE, wlroots); GNOME on Wayland allows
/// no app to do it, so there only "Send clipboard" works.
/// </summary>
public sealed class ClipboardSync(Settings settings, Func<IClipboard?> clipboard)
{
    const int MaxImage = 20 * 1024 * 1024;
    const string PasswordHint = "x-kde-passwordManagerHint";
    readonly ClipboardEcho _echo = new();
    DispatcherTimer? _poll;
    Process? _watch;

    static bool Wayland => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";

    /// <summary>Why copies can't go to the phone automatically here, or null when they can.</summary>
    public string? AutoProblem { get; private set; }

    /// <summary>Starts or stops watching to match the setting (and the connection: only while connected).</summary>
    public void Update(bool connected)
    {
        bool on = settings.ClipboardToPhone && connected && OperatingSystem.IsLinux();
        if (!on)
        {
            _poll?.Stop();
            _poll = null;
            try { _watch?.Kill(); } catch (InvalidOperationException) { }
            _watch = null;
            return;
        }
        if (_poll is not null || _watch is not null) return;
        if (Wayland) Watch();
        else Poll();
    }

    /// <summary>X11: there's no change event to subscribe to without a toolkit hook, so look every second and a half.</summary>
    void Poll()
    {
        bool first = true;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += async (_, _) =>
        {
            if (clipboard() is not { } cb) return;
            try
            {
                var formats = await cb.GetDataFormatsAsync();
                if (formats.Any(f => f.ToString().Contains(PasswordHint))) return;
                var text = await cb.TryGetTextAsync();
                if (first) _echo.Received(text ?? ""); // what was there before isn't a new copy
                else if (_echo.ShouldSend(text)) await SendAsync(text!);
                first = false;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { } // another app holds the clipboard: try next time
        };
        _poll.Start();
    }

    /// <summary>Wayland: wl-paste runs the shell snippet on every copy; each copy ends with a NUL on its output.</summary>
    void Watch()
    {
        Process p;
        try
        {
            p = Process.Start(new ProcessStartInfo("wl-paste", ["--no-newline", "--type", "text", "--watch", "sh", "-c",
                $"wl-paste --list-types | grep -q {PasswordHint} || cat; printf '\\0'"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
        }
        catch (Win32Exception)
        {
            AutoProblem = "On Wayland this needs wl-clipboard (package wl-clipboard). Until then, use Send clipboard.";
            return;
        }
        _watch = p;
        AutoProblem = null;
        _ = Task.Run(async () =>
        {
            var copy = new StringBuilder();
            var buffer = new char[4096];
            bool first = true;
            int n;
            while ((n = await p.StandardOutput.ReadAsync(buffer)) > 0)
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i] != '\0')
                    {
                        copy.Append(buffer[i]);
                        continue;
                    }
                    var text = copy.ToString();
                    copy.Clear();
                    if (first) _echo.Received(text); // wl-paste reports the current copy at start
                    else if (_echo.ShouldSend(text)) await SendAsync(text);
                    first = false;
                }
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 && ReferenceEquals(_watch, p))
            {
                // GNOME has no data-control protocol: wl-paste --watch refuses to start.
                AutoProblem = "This desktop doesn't let apps watch the clipboard (GNOME on Wayland does this). Use Send clipboard instead.";
                Linux.Log.Info("Clipboard watch unavailable on this desktop");
            }
        });
    }

    async Task SendAsync(string text)
    {
        try
        {
            await App.Current.Link.SendClipboardAsync(text);
            Linux.Log.Info($"Clipboard to phone: {text.Length} chars"); // never the text itself
        }
        catch (Exception e) when (e is not OutOfMemoryException) { } // the phone just dropped: this copy isn't sent
    }

    /// <summary>"Send clipboard": this copy only, whether or not sync is on. Returns what to tell the user.</summary>
    public async Task<string> SendNowAsync()
    {
        if (clipboard() is not { } cb) return "The clipboard isn't available";
        var formats = await cb.GetDataFormatsAsync();
        if (formats.Any(f => f.ToString().Contains(PasswordHint))) return "That copy is marked private, so it wasn't sent";
        if (await cb.TryGetTextAsync() is { Length: > 0 } text)
        {
            if (text.Length > 50_000) return "That's too long to send (over 50,000 characters)";
            _echo.Received(text);
            await App.Current.Link.SendClipboardAsync(text);
            return "Sent. Paste it on your phone.";
        }
        if (await cb.TryGetBitmapAsync() is not { } bitmap) return "Only text and images can be sent. Use Send files for the rest.";
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        if (png.Length > MaxImage) return "That image is too big to send (over 20 MB)";
        await App.Current.Link.SendClipboardImageAsync(png.ToArray());
        return "Sent. Paste it on your phone.";
    }

    /// <summary>Text the phone sent: onto this PC's clipboard, not echoed back.</summary>
    public async Task ReceivedAsync(string text)
    {
        _echo.Received(text);
        if (clipboard() is { } cb) await cb.SetTextAsync(text);
    }

    /// <summary>An image copied on the phone (already saved to <paramref name="path"/>): onto this PC's clipboard.</summary>
    public async Task<bool> ReceivedImageAsync(string path)
    {
        if (clipboard() is not { } cb) return false;
        await cb.SetBitmapAsync(new Bitmap(path));
        return true;
    }
}

/// <summary>Which copies are worth sending: new text only, never one that just came from the phone, so nothing bounces.</summary>
public sealed class ClipboardEcho
{
    string? _last;

    /// <summary>True for a new copy to send (and remembers it); false when empty, too long or the same as the last one.</summary>
    public bool ShouldSend(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 50_000 || text == _last) return false;
        _last = text;
        return true;
    }

    /// <summary>Text that arrived from the phone, or was already on the clipboard: not a copy to send.</summary>
    public void Received(string text) => _last = text;
}
