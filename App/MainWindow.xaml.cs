using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace ScreenBrightnessApp;

public sealed partial class MainWindow : Window
{
    private const double PanelWidth = 380;
    private readonly Dictionary<string, string> _modePrefs;
    private readonly Native.SubclassProc _windowProc;
    private readonly IntPtr _hwnd;
    private bool _closed;
    private bool _exiting;
    private bool _visible;
    private bool _detecting;
    private bool _resizeQueued;
    private long _trayDismissedAt;
    private Native.RECT _anchor;

    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        _modePrefs = AppSettings.Load();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _windowProc = HandleWindowMessage;
        Native.SetWindowSubclass(_hwnd, _windowProc, (UIntPtr)1, UIntPtr.Zero);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        // Windows 11 supplies the native rounded outline and shadow; older Windows ignores it.
        var corners = 2;
        Native.DwmSetWindowAttribute(_hwnd, 33, ref corners, sizeof(int));
        TrayIconService.Initialize(this, ToggleFromTray, ShowFromTray, ExitApp);
        AppIconService.Apply(this);
        AppIconService.Watch(this);

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            AutoStartItem.IsChecked = key?.GetValue("AnyBright") != null;
        }
        catch { }
        ApplyTheme(_modePrefs.GetValueOrDefault("$theme", "system"));
        RootGrid.ActualThemeChanged += (_, _) => ApplyHeaderGlyphs();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) QueueDismiss();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            TrayIconService.Remove();
            Native.RemoveWindowSubclass(_hwnd, _windowProc, (UIntPtr)1);
        };
        AppWindow.Closing += (_, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            HideToTray();
        };
        _ = InitAsync();
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (TrayIconService.ProcessWindowMessage(hwnd, message, wParam, lParam)) return IntPtr.Zero;
        if (message == 0x0100 && wParam == (IntPtr)0x1B) // WM_KEYDOWN / VK_ESCAPE
        {
            HideToTray();
            return IntPtr.Zero;
        }
        if (message == 0x001C && wParam == IntPtr.Zero) QueueDismiss(); // WM_ACTIVATEAPP
        if (message == 0x02E0) ScheduleResize(); // WM_DPICHANGED
        return Native.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private async void QueueDismiss()
    {
        // Flyouts and combo-box popups may temporarily own activation. Let them settle,
        // then dismiss only if focus has actually moved out of this application.
        await Task.Delay(100);
        if (_closed || !_visible) return;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var processId);
        if (processId == Environment.ProcessId) return;
        if (TrayIconService.TryGetIconRect(out var icon) && Native.GetCursorPos(out var cursor)
            && cursor.X >= icon.Left && cursor.X < icon.Right && cursor.Y >= icon.Top && cursor.Y < icon.Bottom)
            _trayDismissedAt = Environment.TickCount64;
        HideToTray();
    }

    private void ToggleFromTray()
    {
        // Clicking the icon first deactivates the panel; do not reopen it on mouse-up.
        if (Environment.TickCount64 - _trayDismissedAt < 400) return;
        if (_visible) HideToTray();
        else ShowFromTray();
    }

    private void HideToTray()
    {
        if (!TrayIconService.IsAvailable) return;
        _visible = false;
        AppWindow.Hide();
    }

    public void ShowFromTray()
    {
        if (_closed) return;
        TrayIconService.TryGetIconRect(out _anchor);
        ResizeAndPosition();
        _visible = true;
        AppWindow.Show();
        Activate();
        Native.SetForegroundWindow(_hwnd);
        RootGrid.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleResize();

    private void ScheduleResize()
    {
        if (_closed || _resizeQueued) return;
        _resizeQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resizeQueued = false;
            if (_visible && !_closed) ResizeAndPosition();
        });
    }

    private void ResizeAndPosition()
    {
        var point = new Native.POINT { X = (_anchor.Left + _anchor.Right) / 2, Y = (_anchor.Top + _anchor.Bottom) / 2 };
        var monitor = Native.MonitorFromPoint(point, 2); // MONITOR_DEFAULTTONEAREST
        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(monitor, ref info)) return;
        var scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : Native.GetDpiForWindow(_hwnd) / 96.0;
        var work = info.rcWork;
        var gap = (int)Math.Round(8 * scale);
        var width = Math.Min((int)Math.Round(PanelWidth * scale), work.Right - work.Left - gap * 2);
        var logicalWidth = width / scale - 2;
        HeaderGrid.Measure(new Windows.Foundation.Size(logicalWidth, double.PositiveInfinity));
        CardsPanel.Measure(new Windows.Foundation.Size(Math.Max(1, logicalWidth - 24), double.PositiveInfinity));
        FooterBorder.Measure(new Windows.Foundation.Size(logicalWidth, double.PositiveInfinity));
        var desired = HeaderGrid.DesiredSize.Height + CardsPanel.DesiredSize.Height + FooterBorder.DesiredSize.Height + 6;
        var height = Math.Min((int)Math.Ceiling(Math.Clamp(desired, 180, 640) * scale), work.Bottom - work.Top - gap * 2);

        var x = _anchor.Right - width;
        var y = _anchor.Top - height - gap;
        if (_anchor.Top >= work.Bottom) y = work.Bottom - height - gap; // bottom taskbar
        else if (_anchor.Bottom <= work.Top) y = work.Top + gap; // top taskbar
        else if (_anchor.Right <= work.Left) { x = work.Left + gap; y = _anchor.Bottom - height; }
        else if (_anchor.Left >= work.Right) { x = work.Right - width - gap; y = _anchor.Bottom - height; }
        x = Math.Clamp(x, work.Left + gap, work.Right - width - gap);
        y = Math.Clamp(y, work.Top + gap, work.Bottom - height - gap);
        if (AppWindow.Size.Width != width || AppWindow.Size.Height != height || AppWindow.Position.X != x || AppWindow.Position.Y != y)
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // Theme the outer surface as well as controls and their flyouts.
        PanelBorder.RequestedTheme = RootGrid.RequestedTheme;
        SystemThemeItem.IsChecked = theme is not ("light" or "dark");
        LightThemeItem.IsChecked = theme == "light";
        DarkThemeItem.IsChecked = theme == "dark";
        ApplyHeaderGlyphs();
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var theme = (string)((RadioMenuFlyoutItem)sender).Tag;
        ApplyTheme(theme);
        _modePrefs["$theme"] = theme;
        AppSettings.Save(_modePrefs);
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        var on = AutoStartItem.IsChecked;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (on) key.SetValue("AnyBright", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("AnyBright", false);
        }
        catch { AutoStartItem.IsChecked = !on; }
    }

    private void ApplyHeaderGlyphs()
    {
        var suffix = RootGrid.ActualTheme == ElementTheme.Dark ? "light" : "dark";
        GearImg.Source = new BitmapImage(new Uri("ms-appx:///assets/settings-" + suffix + ".png"));
        HelpImg.Source = new BitmapImage(new Uri("ms-appx:///assets/help-" + suffix + ".png"));
    }

    private async Task InitAsync()
    {
        if (_detecting || CardsPanel.Children.OfType<DisplayCard>().Any(card => card.IsBusy)) return;
        _detecting = true;
        RefreshItem.IsEnabled = false;
        foreach (var card in CardsPanel.Children.OfType<DisplayCard>().ToList()) CardsPanel.Children.Remove(card);
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        EmptyPanel.Visibility = Visibility.Collapsed;
        DisplayCountText.Text = "正在检测显示器…";
        try
        {
            var displays = await Task.Run(() => BrightnessService.EnumerateDisplays()
                .Select(m => (Monitor: m, Gamma: BrightnessService.GetStartupGammaSlider(m.DeviceName))).ToList());
            if (_closed) return;
            foreach (var (monitor, gamma) in displays) CardsPanel.Children.Add(new DisplayCard(monitor, gamma, _modePrefs));
            DisplayCountText.Text = $"{displays.Count} 台显示器 · 随手调节亮度";
            EmptyTitle.Text = "暂未发现显示器";
            EmptyMessage.Text = "请确认显示器已连接，然后重新检测。";
            EmptyPanel.Visibility = displays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            if (_closed) return;
            DisplayCountText.Text = "检测未完成";
            EmptyTitle.Text = "暂时无法读取显示器";
            EmptyMessage.Text = "请检查显示器连接，然后重新检测。";
            EmptyPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _detecting = false;
            if (!_closed)
            {
                RefreshItem.IsEnabled = true;
                LoadingRing.IsActive = false;
                LoadingPanel.Visibility = Visibility.Collapsed;
                ScheduleResize();
            }
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await InitAsync();
    private void OnExitClick(object sender, RoutedEventArgs e) => ExitApp();

    private void ExitApp()
    {
        _exiting = true;
        TrayIconService.Remove();
        Close();
    }
}
