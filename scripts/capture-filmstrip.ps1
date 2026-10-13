<#
.SYNOPSIS
  Records the open and close animations as filmstrips (one image per transition, frames side by side)
  so motion can be reviewed frame by frame. Also logs the OS window size per frame: during a transition
  it must NOT change (that was the source of the flicker).
.OUTPUTS
  artifacts/filmstrip-open.png, artifacts/filmstrip-close.png
#>
param(
    [string]$Exe = "$PSScriptRoot\..\src\Mithu.App\bin\Release\net10.0-windows10.0.19041.0\Mithu.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts",
    [int]$Frames = 12,
    [int]$IntervalMs = 50
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class F {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[void][F]::SetProcessDPIAware()

function Get-Rect([int]$processId) {
    $script:hit = $null
    [F]::EnumWindows({ param($h, $p)
        $owner = 0; [void][F]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $processId -and [F]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64; [void][F]::GetWindowText($h, $sb, 64)
            if ($sb.ToString() -eq 'Mithu') { $r = New-Object F+RECT; [void][F]::GetWindowRect($h, [ref]$r); $script:hit = $r; return $false }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:hit
}

function Click([int]$x, [int]$y) {
    [void][F]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 300
    [F]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); [F]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
}

# Captures $Frames frames of a fixed screen region into one horizontal strip.
function Record([string]$name, [System.Drawing.Rectangle]$region, [scriptblock]$trigger, [int]$processId) {
    # NB: PowerShell names are case-insensitive, so this must not be called $frames (clashes with -Frames).
    $shots = New-Object System.Collections.Generic.List[System.Drawing.Bitmap]
    $sizes = New-Object System.Collections.Generic.List[string]
    & $trigger
    $sw = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $Frames; $i++) {
        $target = $i * $IntervalMs
        while ($sw.ElapsedMilliseconds -lt $target) { Start-Sleep -Milliseconds 2 }
        $bmp = New-Object System.Drawing.Bitmap $region.Width, $region.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($region.X, $region.Y, 0, 0, $bmp.Size)
        $g.Dispose()
        $shots.Add($bmp)
        $r = Get-Rect $processId
        $sizes.Add(("{0}x{1}" -f ($r.R - $r.L), ($r.B - $r.T)))
    }

    $label = 18
    $strip = New-Object System.Drawing.Bitmap ($region.Width * $Frames), ($region.Height + $label)
    $g = [System.Drawing.Graphics]::FromImage($strip)
    $g.Clear([System.Drawing.Color]::FromArgb(40, 44, 52))
    $font = New-Object System.Drawing.Font 'Segoe UI', 9
    for ($i = 0; $i -lt $Frames; $i++) {
        $g.DrawImage($shots[$i], $i * $region.Width, $label)
        $g.DrawString(("{0} ms" -f ($i * $IntervalMs)), $font, [System.Drawing.Brushes]::White, ($i * $region.Width) + 4, 2)
        $g.DrawLine([System.Drawing.Pens]::DimGray, $i * $region.Width, 0, $i * $region.Width, $strip.Height)
        $shots[$i].Dispose()
    }
    $g.Dispose()
    $path = Join-Path $OutDir "filmstrip-$name.png"
    $strip.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $strip.Dispose()

    $distinct = @($sizes | Select-Object -Unique)
    "{0,-6} window sizes during animation: {1}" -f $name, ($distinct -join ' -> ')
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$settingsPath = Join-Path $env:LOCALAPPDATA 'Mithu\settings.json'
$backup = "$settingsPath.filmbak"
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }

try {
    @{ version = 3; dockToTop = $true; closeOnClickAway = $true; lastMode = 'Hidden'; lastVisibleMode = 'Expanded'; reducedMotion = $false } |
        ConvertTo-Json | Set-Content $settingsPath

    [void][F]::SetCursorPos(40, 900)
    $p = Start-Process (Resolve-Path $Exe).Path -PassThru
    $null = $p.WaitForInputIdle(10000)
    Start-Sleep -Milliseconds 1200

    $notch = Get-Rect $p.Id
    $cx = [int](($notch.L + $notch.R) / 2)

    # Fixed capture region: big enough for the expanded panel, centered on the notch.
    $scale = 1.0
    $w = [int](460 * 1.25); $h = [int](380 * 1.25)
    $region = New-Object System.Drawing.Rectangle ($cx - [int]($w / 2)), $notch.T, $w, $h

    Record 'open' $region { Click $cx ($notch.T + 6); [void][F]::SetCursorPos($cx, $notch.T + 200) } $p.Id
    Start-Sleep -Milliseconds 600
    Record 'close' $region { Click 40 900 } $p.Id

    $log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
    "Errors in log (last minute): " + @(Select-String -Path $log -Pattern '\[ERR\]|\[CRT\]' | Where-Object { $_.Line.Substring(11, 5) -ge (Get-Date).AddMinutes(-1).ToString('HH:mm') }).Count
}
finally {
    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
}
