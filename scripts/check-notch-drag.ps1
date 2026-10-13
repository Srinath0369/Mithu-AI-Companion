<#
.SYNOPSIS
  Verifies the hidden notch can be dragged left/right along the top edge:
    1) drag left 400 px  -> moves left, stays at the top, does NOT open
    2) drag right 250 px with vertical wobble -> moves right, still at the top
    3) restart Mithu      -> same position (saved)
    4) plain click        -> opens the full panel
#>
param([string]$Exe = "$PSScriptRoot\..\src\Mithu.App\bin\Release\net10.0-windows10.0.19041.0\Mithu.exe")

$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class N {
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
[void][N]::SetProcessDPIAware()

function Get-Rect([int]$processId) {
    $script:hit = $null
    [N]::EnumWindows({ param($h, $p)
        $owner = 0; [void][N]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $processId -and [N]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64; [void][N]::GetWindowText($h, $sb, 64)
            if ($sb.ToString() -eq 'Mithu') { $r = New-Object N+RECT; [void][N]::GetWindowRect($h, [ref]$r); $script:hit = $r; return $false }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:hit
}
function Fmt($r) { "x={0,5} y={1} w={2} h={3}" -f $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T) }
function Down { [N]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero) }
function Up { [N]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero) }

# Smooth drag from (x,y) by (dx, wobbleY) in steps, like a real hand.
function Drag([int]$x, [int]$y, [int]$dx, [int]$wobble) {
    [void][N]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 300
    Down; Start-Sleep -Milliseconds 60
    $steps = 25
    for ($i = 1; $i -le $steps; $i++) {
        $wy = [int]($wobble * [Math]::Sin($i / $steps * [Math]::PI))
        [void][N]::SetCursorPos($x + [int]($dx * $i / $steps), $y + $wy)
        Start-Sleep -Milliseconds 15
    }
    Start-Sleep -Milliseconds 60
    Up
}

function Start-Mithu {
    [void][N]::SetCursorPos(40, 900)
    $p = Start-Process (Resolve-Path $Exe).Path -PassThru
    $null = $p.WaitForInputIdle(10000)
    Start-Sleep -Milliseconds 1300
    return $p
}

Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$settingsPath = Join-Path $env:LOCALAPPDATA 'Mithu\settings.json'
$backup = "$settingsPath.dragbak"
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
$log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }

try {
    @{ version = 3; dockToTop = $true; closeOnClickAway = $true; lastMode = 'Hidden'; reducedMotion = $false } | ConvertTo-Json | Set-Content $settingsPath
    $p = Start-Mithu

    $r0 = Get-Rect $p.Id; "Start           : $(Fmt $r0)"
    $cx = [int](($r0.L + $r0.R) / 2); $cy = $r0.T + 8

    Drag $cx $cy -400 0
    Start-Sleep -Milliseconds 500
    $r1 = Get-Rect $p.Id; "Drag left 400   : $(Fmt $r1)   moved {0} px" -f ($r1.L - $r0.L)

    $cx = [int](($r1.L + $r1.R) / 2)
    Drag $cx $cy 250 60
    Start-Sleep -Milliseconds 500
    $r2 = Get-Rect $p.Id; "Drag right 250  : $(Fmt $r2)   moved {0} px (with 60 px vertical wobble)" -f ($r2.L - $r1.L)

    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    $p = Start-Mithu
    $r3 = Get-Rect $p.Id; "After restart   : $(Fmt $r3)"

    $cx = [int](($r3.L + $r3.R) / 2)
    [void][N]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 300; Down; Up
    Start-Sleep -Milliseconds 900
    $r4 = Get-Rect $p.Id; "Plain click     : $(Fmt $r4)"

    "--- log ---"
    Get-Content $log | Select-Object -Skip $before | Select-String 'Notch moved|Size changed' | ForEach-Object { '  ' + ($_.Line -replace '^.*?: ', '') }
    "Errors: " + @(Get-Content $log | Select-Object -Skip $before | Select-String '\[ERR\]|\[CRT\]').Count

    "--- verdict ---"
    "Moved left           : " + ($(if (($r0.L - $r1.L) -ge 350) { 'PASS' } else { 'FAIL' }))
    "Stayed at top        : " + ($(if ($r1.T -eq $r0.T -and $r2.T -eq $r0.T) { 'PASS' } else { 'FAIL' }))
    "Stayed hidden (drag) : " + ($(if (($r1.R - $r1.L) -eq ($r0.R - $r0.L) -and ($r2.R - $r2.L) -eq ($r0.R - $r0.L)) { 'PASS' } else { 'FAIL' }))
    "Moved right          : " + ($(if (($r2.L - $r1.L) -ge 200) { 'PASS' } else { 'FAIL' }))
    "Position saved       : " + ($(if ([Math]::Abs($r3.L - $r2.L) -le 2) { 'PASS' } else { 'FAIL' }))
    "Click still opens    : " + ($(if (($r4.B - $r4.T) -gt 300) { 'PASS' } else { 'FAIL' }))
}
finally {
    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
}
