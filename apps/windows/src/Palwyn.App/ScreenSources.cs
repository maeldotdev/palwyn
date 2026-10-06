using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Palwyn.Core.Emergency;
using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Core.Protocol;

namespace Palwyn.App;

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
