<#
  brightness.ps1 - Windows display brightness tool

  gamma mode (default): software brightness. Scales the video card's gamma
  lookup table, i.e. dims the picture *signal* sent to the display - works
  on any monitor, internal or external, no monitor cooperation needed.
  This is what macOS does for displays without hardware brightness control.

  ddc mode: hardware backlight via DDC/CI (VCP code 0x10) - the same protocol
  macOS uses for external monitors. Only works if the monitor enables DDC/CI.

  Commands:
    list                            show displays, current values, DDC support
    get   [-Mode gamma|ddc] [-Display n]
    set N [-Mode gamma|ddc] [-Display n]     N = 0..100
    reset [-Mode gamma|ddc] [-Display n]     restore the state before first `set`
    unlock                          lift GDI's ~50% gamma floor (admin, once)

  Notes:
    - GDI rejects gamma ramps below roughly 50% unless unlocked (see `unlock`).
    - gamma dimming has no visible effect while HDR is on.
    - apps like f.lux / Night light also write the gamma ramp; this tool
      preserves their baseline and multiplies on top of it.
#>
param(
    [Parameter(Position = 0)][string]$Command = 'get',
    [Parameter(Position = 1)]$Value,
    [int[]]$Display,
    [ValidateSet('gamma', 'ddc')][string]$Mode = 'gamma'
)

$ErrorActionPreference = 'Stop'

if (-not ('Win32' -as [type])) {
Add-Type @'
using System;
using System.Runtime.InteropServices;

public struct RAMP
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
}

public struct POINT
{
    public int X;
    public int Y;
}

public struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
}

public static class Win32
{
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool GetDeviceGammaRamp(IntPtr hdc, ref RAMP ramp);
    [DllImport("gdi32.dll")] public static extern bool SetDeviceGammaRamp(IntPtr hdc, ref RAMP ramp);

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
    [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, out uint type, out uint current, out uint max);
    [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint value);
}
'@
}
Add-Type -AssemblyName System.Windows.Forms

$BaseDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

# ---------- display enumeration ----------

function Get-Screens {
    [System.Windows.Forms.Screen]::AllScreens |
        Sort-Object { [int]($_.DeviceName -replace '[^0-9]', '') }
}

function Get-HMonitor($screen) {
    $pt = New-Object POINT
    $pt.X = $screen.Bounds.Left + [int]($screen.Bounds.Width / 2)
    $pt.Y = $screen.Bounds.Top + [int]($screen.Bounds.Height / 2)
    [Win32]::MonitorFromPoint($pt, 2)   # MONITOR_DEFAULTTONEAREST
}

# ---------- gamma (software) ----------

function New-Ramp {
    $r = New-Object RAMP
    $r.Red = [uint16[]]::new(256)
    $r.Green = [uint16[]]::new(256)
    $r.Blue = [uint16[]]::new(256)
    $r
}

function ConvertTo-IdentityRamp {
    $r = New-Ramp
    for ($i = 0; $i -lt 256; $i++) {
        $v = [uint16]($i * 256)
        $r.Red[$i] = $v; $r.Green[$i] = $v; $r.Blue[$i] = $v
    }
    $r
}

function Set-Ramp([string]$deviceName, [RAMP]$ramp) {
    $hdc = [Win32]::CreateDC($null, $deviceName, $null, [IntPtr]::Zero)
    if ($hdc -eq [IntPtr]::Zero) { return $false }
    try { [Win32]::SetDeviceGammaRamp($hdc, [ref]$ramp) }
    finally { [void][Win32]::DeleteDC($hdc) }
}

function Get-GammaPct([string]$deviceName) {
    $hdc = [Win32]::CreateDC($null, $deviceName, $null, [IntPtr]::Zero)
    if ($hdc -eq [IntPtr]::Zero) { return $null }
    try {
        $r = New-Ramp
        if (-not [Win32]::GetDeviceGammaRamp($hdc, [ref]$r)) { return $null }
        [int][Math]::Round($r.Red[255] / 652.8)
    } finally { [void][Win32]::DeleteDC($hdc) }
}

function Set-Gamma([string]$deviceName, [double]$factor, [switch]$RestoreBaseline) {
    $hdc = [Win32]::CreateDC($null, $deviceName, $null, [IntPtr]::Zero)
    if ($hdc -eq [IntPtr]::Zero) { throw "CreateDC failed for $deviceName" }
    try {
        # capture (or reuse) the baseline ramp so repeated `set` never compounds
        # and external ramps (f.lux, Night light) are preserved
        $st = Read-State $deviceName
        $baseline = New-Ramp
        if ($st -and $st.baseline) {
            $baseline.Red = [uint16[]]$st.baseline.r
            $baseline.Green = [uint16[]]$st.baseline.g
            $baseline.Blue = [uint16[]]$st.baseline.b
        } else {
            if (-not [Win32]::GetDeviceGammaRamp($hdc, [ref]$baseline)) { throw "GetDeviceGammaRamp failed for $deviceName" }
        }

        if ($RestoreBaseline -or $factor -ge 1.0) {
            $target = $baseline
        } else {
            $target = New-Ramp
            foreach ($ch in @('Red', 'Green', 'Blue')) {
                for ($i = 0; $i -lt 256; $i++) {
                    $v = [int][Math]::Round($baseline.$ch[$i] * $factor)
                    if ($v -gt 65535) { $v = 65535 }
                    if ($v -lt 0) { $v = 0 }
                    $target.$ch[$i] = [uint16]$v
                }
            }
        }

        if (-not [Win32]::SetDeviceGammaRamp($hdc, [ref]$target)) { return $false }

        if ($RestoreBaseline) { Clear-State $deviceName }
        else {
            Write-State $deviceName @{
                factor   = $factor
                baseline = @{ r = @($baseline.Red); g = @($baseline.Green); b = @($baseline.Blue) }
            }
        }
        return $true
    } finally { [void][Win32]::DeleteDC($hdc) }
}

# ---------- ddc/ci (hardware) ----------

function Get-PhysicalMonitors([IntPtr]$hMonitor) {
    $count = [uint32]0
    if (-not [Win32]::GetNumberOfPhysicalMonitorsFromHMONITOR($hMonitor, [ref]$count)) { return @() }
    if ($count -lt 1) { return @() }
    $arr = New-Object 'PHYSICAL_MONITOR[]' ([int]$count)
    if (-not [Win32]::GetPhysicalMonitorsFromHMONITOR($hMonitor, $count, $arr)) { return @() }
    ,$arr
}

function Get-DdcBrightness([IntPtr]$hMonitor) {
    foreach ($m in (Get-PhysicalMonitors $hMonitor)) {
        $t = [uint32]0; $cur = [uint32]0; $max = [uint32]0
        $ok = [Win32]::GetVCPFeatureAndVCPFeatureReply($m.hPhysicalMonitor, [byte]0x10, [ref]$t, [ref]$cur, [ref]$max)
        [void][Win32]::DestroyPhysicalMonitor($m.hPhysicalMonitor)
        if ($ok) { return @{ current = [int]$cur; max = [int]$max } }
    }
    $null
}

function Set-DdcBrightness([IntPtr]$hMonitor, [int]$pct) {
    $any = $false
    foreach ($m in (Get-PhysicalMonitors $hMonitor)) {
        $t = [uint32]0; $cur = [uint32]0; $max = [uint32]0
        if ([Win32]::GetVCPFeatureAndVCPFeatureReply($m.hPhysicalMonitor, [byte]0x10, [ref]$t, [ref]$cur, [ref]$max) -and $max -gt 0) {
            $val = [uint32][Math]::Round($max * ($pct / 100.0))
            if ([Win32]::SetVCPFeature($m.hPhysicalMonitor, [byte]0x10, $val)) { $any = $true }
        }
        [void][Win32]::DestroyPhysicalMonitor($m.hPhysicalMonitor)
    }
    $any
}

# ---------- per-display state ----------

function Get-StatePath([string]$deviceName) {
    $id = ($deviceName -replace '[^A-Za-z0-9]', '')
    Join-Path $BaseDir (".state\{0}_{1}.json" -f $Mode, $id)
}

function Read-State([string]$deviceName) {
    $p = Get-StatePath $deviceName
    if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json }
}

function Write-State([string]$deviceName, $obj) {
    $dir = Join-Path $BaseDir '.state'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $obj | ConvertTo-Json -Depth 4 -Compress | Set-Content -Path (Get-StatePath $deviceName) -Encoding ASCII
}

function Clear-State([string]$deviceName) {
    $p = Get-StatePath $deviceName
    if (Test-Path $p) { Remove-Item $p -Force }
}

# ---------- main ----------

function Show-Usage {
    Write-Host @"
brightness.ps1 - display brightness tool
  list                            displays, current brightness, DDC support
  get   [-Mode gamma|ddc] [-Display n]
  set N [-Mode gamma|ddc] [-Display n]     N = 0..100
  reset [-Mode gamma|ddc] [-Display n]
  unlock                          allow gamma below ~50% (admin, one-time)
"@
}

$all = @(Get-Screens)
if ($all.Count -eq 0) { Write-Host 'No active displays found.'; exit 1 }

$targets = @()
if ($Display) {
    foreach ($n in $Display) {
        if ($n -lt 1 -or $n -gt $all.Count) {
            Write-Host ("Display index {0} out of range (1-{1})." -f $n, $all.Count); exit 1
        }
        $targets += ($n - 1)
    }
} else {
    $targets = @(0..($all.Count - 1))
}

$cmd = $Command.ToLowerInvariant()

switch ($cmd) {
    'help' { Show-Usage; exit 0 }

    'list' {
        foreach ($i in $targets) {
            $sc = $all[$i]; $name = $sc.DeviceName
            $tag = ''; if ($sc.Primary) { $tag = '  [primary]' }
            Write-Host ("[{0}] {1}  {2}x{3}{4}" -f ($i + 1), $name, $sc.Bounds.Width, $sc.Bounds.Height, $tag)
            $g = Get-GammaPct $name
            $gTxt = 'n/a'; if ($null -ne $g) { $gTxt = "{0}%" -f $g }
            $ddc = Get-DdcBrightness (Get-HMonitor $sc)
            $dTxt = 'not supported'; if ($ddc) { $dTxt = "supported ({0}/{1})" -f $ddc.current, $ddc.max }
            Write-Host ("    gamma: {0}    ddc: {1}" -f $gTxt, $dTxt)
        }
        exit 0
    }

    'get' {
        foreach ($i in $targets) {
            $sc = $all[$i]; $name = $sc.DeviceName
            if ($Mode -eq 'gamma') {
                $st = Read-State $name
                $pct = $null
                if ($st -and $st.PSObject.Properties['factor']) { $pct = [int][Math]::Round([double]$st.factor * 100) }
                if ($null -eq $pct) { $pct = Get-GammaPct $name }
                Write-Host ("[{0}] {1}  gamma brightness: {2}%" -f ($i + 1), $name, $pct)
            } else {
                $ddc = Get-DdcBrightness (Get-HMonitor $sc)
                if ($ddc) { Write-Host ("[{0}] {1}  ddc brightness: {2}/{3}" -f ($i + 1), $name, $ddc.current, $ddc.max) }
                else { Write-Host ("[{0}] {1}  ddc: not supported" -f ($i + 1), $name) }
            }
        }
        exit 0
    }

    'set' {
        if ($null -eq $Value) { Write-Host 'Usage: brightness.ps1 set <0-100> [-Mode gamma|ddc] [-Display n]'; exit 1 }
        try { $Value = [int]$Value } catch { Write-Host 'Value must be an integer 0-100.'; exit 1 }
        if ($Value -lt 0 -or $Value -gt 100) { Write-Host 'Value must be 0-100.'; exit 1 }
        if ($Mode -eq 'gamma' -and $Value -lt 1) {
            $Value = 1   # a fully zero ramp would black the screen out; keep 1%
            Write-Host 'gamma mode clamps 0 -> 1 (a zero ramp would black out the screen).'
        }

        foreach ($i in $targets) {
            $sc = $all[$i]; $name = $sc.DeviceName
            if ($Mode -eq 'gamma') {
                $ok = Set-Gamma $name ($Value / 100.0)
                if ($ok) { Write-Host ("[{0}] {1}  gamma -> {2}%" -f ($i + 1), $name, $Value) }
                else {
                    Write-Host ("[{0}] {1}  gamma -> {2}% FAILED (GDI rejected the ramp)." -f ($i + 1), $name, $Value)
                    Write-Host "Windows caps gamma dimming near 50% by default; run 'brightness.ps1 unlock' once (admin), then reopen the terminal."
                }
            } else {
                $hm = Get-HMonitor $sc
                $cur = Get-DdcBrightness $hm
                if (-not $cur) {
                    Write-Host ("[{0}] {1}  ddc: not supported/failing. Check the monitor OSD 'DDC/CI' option." -f ($i + 1), $name)
                    continue
                }
                if (-not (Read-State $name)) { Write-State $name @{ original = $cur.current; max = $cur.max } }
                if (Set-DdcBrightness $hm $Value) {
                    $raw = [int][Math]::Round($cur.max * ($Value / 100.0))
                    Write-Host ("[{0}] {1}  ddc -> {2}% ({3}/{4})" -f ($i + 1), $name, $Value, $raw, $cur.max)
                } else {
                    Write-Host ("[{0}] {1}  ddc set failed." -f ($i + 1), $name)
                }
            }
        }
        exit 0
    }

    'reset' {
        foreach ($i in $targets) {
            $sc = $all[$i]; $name = $sc.DeviceName
            if ($Mode -eq 'gamma') {
                $st = Read-State $name
                $ok = $false
                if ($st -and $st.baseline) { $ok = Set-Gamma $name 1.0 -RestoreBaseline }
                else { $ok = Set-Ramp $name (ConvertTo-IdentityRamp) }
                if ($ok) { Write-Host ("[{0}] {1}  gamma restored" -f ($i + 1), $name) }
                else { Write-Host ("[{0}] {1}  gamma restore FAILED" -f ($i + 1), $name) }
            } else {
                $st = Read-State $name
                if ($st -and $st.PSObject.Properties['original']) {
                    if (Set-DdcBrightness (Get-HMonitor $sc) ([int]$st.original)) {
                        Clear-State $name
                        Write-Host ("[{0}] {1}  ddc restored to {2}" -f ($i + 1), $name, $st.original)
                    } else {
                        Write-Host ("[{0}] {1}  ddc restore FAILED" -f ($i + 1), $name)
                    }
                } else {
                    Write-Host ("[{0}] {1}  no saved ddc state" -f ($i + 1), $name)
                }
            }
        }
        exit 0
    }

    'unlock' {
        $icm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM'
        try {
            if (-not (Test-Path $icm)) { New-Item -Path $icm -Force | Out-Null }
            New-ItemProperty -Path $icm -Name 'GdiIcmGammaRange' -PropertyType DWord -Value 0 -Force | Out-Null
        } catch {
            Write-Host 'No admin rights - relaunching an elevated window (confirm the UAC prompt)...'
            Start-Process powershell -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-Command',
                "New-Item -Path '$icm' -Force | Out-Null; New-ItemProperty -Path '$icm' -Name GdiIcmGammaRange -PropertyType DWord -Value 0 -Force | Out-Null; Write-Host 'GdiIcmGammaRange = 0 set.'; Start-Sleep 2")
        }
        Write-Host 'OK. Full-range gamma unlocked for newly started processes.'
        Write-Host 'Reopen the terminal (or reboot once) and try a low value, e.g.: brightness.ps1 set 20'
        exit 0
    }

    default { Show-Usage; exit 1 }
}
