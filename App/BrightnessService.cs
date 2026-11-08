using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenBrightnessApp;

public sealed class DisplayMonitor
{
    public IntPtr HMonitor { get; init; }
    public string DeviceName { get; init; } = "";
    public string FriendlyName { get; set; } = "";
    public bool Primary { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool DdcSupported { get; set; }
    public int DdcCurrent { get; set; }
    public int DdcMax { get; set; } = 100;
}

/// <summary>
/// Brightness control via two paths:
///  - DDC/CI (VCP 0x10): commands the monitor to change its own backlight,
///    equivalent to using the monitor's joystick/buttons.
///  - Gamma ramp: scales the video card's lookup table, dimming the *signal*
///    sent to the display. Works on any monitor without cooperation.
/// </summary>
public static partial class BrightnessService
{
    private const byte VcpBrightness = 0x10;

    // source-generated serializers: trim-safe by construction (no reflection)
    [JsonSerializable(typeof(GammaStateDto))]
    [JsonSerializable(typeof(DdcStateDto))]
    private sealed partial class ServiceJsonContext : JsonSerializerContext;

    // per-device baselines captured before the first gamma change,
    // so repeated sets never compound and external ramps (f.lux, Night light) survive
    private static readonly Dictionary<string, ushort[][]> GammaBaselines = new();
    private static readonly Dictionary<string, int> DdcOriginals = new();

    public static int ToPercent(int current, int max)
        => max <= 0 ? current : (int)Math.Round(current * 100.0 / max);

    public static List<DisplayMonitor> EnumerateDisplays()
    {
        var list = new List<DisplayMonitor>();

        Native.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEXW();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
            if (Native.GetMonitorInfoW(hMonitor, ref info))
            {
                list.Add(new DisplayMonitor
                {
                    HMonitor = hMonitor,
                    DeviceName = info.szDevice,
                    Primary = (info.dwFlags & 1) != 0,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                });
            }
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        list.Sort((a, b) => DisplayNumber(a.DeviceName).CompareTo(DisplayNumber(b.DeviceName)));

        foreach (var m in list)
        {
            m.FriendlyName = QueryFriendlyName(m.HMonitor) ?? m.DeviceName;
            var ddc = ReadDdc(m.HMonitor);
            if (ddc != null)
            {
                m.DdcSupported = true;
                m.DdcCurrent = ddc.Value.current;
                m.DdcMax = ddc.Value.max;
                RememberDdcOriginal(m.DeviceName, ToPercent(ddc.Value.current, ddc.Value.max));
            }
        }
        return list;
    }

    private static int DisplayNumber(string deviceName)
    {
        var digits = new string(deviceName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static string? QueryFriendlyName(IntPtr hMonitor)
    {
        var monitors = GetPhysicalMonitors(hMonitor);
        string? name = null;
        if (monitors.Length > 0)
        {
            var desc = monitors[0].Description;
            if (!string.IsNullOrWhiteSpace(desc)) name = desc;
        }
        foreach (var pm in monitors) Native.DestroyPhysicalMonitor(pm.hPhysicalMonitor);
        return name;
    }

    private static Native.PHYSICAL_MONITOR[] GetPhysicalMonitors(IntPtr hMonitor)
    {
        uint count = 0;
        if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count < 1)
            return Array.Empty<Native.PHYSICAL_MONITOR>();
        var arr = new Native.PHYSICAL_MONITOR[(int)count];
        if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, arr))
            return Array.Empty<Native.PHYSICAL_MONITOR>();
        return arr;
    }

    // ---------- DDC/CI (hardware backlight) ----------

    public static (int current, int max)? ReadDdc(IntPtr hMonitor)
    {
        (int, int)? result = null;
        foreach (var pm in GetPhysicalMonitors(hMonitor))
        {
            bool ok = Native.GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, VcpBrightness, out _, out uint current, out uint max);
            Native.DestroyPhysicalMonitor(pm.hPhysicalMonitor);
            if (ok && result == null) result = ((int)current, (int)max);
        }
        return result;
    }

    /// <summary>
    /// One bus transaction per call: no capability pre-read (max comes from the cached
    /// reply captured at startup), plus one retry — slow monitor MCUs occasionally drop
    /// commands when transactions arrive back-to-back.
    /// </summary>
    public static bool SetDdc(IntPtr hMonitor, int percent, int maxPercent = 100)
    {
        if (maxPercent <= 0) maxPercent = 100;
        bool any = false;
        foreach (var pm in GetPhysicalMonitors(hMonitor))
        {
            uint value = (uint)Math.Round(maxPercent * (percent / 100.0));
            bool ok = Native.SetVCPFeature(pm.hPhysicalMonitor, VcpBrightness, value);
            if (!ok)
            {
                Thread.Sleep(40);
                ok = Native.SetVCPFeature(pm.hPhysicalMonitor, VcpBrightness, value);
            }
            if (ok) any = true;
            Native.DestroyPhysicalMonitor(pm.hPhysicalMonitor);
        }
        return any;
    }

    public static bool ResetDdc(string deviceName, IntPtr hMonitor, int maxPercent = 100)
    {
        if (!DdcOriginals.TryGetValue(deviceName, out int percent))
        {
            var persisted = LoadDdcOriginal(deviceName);
            if (persisted == null) return false;
            percent = persisted.Value;
            DdcOriginals[deviceName] = percent;
        }
        return SetDdc(hMonitor, percent, maxPercent);
    }

    // ---------- gamma ramp (software signal dimming) ----------

    // Windows rejects gamma ramps below ~50% unless GdiIcmGammaRange=0 is set.
    // The GUI slider maps its 0..100 range onto whichever window is available:
    // locked -> factor [0.52, 1.0]; unlocked -> factor [0.05, 1.0].
    private const double LockedGammaFloor = 0.52;
    private const double UnlockedGammaFloor = 0.05;

    // GDI reads the registry override once per process, so cache it at startup:
    // flipping the registry mid-run must not extend the range before win32k allows it.
    public static bool IsGammaUnlocked { get; } = DetectGammaUnlocked();

    private static bool DetectGammaUnlocked()
    {
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM",
                "GdiIcmGammaRange", null);
            return v is int i && i == 0;
        }
        catch { return false; }
    }

    public static double GammaSliderToFactor(int sliderPercent)
    {
        var t = Math.Clamp(sliderPercent, 0, 100) / 100.0;
        return IsGammaUnlocked
            ? UnlockedGammaFloor + (1.0 - UnlockedGammaFloor) * t
            : LockedGammaFloor + (1.0 - LockedGammaFloor) * t;
    }

    public static int GammaFactorToSlider(double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        var t = IsGammaUnlocked
            ? (factor - UnlockedGammaFloor) / (1.0 - UnlockedGammaFloor)
            : (factor - LockedGammaFloor) / (1.0 - LockedGammaFloor);
        return (int)Math.Round(Math.Clamp(t, 0.0, 1.0) * 100);
    }

    public static double? ReadGammaFactor(string deviceName)
    {
        IntPtr hdc = Native.CreateDC(null, deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            var ramp = EmptyRamp();
            if (!Native.GetDeviceGammaRamp(hdc, ref ramp)) return null;
            return ramp.Red[255] / 65280.0;   // identity max = 255*256
        }
        finally { Native.DeleteDC(hdc); }
    }

    public static bool SetGammaFactor(string deviceName, double factor)
    {
        factor = Math.Clamp(factor, 0.01, 1.0);

        IntPtr hdc = Native.CreateDC(null, deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            if (!GammaBaselines.TryGetValue(deviceName, out var baseline))
            {
                var persisted = LoadGammaState(deviceName);
                if (persisted != null && persisted.r.Length == 256 && persisted.g.Length == 256 && persisted.b.Length == 256)
                {
                    // reuse the baseline captured in a previous run so dimming never compounds
                    baseline = new[]
                    {
                        Array.ConvertAll(persisted.r, x => (ushort)x),
                        Array.ConvertAll(persisted.g, x => (ushort)x),
                        Array.ConvertAll(persisted.b, x => (ushort)x),
                    };
                }
                else
                {
                    var captured = EmptyRamp();
                    if (!Native.GetDeviceGammaRamp(hdc, ref captured)) return false;
                    baseline = new[] { captured.Red, captured.Green, captured.Blue };
                }
                GammaBaselines[deviceName] = baseline;
            }

            var target = EmptyRamp();
            for (int c = 0; c < 3; c++)
            {
                var dst = c == 0 ? target.Red : c == 1 ? target.Green : target.Blue;
                for (int i = 0; i < 256; i++)
                {
                    int v = (int)Math.Round(baseline[c][i] * factor);
                    if (v > 65535) v = 65535;
                    if (v < 0) v = 0;
                    dst[i] = (ushort)v;
                }
            }
            bool ok = Native.SetDeviceGammaRamp(hdc, ref target);
            if (ok) SaveGammaState(deviceName, baseline, factor);
            return ok;
        }
        finally { Native.DeleteDC(hdc); }
    }

    public static bool ResetGamma(string deviceName)
    {
        IntPtr hdc = Native.CreateDC(null, deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            Native.Ramp ramp;
            if (GammaBaselines.TryGetValue(deviceName, out var baseline))
            {
                ramp = new Native.Ramp { Red = baseline[0], Green = baseline[1], Blue = baseline[2] };
                GammaBaselines.Remove(deviceName);
            }
            else
            {
                ramp = IdentityRamp();
            }
            bool ok = Native.SetDeviceGammaRamp(hdc, ref ramp);
            if (ok) ClearGammaState(deviceName);
            return ok;
        }
        finally { Native.DeleteDC(hdc); }
    }

    // ---------- persistence: baselines survive restarts, so dimming never compounds ----------

    private static string StatePath(string prefix, string deviceName)
    {
        var id = new string(deviceName.Where(char.IsLetterOrDigit).ToArray());
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenBrightness");
        return Path.Combine(dir, $"{prefix}_{id}.json");
    }

    private sealed class GammaStateDto
    {
        public int version { get; set; } = 2;          // v1 stored appliedPct (percent), v2 stores factor
        public int[] r { get; set; } = Array.Empty<int>();
        public int[] g { get; set; } = Array.Empty<int>();
        public int[] b { get; set; } = Array.Empty<int>();
        public double factor { get; set; } = 1.0;
        public int appliedPct { get; set; } = 100;     // v1 legacy field, kept for migration
    }

    private static double StateToFactor(GammaStateDto dto)
        => dto.version >= 2 ? dto.factor : dto.appliedPct / 100.0;

    private sealed class DdcStateDto
    {
        public int originalPct { get; set; }
    }

    private static GammaStateDto? LoadGammaState(string deviceName)
    {
        try
        {
            var path = StatePath("gamma", deviceName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), ServiceJsonContext.Default.GammaStateDto);
        }
        catch { /* corrupted -> recapture */ }
        return null;
    }

    private static void SaveGammaState(string deviceName, ushort[][] baseline, double factor)
    {
        try
        {
            var dto = new GammaStateDto
            {
                r = Array.ConvertAll(baseline[0], x => (int)x),
                g = Array.ConvertAll(baseline[1], x => (int)x),
                b = Array.ConvertAll(baseline[2], x => (int)x),
                factor = factor,
                appliedPct = (int)Math.Round(factor * 100),
            };
            var path = StatePath("gamma", deviceName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, ServiceJsonContext.Default.GammaStateDto));
        }
        catch { /* best effort */ }
    }

    private static void ClearGammaState(string deviceName)
    {
        try
        {
            var path = StatePath("gamma", deviceName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void RememberDdcOriginal(string deviceName, int percent)
    {
        DdcOriginals[deviceName] = percent;
        try
        {
            var path = StatePath("ddc", deviceName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new DdcStateDto { originalPct = percent }, ServiceJsonContext.Default.DdcStateDto));
        }
        catch { /* best effort */ }
    }

    private static int? LoadDdcOriginal(string deviceName)
    {
        try
        {
            var path = StatePath("ddc", deviceName);
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize(File.ReadAllText(path), ServiceJsonContext.Default.DdcStateDto);
                if (dto != null) return dto.originalPct;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Slider position for the gamma card at startup (v1 states are migrated).</summary>
    public static int GetStartupGammaSlider(string deviceName)
    {
        var persisted = LoadGammaState(deviceName);
        if (persisted != null) return GammaFactorToSlider(StateToFactor(persisted));
        return ReadGammaFactor(deviceName) is { } f ? GammaFactorToSlider(f) : 100;
    }

    private static Native.Ramp EmptyRamp() => new()
    {
        Red = new ushort[256],
        Green = new ushort[256],
        Blue = new ushort[256],
    };

    private static Native.Ramp IdentityRamp()
    {
        var ramp = EmptyRamp();
        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)(i * 256);
            ramp.Red[i] = v;
            ramp.Green[i] = v;
            ramp.Blue[i] = v;
        }
        return ramp;
    }
}
