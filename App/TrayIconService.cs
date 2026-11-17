using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace ScreenBrightnessApp;

/// <summary>
/// Notification-area icon: left-click toggles the brightness panel,
/// right-click opens a small menu. Zero dependencies - raw
/// Shell_NotifyIcon + a Win32 popup menu, callbacks routed through the window subclass.
/// </summary>
internal static class TrayIconService
{
    private const uint CallbackMessage = 0x8000;   // WM_APP
    private const int IdShow = 1;
    private const int IdExit = 2;

    private static IntPtr _hwnd;
    private static IntPtr _hIcon;
    private static bool _added;
    private static uint _taskbarCreatedMsg;
    private static Action? _showRequested;
    private static Action? _toggleRequested;
    private static Action? _exitRequested;

    public static void Initialize(Window window, Action toggleRequested, Action showRequested, Action exitRequested)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _showRequested = showRequested;
        _toggleRequested = toggleRequested;
        _exitRequested = exitRequested;
        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");

        try
        {
            string iconPath;
            try { iconPath = AppIconService.EnsureAccentIcon(); }
            catch { iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico"); }
            _hIcon = Native.LoadImage(IntPtr.Zero, iconPath, Native.IMAGE_ICON, 16, 16, Native.LR_LOADFROMFILE);
            if (_hIcon == IntPtr.Zero) return;
            _added = AddOrUpdate(Native.NIM_ADD);
            if (_added) SetVersion();
        }
        catch { }
    }

    public static bool IsAvailable => _added;

    public static bool TryGetIconRect(out Native.RECT rect)
    {
        var id = new Native.NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = _hwnd, uID = 1 };
        if (_added && Native.Shell_NotifyIconGetRect(ref id, out rect) == 0) return true;
        return Fallback(out rect);
    }

    private static bool Fallback(out Native.RECT rect)
    {
        if (Native.GetCursorPos(out var pt)) { rect = new Native.RECT { Left = pt.X, Right = pt.X, Top = pt.Y, Bottom = pt.Y }; return true; }
        rect = default;
        return false;
    }

    private static void SetVersion()
    {
        var data = NewData(Native.NIF_MESSAGE);
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref data);
    }

    public static void UpdateIcon(string iconPath)
    {
        if (!_added) return;
        try
        {
            var hIcon = Native.LoadImage(IntPtr.Zero, iconPath, Native.IMAGE_ICON, 16, 16, Native.LR_LOADFROMFILE);
            if (hIcon == IntPtr.Zero) return;
            var old = _hIcon;
            _hIcon = hIcon;
            AddOrUpdate(Native.NIM_MODIFY);
            if (old != IntPtr.Zero) Native.DestroyIcon(old);
        }
        catch { /* keep the previous tray icon */ }
    }

    public static void Remove()
    {
        if (!_added) return;
        var nid = NewData(Native.NIF_MESSAGE);
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref nid);
        _added = false;
        if (_hIcon != IntPtr.Zero)
        {
            Native.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    /// <summary>Called from the window subclass; returns true when the message belongs to the tray.</summary>
    public static bool ProcessWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            if (_added && AddOrUpdate(Native.NIM_ADD)) SetVersion();
            return false;
        }
        if (message != CallbackMessage || !_added) return false;

        // Version 4 packs the event in the low word and the icon ID in the high word.
        var evt = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case Native.NIN_SELECT:     // keyboard / programmatic selection
            case Native.NIN_KEYSELECT:
                _toggleRequested?.Invoke();
                return true;
            case Native.WM_CONTEXTMENU: // right click
                ShowContextMenu(0, 0, true);
                return true;
            default:
                return true;            // swallow the icon's remaining mouse chatter
        }
    }

    private static Native.NOTIFYICONDATAW NewData(uint flags)
    {
        return new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _hIcon,
            szTip = "AnyBright · 屏幕亮度",
        };
    }

    private static bool AddOrUpdate(uint message)
    {
        var nid = NewData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | 0x80); // NIF_SHOWTIP (v4)
        return Native.Shell_NotifyIconW(message, ref nid);
    }

    private static void ShowContextMenu(int x, int y, bool useCursorCoords)
    {
        Native.SetForegroundWindow(_hwnd);   // KB135788: lets the menu dismiss on outside clicks
        if (useCursorCoords)
        {
            Native.GetCursorPos(out var pt);
            x = pt.X; y = pt.Y;
        }
        var menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)IdShow, "显示亮度面板");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)IdExit, "退出");
        var cmd = Native.TrackPopupMenu(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, x, y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);
        if (cmd == IdShow) _showRequested?.Invoke();
        else if (cmd == IdExit) _exitRequested?.Invoke();
    }
}
