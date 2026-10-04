using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Palwyn.App.Emergency;
using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;

namespace Palwyn.App;

/// <summary>A message for the phone screen window, with a Retry button.</summary>
sealed class ScreenSourceException(string message) : Exception(message);

/// <summary>Where the phone screen window's frames come from and its controls go: the paired link (Android asks the
/// user on the phone) or the emergency helper over adb (no prompt).</summary>
interface IScreenSource : IAsyncDisposable
{
    string Title { get; }
    bool CanControl { get; }
    /// <summary>Shown while starting.</summary>
    string Starting { get; }
    /// <summary>Shown when frames stop without the window being closed.</summary>
    string Ended { get; }
    /// <summary>CanControl changed.</summary>
    event Action? Changed;
    /// <summary>The source can't work any more (the phone disconnected); the window shows the message.</summary>
    event Action<string>? Lost;

    /// <summary>Starts sharing. True when frames can be read now; false when they start later (the link: once the user
    /// allows it on the phone, see ScreenWindow.OnState). Throws ScreenSourceException with a message for the user.</summary>
    Task<bool> BeginAsync();
    IAsyncEnumerable<byte[]> FramesAsync(CancellationToken ct);
    Task SendAsync(string type, JsonObject payload);
}

/// <summary>Screen sharing over the paired link (docs/protocol.md, SCREEN_*).</summary>
sealed class LinkScreenSource : IScreenSource
{
    public string Title => "Phone screen";
    public bool CanControl => App.Current.Link.Capabilities.Contains("screen.control");
    public string Starting => "On your phone, tap the Palwyn notification, then Start. To skip this next time, turn on \"Keep screen sharing ready\" in Palwyn on the phone.";
    public string Ended => "Screen sharing ended.";
    public event Action? Changed;
    public event Action<string>? Lost;

    public LinkScreenSource()
    {
        App.Current.Link.DashboardChanged += OnDashboard; // capabilities: control comes and goes with sharing
        App.Current.StatusChanged += OnStatus;
    }

    void OnDashboard() => Changed?.Invoke();

    void OnStatus()
    {
        if (!App.Current.Link.IsConnected) Lost?.Invoke("Your phone disconnected.");
    }

    public async Task<bool> BeginAsync()
    {
        try { await App.Current.Link.RequestScreenAsync(); }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            throw new ScreenSourceException(e is PhoneErrorException ? "Update Palwyn on your phone to see its screen here." : "Your phone isn't connected.");
        }
        return false;
    }

    public async IAsyncEnumerable<byte[]> FramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var link = await App.Current.Link.OpenScreenAsync(ct);
        while (await link.ReadBlobAsync(ct) is { } jpeg)
        {
            if (jpeg.Length > 0 && jpeg[0] == (byte)'{') yield break; // ERROR instead of frames: not shared with this PC
            yield return jpeg;
        }
    }

    public Task SendAsync(string type, JsonObject payload) => App.Current.Link.ScreenControlAsync(type, payload);

    public ValueTask DisposeAsync()
    {
        App.Current.Link.DashboardChanged -= OnDashboard;
        App.Current.StatusChanged -= OnStatus;
        return ValueTask.CompletedTask;
    }
}

/// <summary>The emergency screen: the helper over adb, no prompt on the phone, always controllable.</summary>
sealed class EmergencyScreenSource(AdbDevice device) : IScreenSource
{
    EmergencySession? _session;

    public string Title => "Phone screen (emergency)";
    public bool CanControl => true;
    public string Starting => "Opening the phone's screen…";
    public string Ended => "Disconnected.";
    public event Action? Changed { add { } remove { } }
    public event Action<string>? Lost { add { } remove { } }

    public async Task<bool> BeginAsync()
    {
        if (Interlocked.Exchange(ref _session, null) is { } old) await old.DisposeAsync(); // Retry
        try { _session = await EmergencySession.StartAsync(device, CancellationToken.None); }
        catch (EmergencyException e) { throw new ScreenSourceException(e.Message); }
        return true;
    }

    public IAsyncEnumerable<byte[]> FramesAsync(CancellationToken ct) =>
        _session?.FramesAsync(ct) ?? AsyncEnumerable.Empty<byte[]>();

    public Task SendAsync(string type, JsonObject payload) => _session?.SendAsync(type, payload) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _session, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
}
