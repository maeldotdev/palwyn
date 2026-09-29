using System.Runtime.InteropServices;
using Palwyn.Core;

namespace Palwyn.App;

static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    public static PixelRect ToPixelRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public static (PixelRect WorkArea, double Scale) MonitorFor(PixelRect anchor)
    {
        var r = new RECT { Left = anchor.X, Top = anchor.Y, Right = anchor.Right, Bottom = anchor.Bottom };
        var monitor = MonitorFromRect(ref r, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpi, out _);
        return (ToPixelRect(info.rcWork), dpi / 96.0);
    }

    // Power requests (kernel32): what video players use to keep the screen on. The reason string shows in
    // `powercfg /requests`, so the user can see why the PC stays awake.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct REASON_CONTEXT { public uint Version; public uint Flags; public string SimpleReasonString; }

    public const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 1;
    public const int PowerRequestDisplayRequired = 0, PowerRequestSystemRequired = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PowerClearRequest(IntPtr request, int type);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const int GWLP_WNDPROC = -4;
    public const uint WM_SETTINGCHANGE = 0x001A;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    public static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Shows without taking focus. AppWindow.Show(false) shows a never-shown window minimized.</summary>
    public static void ShowNoActivate(IntPtr hWnd) => ShowWindow(hWnd, 4 /* SW_SHOWNOACTIVATE */);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int cmd);

    public static void MakeTopmost(IntPtr hWnd) =>
        SetWindowPos(hWnd, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /* NOSIZE|NOMOVE|NOACTIVATE */);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    /// <summary>The user's Downloads folder, wherever they moved it.</summary>
    public static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0)
        {
            try { return Marshal.PtrToStringUni(path)!; }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    // ---- Classic file drop (WM_DROPFILES) ----

    public const uint WM_DROPFILES = 0x0233;

    /// <summary>
    /// Accepts files dragged from Explorer the classic way. WinUI 3 registers no drop target when the app runs
    /// elevated (seen on this PC: no window had one), so XAML drop events never fire there; this path still works.
    /// Also lets the messages through when the drag comes from a less privileged process.
    /// </summary>
    public static void AcceptFileDrops(IntPtr hWnd)
    {
        foreach (uint msg in new uint[] { WM_DROPFILES, 0x004A /* WM_COPYDATA */, 0x0049 /* WM_COPYGLOBALDATA */ })
            ChangeWindowMessageFilterEx(hWnd, msg, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        DragAcceptFiles(hWnd, true);
    }

    /// <summary>The paths in a WM_DROPFILES drop; releases the drop handle.</summary>
    public static List<string> DroppedFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var sb = new System.Text.StringBuilder((int)DragQueryFileW(hDrop, i, null, 0) + 1);
            DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
            paths.Add(sb.ToString());
        }
        DragFinish(hDrop);
        return paths;
    }

    [DllImport("shell32.dll")]
    static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint DragQueryFileW(IntPtr hDrop, uint index, System.Text.StringBuilder? file, uint size);

    [DllImport("shell32.dll")]
    static extern void DragFinish(IntPtr hDrop);

    [DllImport("user32.dll")]
    static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr info);

    // ---- Synthesized input (the phone's remote) ----

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8,
        MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    public static void Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) =>
        Send(new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } });

    /// <summary>Presses and releases a virtual key; <paramref name="extended"/> for arrows, Home/End, Page Up/Down, Delete.</summary>
    public static void Key(ushort vk, bool extended)
    {
        uint ext = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        Send(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = ext } } },
             new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = ext | KEYEVENTF_KEYUP } } });
    }

    /// <summary>Types text as Unicode key events, independent of the keyboard layout.</summary>
    public static void Type(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\n') // Enter; a Unicode newline does nothing in most apps
            {
                if (inputs.Count > 0) Send([.. inputs]);
                inputs.Clear();
                Key(0x0D, false);
                continue;
            }
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        if (inputs.Count > 0) Send([.. inputs]);
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();
}
