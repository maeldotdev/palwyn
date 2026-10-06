using System.Text.Json.Nodes;

namespace Palwyn.Core.Emergency;

/// <summary>A message for the phone screen window, with a Retry button.</summary>
public sealed class ScreenSourceException(string message) : Exception(message);

/// <summary>Where the phone screen window's frames come from and its controls go: the paired link (Android asks the
/// user on the phone; each PC app has its own, on its link hub) or the emergency helper over adb (no prompt).</summary>
public interface IScreenSource : IAsyncDisposable
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
    /// allows it on the phone, SCREEN_STATE "started"). Throws ScreenSourceException with a message for the user.</summary>
    Task<bool> BeginAsync();
    IAsyncEnumerable<byte[]> FramesAsync(CancellationToken ct);
    Task SendAsync(string type, JsonObject payload);
}

/// <summary>The emergency screen: the helper over adb, no prompt on the phone, always controllable.</summary>
public sealed class EmergencyScreenSource(AdbDevice device) : IScreenSource
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
