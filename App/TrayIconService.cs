using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace ScreenBrightnessApp;

/// <summary>
/// Notification-area (tray) icon: closing the window parks the app here, left-click
/// toggles the window, right-click opens a small menu. Zero dependencies - raw
/// Shell_NotifyIcon + a Win32 popup menu, callbacks routed through the window subclass.
/// </summary>
internal static class TrayIconService
{
    private const uint CallbackMessage = 0x8000;   // WM_APP
    private const uint WM_LBUTTONUP = 0x0202;
    private const int IdShow = 1;
    private const int IdExit = 2;

    private static IntPtr _hwnd;
    private static IntPtr _hIcon;
    private static bool _added;
    private static bool _balloonShown;
    private static uint _taskbarCreatedMsg;
    private static Action? _showRequested;
    private static Action? _exitRequested;
    private static string _iconPath = "";

    public static void Initialize(Window window, Action showRequested, Action exitRequested)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _showRequested = showRequested;
        _exitRequested = exitRequested;
        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                _iconPath = AppIconService.EnsureAccentIcon();
                var hIcon = Native.LoadImage(IntPtr.Zero, _iconPath, Native.IMAGE_ICON, 16, 16, Native.LR_LOADFROMFILE);
                if (hIcon == IntPtr.Zero) return;
                _hIcon = hIcon;
                AddOrUpdate(Native.NIM_ADD);
                _added = true;
                // opt into the modern message protocol (WM_CONTEXTMENU / NIN_SELECT)
                var v = NewData(Native.NIF_MESSAGE);
                v.uVersion = Native.NOTIFYICON_VERSION_4;
                Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref v);
            }
            catch { /* tray is best-effort; the app works without it */ }
        });
    }

    public static void UpdateIcon(string iconPath)
    {
        if (!_added || iconPath == _iconPath) return;
        try
        {
            var hIcon = Native.LoadImage(IntPtr.Zero, iconPath, Native.IMAGE_ICON, 16, 16, Native.LR_LOADFROMFILE);
            if (hIcon == IntPtr.Zero) return;
            var old = _hIcon;
            _hIcon = hIcon;
            _iconPath = iconPath;
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
            if (_added) AddOrUpdate(Native.NIM_ADD);   // Explorer restarted: re-register the icon
            return false;
        }
        if (message != CallbackMessage || !_added) return false;

        // Event type arrives in lParam's low word (with a 0x10000 "from tray" flag):
        // 0x201/0x202 = left button down/up, 0x400 = NIN_SELECT, 0x7B = WM_CONTEXTMENU
        var evt = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case WM_LBUTTONUP:          // left click
            case Native.NIN_SELECT:     // keyboard / programmatic selection
                _showRequested?.Invoke();
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

    private static void AddOrUpdate(uint message)
    {
        var nid = NewData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP);
        Native.Shell_NotifyIconW(message, ref nid);
    }

    /// <summary>One-time toast after the first close-to-tray so users know the app is still running.</summary>
    public static void ShowBalloonOnce()
    {
        if (_balloonShown || !_added) return;
        _balloonShown = true;
        var nid = NewData(Native.NIF_INFO);
        nid.szInfoTitle = "AnyBright";
        nid.szInfo = "已驻留系统托盘：左键图标可随时调节亮度，右键可退出。";
        nid.dwInfoFlags = Native.NIIF_INFO;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref nid);
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
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)IdShow, "显示主窗口");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)IdExit, "退出");
        var cmd = Native.TrackPopupMenu(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, x, y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);
        if (cmd == IdShow) _showRequested?.Invoke();
        else if (cmd == IdExit) _exitRequested?.Invoke();
    }
}
