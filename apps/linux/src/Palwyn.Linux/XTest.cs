using System.Runtime.InteropServices;

namespace Palwyn.Linux;

/// <summary>
/// The phone's mouse and keyboard on an X11 session, through the XTest extension (libXtst, part of every X11
/// desktop). On Wayland, X11 input only reaches X11 windows, so it isn't offered there: that needs the desktop's
/// RemoteDesktop portal, which Palwyn doesn't use yet.
/// </summary>
// ponytail: Wayland remote input (xdg-desktop-portal RemoteDesktop, one consent dialog) is the upgrade path.
public static class XTest
{
    const string X11 = "libX11.so.6", Xtst = "libXtst.so.6";

    [DllImport(X11)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] static extern int XFlush(IntPtr display);
    [DllImport(X11)] static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport(X11)] static extern nuint XKeycodeToKeysym(IntPtr display, byte keycode, int index);
    [DllImport(Xtst)] static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport(Xtst)] static extern int XTestFakeRelativeMotionEvent(IntPtr display, int dx, int dy, nuint delay);
    [DllImport(Xtst)] static extern int XTestFakeButtonEvent(IntPtr display, uint button, int press, nuint delay);
    [DllImport(Xtst)] static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int press, nuint delay);

    static readonly Lock Gate = new();
    static IntPtr _display;
    static bool? _available;
    static int _scroll; // wheel units not yet turned into a click (120 per notch, as on Windows)

    /// <summary>Why the phone can't use the mouse and keyboard here, or null when it can.</summary>
    public static string? Problem => Available ? null
        : Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
            ? "On Wayland the phone can't use this PC's mouse and keyboard yet; sign in with an X11 session (\"Ubuntu on Xorg\", \"Plasma (X11)\") for that."
            : "The phone can't use the mouse and keyboard here: this isn't an X11 desktop with the XTest extension.";

    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                if (_available is bool known) return known;
                _available = false;
                if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
                    || Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return false;
                try
                {
                    _display = XOpenDisplay(IntPtr.Zero);
                    _available = _display != IntPtr.Zero && XTestQueryExtension(_display, out _, out _, out _, out _) != 0;
                }
                catch (DllNotFoundException) { }
                return _available.Value;
            }
        }
    }

    public static void Move(int dx, int dy) => Do(d => XTestFakeRelativeMotionEvent(d, dx, dy, 0));

    /// <param name="button">"left", "middle" or "right".</param>
    /// <param name="action">"click", "down" or "up".</param>
    public static void Button(string button, string action) => Do(d =>
    {
        uint b = button switch { "middle" => 2u, "right" => 3u, _ => 1u };
        if (action != "up") XTestFakeButtonEvent(d, b, 1, 0);
        if (action != "down") XTestFakeButtonEvent(d, b, 0, 0);
    });

    /// <summary>Windows wheel units (120 a notch, positive = up); X11 scrolls with buttons 4 (up) and 5 (down).</summary>
    public static void Scroll(int dy) => Do(d =>
    {
        _scroll += dy;
        while (Math.Abs(_scroll) >= 120)
        {
            uint b = _scroll > 0 ? 4u : 5u;
            XTestFakeButtonEvent(d, b, 1, 0);
            XTestFakeButtonEvent(d, b, 0, 0);
            _scroll -= Math.Sign(_scroll) * 120;
        }
    });

    /// <summary>The phone's REMOTE_KEY names as X keysyms.</summary>
    public static readonly Dictionary<string, uint> Keys = new()
    {
        ["enter"] = 0xff0d, ["backspace"] = 0xff08, ["delete"] = 0xffff, ["tab"] = 0xff09, ["escape"] = 0xff1b,
        ["left"] = 0xff51, ["up"] = 0xff52, ["right"] = 0xff53, ["down"] = 0xff54, ["home"] = 0xff50, ["end"] = 0xff57,
        ["pageUp"] = 0xff55, ["pageDown"] = 0xff56, ["f5"] = 0xffc2,
    };

    public static void Key(string name) => Do(d => Press(d, Keys[name]));

    /// <summary>Types text. Characters the keyboard layout can't produce (with or without Shift) are skipped.</summary>
    /// <returns>How many were skipped.</returns>
    public static int Type(string text)
    {
        int skipped = 0;
        Do(d =>
        {
            foreach (var rune in text.EnumerateRunes())
                if (!Press(d, KeysymFor(rune))) skipped++;
        });
        return skipped;
    }

    /// <summary>X keysyms: Latin-1 is the code point itself, newline and tab are keys, the rest is 0x01000000 + code point.</summary>
    public static uint KeysymFor(System.Text.Rune rune) => rune.Value switch
    {
        '\n' or '\r' => 0xff0d,
        '\t' => 0xff09,
        >= 0x20 and <= 0x7e or >= 0xa0 and <= 0xff => (uint)rune.Value,
        _ => 0x01000000u + (uint)rune.Value,
    };

    static bool Press(IntPtr d, uint keysym)
    {
        byte code = XKeysymToKeycode(d, keysym);
        if (code == 0) return false;
        bool shift = XKeycodeToKeysym(d, code, 0) != keysym;
        if (shift && XKeycodeToKeysym(d, code, 1) != keysym) return false; // needs AltGr or another level: skipped
        byte shiftCode = shift ? XKeysymToKeycode(d, 0xffe1) : (byte)0; // Shift_L
        if (shift) XTestFakeKeyEvent(d, shiftCode, 1, 0);
        XTestFakeKeyEvent(d, code, 1, 0);
        XTestFakeKeyEvent(d, code, 0, 0);
        if (shift) XTestFakeKeyEvent(d, shiftCode, 0, 0);
        return true;
    }

    static void Do(Action<IntPtr> act)
    {
        if (!Available) return;
        lock (Gate)
        {
            act(_display);
            XFlush(_display);
        }
    }
}
