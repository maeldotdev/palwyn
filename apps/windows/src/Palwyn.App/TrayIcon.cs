using System.Runtime.InteropServices;
using Microsoft.Win32;
using Palwyn.Core;

namespace Palwyn.App;

/// <summary>
/// Notification-area icon via Shell_NotifyIcon (WinUI has no tray API). Callbacks arrive on the host
/// window's message loop through a window-procedure subclass.
/// </summary>
sealed class TrayIcon : IDisposable
{
    const uint WM_TRAY = 0x8000 + 1; // WM_APP + 1
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    const uint NOTIFYICON_VERSION_4 = 4;
    const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B;
    const uint IconId = 1;

    readonly IntPtr _hwnd;
    readonly Win32.WndProc _proc;
    readonly IntPtr _previousProc;
    readonly uint _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");
    IntPtr _icon;
    string _tip = "Palwyn";
    bool _connected;

    /// <summary>Raised on click, right-click or keyboard select, with the icon's screen rect (or the cursor point).</summary>
    public event Action<PixelRect>? Invoked;

    /// <summary>The Windows clipboard changed (any app copied something).</summary>
    public event Action? ClipboardChanged;
    const uint WM_CLIPBOARDUPDATE = 0x031D;

    public TrayIcon(IntPtr hostHwnd)
    {
        _hwnd = hostHwnd;
        _proc = WndProc;
        _previousProc = Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
        LoadIcon();
        Add();
        AddClipboardFormatListener(_hwnd);
    }

    public void Update(PhoneStatus status)
    {
        _connected = status.State == ConnectionState.Connected;
        _tip = status.State switch
        {
            ConnectionState.Connected => $"Palwyn: {status.PhoneName} connected" + (status.BatteryPercent is int b ? $", {b}%" : ""),
            ConnectionState.Connecting => "Palwyn: connecting",
            ConnectionState.Disconnected => $"Palwyn: {status.PhoneName} not connected",
            ConnectionState.Blocked => $"Palwyn: {status.PhoneName} needs attention",
            _ => "Palwyn: no phone paired",
        };
        LoadIcon();
        var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            Log.Info("Tray icon add failed; will retry when the taskbar is created");
            return;
        }
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    void LoadIcon()
    {
        bool lightTaskbar = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Tray",
            $"tray-{(lightTaskbar ? "light" : "dark")}-{(_connected ? "on" : "off")}.ico");
        int size = Win32.GetSystemMetricsForDpi(49 /* SM_CXSMICON */, Win32.GetDpiForSystem());
        var icon = Win32.LoadImageW(IntPtr.Zero, file, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        if (icon == IntPtr.Zero) { Log.Info($"Tray icon load failed: {file}"); return; }
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _icon = icon;
    }

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt is NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU)
                Invoked?.Invoke(Anchor(wParam));
            return IntPtr.Zero;
        }
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated)
        {
            Log.Info("Taskbar recreated; re-adding tray icon");
            Add();
        }
        else if (msg == Win32.WM_SETTINGCHANGE && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            LoadIcon();
            var data = Data(NIF_ICON);
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        return Win32.CallWindowProcW(_previousProc, hWnd, msg, wParam, lParam);
    }

    PixelRect Anchor(IntPtr wParam)
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _hwnd, uID = IconId };
        if (Shell_NotifyIconGetRect(ref id, out var rect) == 0) return Win32.ToPixelRect(rect);
        long w = wParam.ToInt64(); // v4 callbacks carry the anchor point in wParam
        return new PixelRect((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF), 1, 1);
    }

    NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = _tip,
    };

    public void Dispose()
    {
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out Win32.RECT rect);

    [DllImport("user32.dll")]
    static extern bool AddClipboardFormatListener(IntPtr hWnd);
}
