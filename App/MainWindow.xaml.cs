using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace ScreenBrightnessApp;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, string> _modePrefs;
    private bool _closed;
    private readonly Native.SubclassProc _windowProc;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "app.ico")); }
        catch { /* icon asset missing: keep the default window icon */ }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        _windowProc = HandleWindowMessage; // Keep the native callback alive for the window's lifetime.
        Native.SetWindowSubclass(hwnd, _windowProc, (UIntPtr)1, UIntPtr.Zero);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(760 * scale), workArea.Width), Math.Min((int)(640 * scale), workArea.Height)));
        _modePrefs = AppSettings.Load();
        ContentGrid.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 640;
            ContentGrid.Padding = compact ? new Thickness(20, 16, 20, 16) : new Thickness(32, 24, 32, 20);
            IntroSubtitle.Text = compact ? "为每一块屏幕，找到舒适的亮度。" : "独立调节每一块屏幕，为此刻找到舒适的亮度。";
        };
        ApplyTheme(_modePrefs.GetValueOrDefault("$theme", "system"));
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        Closed += (_, _) =>
        {
            _closed = true;
            Native.RemoveWindowSubclass(hwnd, _windowProc, (UIntPtr)1);
        };
        UpdateCaptionColors();
        _ = InitAsync();
    }

    private static IntPtr HandleWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        const uint WmGetMinMaxInfo = 0x0024;
        if (message == WmGetMinMaxInfo)
        {
            var result = Native.DefSubclassProc(hwnd, message, wParam, lParam);
            var info = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
            var scale = Native.GetDpiForWindow(hwnd) / 96.0;
            info.MinTrackSize.X = (int)(440 * scale);
            info.MinTrackSize.Y = (int)(480 * scale);
            Marshal.StructureToPtr(info, lParam, false);
            return result;
        }
        return Native.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        SystemThemeItem.IsChecked = theme is not ("light" or "dark");
        LightThemeItem.IsChecked = theme == "light";
        DarkThemeItem.IsChecked = theme == "dark";
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var theme = (string)((RadioMenuFlyoutItem)sender).Tag;
        ApplyTheme(theme);
        _modePrefs["$theme"] = theme;
        AppSettings.Save(_modePrefs);
    }

    private void UpdateCaptionColors()
    {
        // Keep the system caption buttons legible when the system theme changes.
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = RootGrid.ActualTheme == ElementTheme.Dark
            ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = RootGrid.ActualTheme == ElementTheme.Dark
            ? Microsoft.UI.Colors.Gray : Microsoft.UI.Colors.DimGray;
    }

    private async Task InitAsync()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        EmptyPanel.Visibility = Visibility.Collapsed;
        DisplayCountText.Text = "正在检测";
        try
        {
            var displays = await Task.Run(() => BrightnessService.EnumerateDisplays()
                .Select(m => (Monitor: m, Gamma: BrightnessService.GetStartupGammaPercent(m.DeviceName))).ToList());
            if (_closed) return;
            foreach (var (monitor, gamma) in displays)
            {
                var card = new DisplayCard(monitor, gamma, _modePrefs);
                card.GammaLimitReached += (_, _) => UnlockInfoBar.IsOpen = true;
                CardsPanel.Children.Add(card);
            }
            DisplayCountText.Text = $"已连接 {displays.Count} 台";
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
            LoadingRing.IsActive = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await InitAsync();

    private void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"New-Item -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM' -Force | Out-Null; " +
                             "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM' -Name GdiIcmGammaRange -PropertyType DWord -Value 0 -Force | Out-Null; " +
                             "Write-Host 'GdiIcmGammaRange = 0 已写入'\"",
                Verb = "runas",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* The user may cancel the elevation prompt. */ }
    }
}
