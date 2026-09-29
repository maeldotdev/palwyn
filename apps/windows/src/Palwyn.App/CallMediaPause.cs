using Palwyn.Core;
using Windows.Media.Control;

namespace Palwyn.App;

/// <summary>
/// Pauses what's playing on this PC while the phone rings or a call is on, and resumes it afterwards: only
/// the players Palwyn paused, and only if they're still paused (the user may have stopped them since).
/// Works with any app that shows in Windows' media controls (Spotify, browsers, Media Player).
/// </summary>
static class CallMediaPause
{
    static readonly List<GlobalSystemMediaTransportControlsSession> Paused = [];
    static bool _inCall;

    /// <param name="call">null = the link dropped: the call's state is unknown, so treat it as over.</param>
    public static async void Update(PhoneCall? call)
    {
        bool inCall = call is { State: not CallState.Ended };
        if (inCall == _inCall) return;
        _inCall = inCall;
        try
        {
            if (inCall)
            {
                if (!AppSettings.PauseMediaDuringCalls) return;
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                foreach (var session in manager.GetSessions())
                    if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        && await session.TryPauseAsync())
                        Paused.Add(session);
                if (Paused.Count > 0) Log.Info($"Paused {Paused.Count} player(s) for a call");
            }
            else
            {
                foreach (var session in Paused)
                    if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                        await session.TryPlayAsync();
                if (Paused.Count > 0) Log.Info("Resumed players after the call");
                Paused.Clear();
            }
        }
        catch (Exception e) // a player that closed mid-call throws; the call itself must never be affected
        {
            Log.Info($"Media pause failed: {e.GetType().Name}");
            Paused.Clear();
        }
    }
}
