namespace Palwyn.App;

/// <summary>
/// While the phone is connected and the setting is on, the PC doesn't sleep, turn its screen off, start the
/// screensaver or lock itself on idle: one Windows power request, cleared the moment either stops being true.
/// </summary>
static class KeepPcAwake
{
    static IntPtr _request;
    static bool _held;

    /// <summary>Call on every status or setting change; cheap when nothing changes. UI thread.</summary>
    public static void Update(bool phoneConnected)
    {
        bool want = phoneConnected && AppSettings.KeepPcAwake;
        if (want == _held) return;
        if (_request == IntPtr.Zero)
        {
            var reason = new Win32.REASON_CONTEXT
            {
                Flags = Win32.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = "Palwyn: keeping this PC awake while your phone is connected",
            };
            _request = Win32.PowerCreateRequest(ref reason);
            if (_request == new IntPtr(-1)) // INVALID_HANDLE_VALUE
            {
                _request = IntPtr.Zero;
                Log.Info("Keep awake: power request unavailable");
                return;
            }
        }
        if (want)
        {
            Win32.PowerSetRequest(_request, Win32.PowerRequestDisplayRequired);
            Win32.PowerSetRequest(_request, Win32.PowerRequestSystemRequired);
        }
        else
        {
            Win32.PowerClearRequest(_request, Win32.PowerRequestDisplayRequired);
            Win32.PowerClearRequest(_request, Win32.PowerRequestSystemRequired);
        }
        _held = want;
        Log.Info(want ? "Keep awake: on (phone connected)" : "Keep awake: off");
    }
}
