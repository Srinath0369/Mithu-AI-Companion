<#
.SYNOPSIS
  Verifies that while stuck to the top, dragging only moves Mithu left/right in every size:
    Compact: drag the face diagonally   -> only X changes, Y stays at the top
    Compact: drag the header diagonally -> only X changes
    Expanded: drag the face diagonally  -> only X changes
    Hidden:  drag the notch diagonally  -> only X changes (regression check)
    Face click (no movement) still reacts and does not move or resize.
#>
param([string]$Exe = "$PSScriptRoot\..\src\Mithu.App\bin\Release\net10.0-windows10.0.19041.0\Mithu.exe")

$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class D {
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
[void][D]::SetProcessDPIAware()

function Get-Rect([int]$processId) {
    $script:hit = $null
    [D]::EnumWindows({ param($h, $p)
        $owner = 0; [void][D]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $processId -and [D]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64; [void][D]::GetWindowText($h, $sb, 64)
            if ($sb.ToString() -eq 'Mithu') { $r = New-Object D+RECT; [void][D]::GetWindowRect($h, [ref]$r); $script:hit = $r; return $false }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:hit
}
function Down { [D]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero) }
function Up { [D]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero) }
function Click([int]$x, [int]$y) { [void][D]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 300; Down; Up }

# Diagonal drag: dx sideways AND dy downward, like the "random direction" the user saw.
function Drag([int]$x, [int]$y, [int]$dx, [int]$dy) {
    [void][D]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 300
    Down; Start-Sleep -Milliseconds 60
    for ($i = 1; $i -le 25; $i++) {
        [void][D]::SetCursorPos($x + [int]($dx * $i / 25), $y + [int]($dy * $i / 25)); Start-Sleep -Milliseconds 15
    }
    Start-Sleep -Milliseconds 60; Up
    Start-Sleep -Milliseconds 400
}

$results = New-Object System.Collections.Generic.List[string]
function Check([string]$name, $before, $after, [int]$expectDx) {
    $dx = $after.L - $before.L; $dy = $after.T - $before.T
    $sizeSame = (($after.R - $after.L) -eq ($before.R - $before.L)) -and (($after.B - $after.T) -eq ($before.B - $before.T))
    $ok = ([Math]::Abs($dx - $expectDx) -le 2) -and ($dy -eq 0) -and $sizeSame
    $results.Add(("{0,-30} dx={1,5} dy={2,3} size {3}  {4}" -f $name, $dx, $dy, $(if ($sizeSame) { 'same' } else { 'CHANGED' }), $(if ($ok) { 'PASS' } else { 'FAIL' })))
}

Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
for ($i = 0; $i -lt 20 -and (Get-Process Mithu -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
if (Get-Process Mithu -ErrorAction SilentlyContinue) { throw "Another Mithu instance is still running; close it and re-run." }
Start-Sleep -Milliseconds 400
$settingsPath = Join-Path $env:LOCALAPPDATA 'Mithu\settings.json'
$backup = "$settingsPath.ddragbak"
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
$log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }

try {
    @{ version = 3; dockToTop = $true; closeOnClickAway = $true; lastMode = 'Compact'; reducedMotion = $false } | ConvertTo-Json | Set-Content $settingsPath
    [void][D]::SetCursorPos(40, 900)
    $p = Start-Process (Resolve-Path $Exe).Path -PassThru
    $null = $p.WaitForInputIdle(10000)
    Start-Sleep -Milliseconds 1200

    # Geometry (DIP -> px). Large window = 420+28 wide; header centered 372 wide => header left = 14+24.
    $r = Get-Rect $p.Id
    $scale = ($r.R - $r.L) / 448.0
    function FacePt($w) { @(($w.L + [int]((14 + 34 + 28) * $scale)), ($w.T + [int](30 * $scale))) }
    function HeaderPt($w) { @(($w.L + [int]((14 + 24 + 160) * $scale)), ($w.T + [int](30 * $scale))) }

    # Click the face first so the window is active (clicking elsewhere would otherwise close it).
    $f = FacePt $r; Click $f[0] $f[1]; Start-Sleep -Milliseconds 300

    # Compact: drag face diagonally.
    $r0 = Get-Rect $p.Id; $f = FacePt $r0
    Drag $f[0] $f[1] -300 120
    $r1 = Get-Rect $p.Id; Check 'Compact: face drag (-300,+120)' $r0 $r1 -300

    # Compact: drag header straight, downward-diagonal and upward-diagonal (upward pushes the cursor off-screen).
    $h = HeaderPt $r1
    Drag $h[0] $h[1] 200 0
    $r1b = Get-Rect $p.Id; Check 'Compact: header drag (+200,0)' $r1 $r1b 200
    $h = HeaderPt $r1b
    Drag $h[0] $h[1] -200 80
    $r1c = Get-Rect $p.Id; Check 'Compact: header drag (-200,+80)' $r1b $r1c -200
    $h = HeaderPt $r1c
    Drag $h[0] $h[1] 200 -20
    $r1d = Get-Rect $p.Id; Check 'Compact: header drag (+200,-20)' $r1c $r1d 200
    $h = HeaderPt $r1d
    Drag $h[0] $h[1] -200 -80
    $r2 = Get-Rect $p.Id; Check 'Compact: header drag (-200,-80)*' $r1d $r2 -200

    # Expanded (via Ctrl+Down keeps window active), then drag the face diagonally.
    $f = FacePt $r2; Click $f[0] $f[1]; Start-Sleep -Milliseconds 200
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait('^{DOWN}'); Start-Sleep -Milliseconds 900
    $r3 = Get-Rect $p.Id; $f = FacePt $r3
    Drag $f[0] $f[1] 150 200
    $r4 = Get-Rect $p.Id; Check 'Expanded: face drag (+150,+200)' $r3 $r4 150

    # Face click without movement: must not move or resize.
    $f = FacePt $r4; Click $f[0] $f[1]; Start-Sleep -Milliseconds 400
    $r5 = Get-Rect $p.Id; Check 'Expanded: face click (no move)' $r4 $r5 0

    # Hidden: Esc twice -> notch; drag diagonally.
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 700
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 1200
    [void][D]::SetCursorPos(40, 900); Start-Sleep -Milliseconds 400
    $r6 = Get-Rect $p.Id
    Drag ([int](($r6.L + $r6.R) / 2)) ($r6.T + 8) -250 90
    $r7 = Get-Rect $p.Id; Check 'Hidden: notch drag (-250,+90)' $r6 $r7 -250

    $results
    "--- log ---"
    Get-Content $log | Select-Object -Skip $before | Select-String 'Notch moved|Size changed|Face clicked|Face punched' | ForEach-Object { '  ' + ($_.Line -replace '^.*?: ', '') }
    "Errors: " + @(Get-Content $log | Select-Object -Skip $before | Select-String '\[ERR\]|\[CRT\]').Count
}
finally {
    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
}
