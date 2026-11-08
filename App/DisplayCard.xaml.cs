using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ScreenBrightnessApp;

public sealed partial class DisplayCard : UserControl
{
    private readonly DisplayMonitor _monitor;
    private readonly Dictionary<string, string> _modePrefs;
    private readonly DispatcherQueueTimer _timer;
    private bool _syncing;
    private bool _busy;
    private bool _unloaded;
    private string _pendingKind = "";
    private int _pendingPercent;
    private string _pendingMode = "gamma";
    public event EventHandler? GammaLimitReached;

    private bool IsDdc => ModeCombo.SelectedIndex == 0;

    public DisplayCard(DisplayMonitor monitor, int gammaPercent, Dictionary<string, string> modePrefs)
    {
        InitializeComponent();
        _monitor = monitor;
        _modePrefs = modePrefs;
        SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 560;
            CardBody.Padding = compact ? new Thickness(20, 20, 20, 12) : new Thickness(24, 22, 24, 12);
            CardFooter.Padding = compact ? new Thickness(20, 12, 20, 16) : new Thickness(24, 14, 24, 18);
            PrimaryBadge.Visibility = !compact && monitor.Primary ? Visibility.Visible : Visibility.Collapsed;
            ModeCombo.Width = compact ? 164 : 204;
            ResetLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        };
        var mode = modePrefs.TryGetValue(monitor.DeviceName, out var saved)
            ? saved : (monitor.DdcSupported ? "ddc" : "gamma");
        if (!monitor.DdcSupported) mode = "gamma";

        var number = new string(monitor.DeviceName.Where(char.IsDigit).ToArray());
        DisplayNumberText.Text = number;
        DisplayNameText.Text = monitor.FriendlyName;
        ToolTipService.SetToolTip(DisplayNameText, monitor.FriendlyName);
        DisplayMetaText.Text = $"显示器 {number} · {monitor.Width} × {monitor.Height}{(monitor.Primary ? " · 主屏幕" : "")}";
        PrimaryBadge.Visibility = monitor.Primary ? Visibility.Visible : Visibility.Collapsed;
        DdcOption.IsEnabled = monitor.DdcSupported;
        if (!monitor.DdcSupported)
            ToolTipService.SetToolTip(ModeCombo, "未检测到 DDC/CI。请检查显示器菜单，或尝试直接连接显示器。");
        ModeCombo.SelectedIndex = mode == "ddc" ? 0 : 1;
        UpdateModeDescription();
        SetDisplayedValue(IsDdc ? BrightnessService.ToPercent(monitor.DdcCurrent, monitor.DdcMax) : gammaPercent);
        ShowStatus(monitor.DdcSupported ? "已就绪，拖动滑块即可调节" : "使用软件调光 · 未检测到硬件背光控制");
        AutomationProperties.SetName(BrightnessSlider, $"{monitor.FriendlyName} 亮度");

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(220);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            _pendingKind = "set";
            _pendingPercent = (int)Math.Round(BrightnessSlider.Value);
            _pendingMode = IsDdc ? "ddc" : "gamma";
            _ = PumpAsync();
        };
        BrightnessSlider.ValueChanged += (_, _) =>
        {
            ValueText.Text = $"{(int)Math.Round(BrightnessSlider.Value)}";
            if (_syncing || _unloaded) return;
            if (!IsDdc && BrightnessSlider.Value <= 0 && !BrightnessService.IsGammaUnlocked)
            {
                // slider bottomed out while the system floor is still in place:
                // point at the unlock instead of silently doing nothing
                GammaLimitReached?.Invoke(this, EventArgs.Empty);
            }
            ShowStatus("正在调整…", "\uE895");
            _timer.Stop();
            _timer.Start();
        };
        ModeCombo.SelectionChanged += OnModeChanged;
        ResetButton.Click += (_, _) =>
        {
            // Cancel a debounced drag so it cannot overwrite the reset afterwards.
            _timer.Stop();
            _pendingKind = "reset";
            _pendingMode = IsDdc ? "ddc" : "gamma";
            _ = PumpAsync();
        };
        Unloaded += (_, _) => { _unloaded = true; _timer.Stop(); _pendingKind = ""; };
    }

    private void UpdateModeDescription()
    {
        ModeDescriptionText.Text = IsDdc ? "调节显示器背光" : "调节输出画面亮度";
        _syncing = true;
        try { BrightnessSlider.Minimum = 0; }   // 0 is safe in both modes (gamma maps to the allowed range)
        finally { _syncing = false; }
    }

    private void SetDisplayedValue(int percent)
    {
        // Reading/resetting a value must never schedule another hardware write.
        _syncing = true;
        try
        {
            BrightnessSlider.Value = Math.Clamp(percent, 0, 100);
            ValueText.Text = $"{(int)Math.Round(BrightnessSlider.Value)}";
        }
        finally { _syncing = false; }
    }

    private void ShowStatus(string text, string glyph = "\uE73E")
    {
        StatusText.Text = text;
        StatusIcon.Glyph = glyph;
    }

    private void SetControlsBusy(bool busy)
    {
        ModeCombo.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        // Allow coalesced slider drags during writes, but not during mode reads/reset.
    }

    private async Task PumpAsync()
    {
        if (_busy || _unloaded) return;
        _busy = true;
        SetControlsBusy(true);
        try
        {
            // DDC bus transactions stay serialized; a drag keeps only the latest request.
            while (_pendingKind.Length > 0 && !_unloaded)
            {
                var kind = _pendingKind;
                var pct = _pendingPercent;
                var ddc = _pendingMode == "ddc";
                _pendingKind = "";
                if (kind == "set")
                {
                    var ok = await Task.Run(() => ddc
                        ? BrightnessService.SetDdc(_monitor.HMonitor, pct, _monitor.DdcMax)
                        : BrightnessService.SetGammaFactor(_monitor.DeviceName, BrightnessService.GammaSliderToFactor(pct)));
                    if (_unloaded) return;
                    // Do not replace the pending drag's feedback with an older result.
                    if (_pendingKind.Length > 0 || _timer.IsRunning) continue;
                    if (ok) ShowStatus($"已应用 {pct}% · {(ddc ? "硬件背光" : "软件调光")}");
                    else ShowStatus("未能应用亮度，请稍后重试", "\uE7BA");
                }
                else
                {
                    BrightnessSlider.IsEnabled = false;
                    ShowStatus("正在恢复初始亮度…", "\uE895");
                    var result = await Task.Run(() =>
                    {
                        var ok = ddc
                            ? BrightnessService.ResetDdc(_monitor.DeviceName, _monitor.HMonitor, _monitor.DdcMax)
                            : BrightnessService.ResetGamma(_monitor.DeviceName);
                            int? value = ddc
                                ? BrightnessService.ReadDdc(_monitor.HMonitor) is { } read ? BrightnessService.ToPercent(read.current, read.max) : null
                                : BrightnessService.ReadGammaFactor(_monitor.DeviceName) is { } f ? BrightnessService.GammaFactorToSlider(f) : null;
                        return (ok, value);
                    });
                    if (_unloaded) return;
                    if (result.value is { } value) SetDisplayedValue(value);
                    ShowStatus(result.ok ? "已恢复初始亮度" : "暂时无法恢复，请稍后重试", result.ok ? "\uE73E" : "\uE7BA");
                }
                if (_pendingKind.Length > 0) await Task.Delay(40);
            }
        }
        catch { ShowStatus("显示器暂时未响应，请稍后重试", "\uE7BA"); }
        finally
        {
            _busy = false;
            SetControlsBusy(false);
            BrightnessSlider.IsEnabled = true;
        }
    }

    private async void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        _timer.Stop();
        _pendingKind = "";
        _busy = true;
        SetControlsBusy(true);
        BrightnessSlider.IsEnabled = false;
        UpdateModeDescription();
        _modePrefs[_monitor.DeviceName] = IsDdc ? "ddc" : "gamma";
        AppSettings.Save(_modePrefs);
        ShowStatus("正在读取当前亮度…", "\uE895");
        var ddc = IsDdc;
        try
        {
            int? value = await Task.Run(() => ddc
                ? BrightnessService.ReadDdc(_monitor.HMonitor) is { } read ? BrightnessService.ToPercent(read.current, read.max) : (int?)null
                : BrightnessService.GetStartupGammaSlider(_monitor.DeviceName));
            if (_unloaded) return;
            if (value is { } percent) SetDisplayedValue(percent);
            ShowStatus(value != null ? "已切换调光方式" : "无法读取硬件亮度，请尝试软件调光", value != null ? "\uE73E" : "\uE7BA");
        }
        catch { ShowStatus("无法读取亮度，请稍后重试", "\uE7BA"); }
        finally
        {
            _busy = false;
            SetControlsBusy(false);
            BrightnessSlider.IsEnabled = true;
        }
    }
}
