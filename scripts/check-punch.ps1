<#
.SYNOPSIS
  Verifies the face-click "punch": in Compact and Expanded, clicking the face must NOT change size,
  and must play a squash (captured as a close-up filmstrip). Clicking the notch must open Expanded.
.OUTPUTS
  artifacts/filmstrip-punch.png
#>
param(
    [string]$Exe = "$PSScriptRoot\..\src\Mithu.App\bin\Release\net10.0-windows10.0.19041.0\Mithu.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class P {
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
[void][P]::SetProcessDPIAware()

function Get-Rect([int]$processId) {
    $script:hit = $null
    [P]::EnumWindows({ param($h, $p)
        $owner = 0; [void][P]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $processId -and [P]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64; [void][P]::GetWindowText($h, $sb, 64)
            if ($sb.ToString() -eq 'Mithu') { $r = New-Object P+RECT; [void][P]::GetWindowRect($h, [ref]$r); $script:hit = $r; return $false }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:hit
}
function Size($r) { "{0}x{1}" -f ($r.R - $r.L), ($r.B - $r.T) }
function Click([int]$x, [int]$y) {
    [void][P]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 250
    [P]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); [P]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$settingsPath = Join-Path $env:LOCALAPPDATA 'Mithu\settings.json'
$backup = "$settingsPath.punchbak"
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
$log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }

try {
    @{ version = 3; dockToTop = $true; closeOnClickAway = $true; lastMode = 'Hidden'; reducedMotion = $false } | ConvertTo-Json | Set-Content $settingsPath
    [void][P]::SetCursorPos(40, 900)
    $p = Start-Process (Resolve-Path $Exe).Path -PassThru
    $null = $p.WaitForInputIdle(10000)
    Start-Sleep -Milliseconds 1200

    # 1) Click the hidden tab -> must open Expanded.
    $r = Get-Rect $p.Id; "Hidden            : $(Size $r)"
    $cx = [int](($r.L + $r.R) / 2)
    Click $cx ($r.T + 6)
    Start-Sleep -Milliseconds 900
    $r = Get-Rect $p.Id; "Click hidden tab  : $(Size $r)  (expect expanded)"

    # Face center: window left + (shadow 14 + content x 34 + 28) DIP, top + (7 + 23) DIP.
    $scale = ($r.B - $r.T) / 370.0
    $fx = $r.L + [int]((14 + 34 + 28) * $scale); $fy = $r.T + [int](30 * $scale)

    # 2) Click the face in Expanded -> punch, size unchanged. Record a close-up filmstrip.
    $region = New-Object System.Drawing.Rectangle ($fx - [int](50 * $scale)), ($r.T), ([int](100 * $scale)), ([int](64 * $scale))
    $shots = New-Object System.Collections.Generic.List[System.Drawing.Bitmap]
    Click $fx $fy
    $sw = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 10; $i++) {
        while ($sw.ElapsedMilliseconds -lt ($i * 40)) { Start-Sleep -Milliseconds 2 }
        $bmp = New-Object System.Drawing.Bitmap $region.Width, $region.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($region.X, $region.Y, 0, 0, $bmp.Size); $g.Dispose()
        $shots.Add($bmp)
    }
    Start-Sleep -Milliseconds 400
    $r2 = Get-Rect $p.Id; "Click face (exp.) : $(Size $r2)  (expect unchanged)"

    $label = 16
    $strip = New-Object System.Drawing.Bitmap ($region.Width * $shots.Count), ($region.Height + $label)
    $g = [System.Drawing.Graphics]::FromImage($strip); $g.Clear([System.Drawing.Color]::FromArgb(40, 44, 52))
    $font = New-Object System.Drawing.Font 'Segoe UI', 8
    for ($i = 0; $i -lt $shots.Count; $i++) {
        $g.DrawImage($shots[$i], $i * $region.Width, $label)
        $g.DrawString(("{0} ms" -f ($i * 40)), $font, [System.Drawing.Brushes]::White, ($i * $region.Width) + 3, 1)
        $shots[$i].Dispose()
    }
    $g.Dispose(); $strip.Save((Join-Path $OutDir 'filmstrip-punch.png')); $strip.Dispose()

    # 3) Go to Compact with the ▲ button, then click the face -> size unchanged.
    # Header is 372 wide, centered in 420 content + 14 shadow pad => header right edge = window right - (14 + 24).
    # ▼ centre is 12 + 15 from the header's right edge; ▲ is 32 further left.
    $headerRight = $r2.R - [int]((14 + 24) * $scale)
    $upX = $headerRight - [int]((12 + 15 + 32) * $scale); $btnY = $r2.T + [int](30 * $scale)
    Click $upX $btnY
    Start-Sleep -Milliseconds 900
    $r3 = Get-Rect $p.Id
    Click $fx $fy
    Start-Sleep -Milliseconds 700
    $r4 = Get-Rect $p.Id; "Click face (comp.): $(Size $r4)  (same window as before: $(Size $r3))"

    "--- size changes ---"
    Get-Content $log | Select-Object -Skip $before | Select-String 'Size changed|Face punched' | ForEach-Object { '  ' + ($_.Line -replace '^.*?: ', '') }
    "Errors: " + @(Get-Content $log | Select-Object -Skip $before | Select-String '\[ERR\]|\[CRT\]').Count
}
finally {
    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
}
