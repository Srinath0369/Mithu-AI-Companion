<#
.SYNOPSIS
  End-to-end check of the face click reactions on the real app:
    1) single click            -> smile
    2) 4 rapid clicks          -> punches
    3) stop clicking           -> angry ~3 s after the last punch finishes
    4) wait                    -> calm again
  Captures a close-up of the face at each stage (artifacts/reactions.png) and prints timings from the log.
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
public static class R {
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
[void][R]::SetProcessDPIAware()

function Get-Rect([int]$processId) {
    $script:hit = $null
    [R]::EnumWindows({ param($h, $p)
        $owner = 0; [void][R]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $processId -and [R]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64; [void][R]::GetWindowText($h, $sb, 64)
            if ($sb.ToString() -eq 'Mithu') { $r = New-Object R+RECT; [void][R]::GetWindowRect($h, [ref]$r); $script:hit = $r; return $false }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:hit
}
$script:presses = New-Object System.Collections.Generic.List[string]
function Press() {
    [R]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); [R]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
    $script:presses.Add((Get-Date).ToString('HH:mm:ss.fff'))
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$settingsPath = Join-Path $env:LOCALAPPDATA 'Mithu\settings.json'
$backup = "$settingsPath.reactbak"
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
$log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }

$shots = New-Object System.Collections.Generic.List[object]
function Snap([string]$label) {
    $bmp = New-Object System.Drawing.Bitmap $script:region.Width, $script:region.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($script:region.X, $script:region.Y, 0, 0, $bmp.Size); $g.Dispose()
    $shots.Add(@($label, $bmp))
}

try {
    @{ version = 3; dockToTop = $true; closeOnClickAway = $true; lastMode = 'Hidden'; reducedMotion = $false } | ConvertTo-Json | Set-Content $settingsPath
    [void][R]::SetCursorPos(40, 900)
    $p = Start-Process (Resolve-Path $Exe).Path -PassThru
    $null = $p.WaitForInputIdle(10000)
    Start-Sleep -Milliseconds 1200

    # Open with a click on the notch.
    $r = Get-Rect $p.Id; $cx = [int](($r.L + $r.R) / 2)
    [void][R]::SetCursorPos($cx, $r.T + 6); Start-Sleep -Milliseconds 250; Press
    Start-Sleep -Milliseconds 900
    $r = Get-Rect $p.Id
    $scale = ($r.B - $r.T) / 370.0
    $fx = $r.L + [int]((14 + 34 + 28) * $scale); $fy = $r.T + [int](30 * $scale)
    $script:region = New-Object System.Drawing.Rectangle ($fx - [int](40 * $scale)), $r.T, ([int](300 * $scale)), ([int](60 * $scale))
    [void][R]::SetCursorPos($fx, $fy); Start-Sleep -Milliseconds 300
    Snap 'calm'

    # 1) Single click -> smile.
    Press; Start-Sleep -Milliseconds 300; Snap 'single click: smile'
    Start-Sleep -Milliseconds 1500

    # 2) Rapid clicks -> punches.
    for ($i = 0; $i -lt 4; $i++) { Press; Start-Sleep -Milliseconds 200 }
    Snap 'rapid clicks: punch'

    # 3) Stop: angry after the last punch finishes.
    Start-Sleep -Milliseconds 900; Snap 'stopped: angry'
    Start-Sleep -Milliseconds 1500; Snap 'still angry'

    # 4) Calm again.
    Start-Sleep -Milliseconds 2200; Snap 'calm again'

    # Compose a labelled strip.
    $label = 18
    $w = $script:region.Width; $h = $script:region.Height
    $strip = New-Object System.Drawing.Bitmap $w, (($h + $label) * $shots.Count)
    $g = [System.Drawing.Graphics]::FromImage($strip); $g.Clear([System.Drawing.Color]::FromArgb(40, 44, 52))
    $font = New-Object System.Drawing.Font 'Segoe UI', 9
    for ($i = 0; $i -lt $shots.Count; $i++) {
        $y = $i * ($h + $label)
        $g.DrawString($shots[$i][0], $font, [System.Drawing.Brushes]::White, 4, $y + 1)
        $g.DrawImage($shots[$i][1], 0, $y + $label)
        $shots[$i][1].Dispose()
    }
    $g.Dispose(); $strip.Save((Join-Path $OutDir 'reactions.png')); $strip.Dispose()

    "--- presses sent by this script ---"
    $script:presses | ForEach-Object { "$_  press" }
    "--- reaction timeline (from log) ---"
    $lines = Get-Content $log | Select-Object -Skip $before | Select-String 'Face clicked|Face punched|Face angry|Face calm|Size changed'
    $lines | ForEach-Object { $_.Line.Substring(11, 12) + '  ' + ($_.Line -replace '^.*?: ', '') }

    $lastPunch = $lines | Where-Object { $_.Line -match 'Face punched' } | Select-Object -Last 1
    $angry = $lines | Where-Object { $_.Line -match 'Face angry' } | Select-Object -First 1
    $calm = $lines | Where-Object { $_.Line -match 'Face calm' } | Select-Object -First 1
    function T($l) { [datetime]::ParseExact($l.Line.Substring(11, 12), 'HH:mm:ss.fff', $null) }
    if ($lastPunch -and $angry) { "Angry started {0:N2} s after the last punch" -f ((T $angry) - (T $lastPunch)).TotalSeconds }
    if ($angry -and $calm) { "Angry lasted  {0:N2} s" -f ((T $calm) - (T $angry)).TotalSeconds }
    "Errors: " + @(Get-Content $log | Select-Object -Skip $before | Select-String '\[ERR\]|\[CRT\]').Count
}
finally {
    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
}
