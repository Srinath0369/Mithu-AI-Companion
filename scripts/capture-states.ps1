<#
.SYNOPSIS
  Visual check: launches Mithu, drives it through Hidden / Compact / Expanded with the mouse and
  keyboard, and saves a screenshot of the top-center screen region for each state to artifacts/.
  Also reports the real window rectangle. Restores the user's settings afterwards.
#>
param(
    [string]$Exe = "$PSScriptRoot\..\src\Mithu.App\bin\Release\net10.0-windows10.0.19041.0\Mithu.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts",
    [switch]$Floating
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class U {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
}
"@
[void][U]::SetProcessDPIAware()

function Get-Rect([int]$processId) {
    $script:hit = $null
    [U]::EnumWindows({ param($h, $p)
        $owner = 0; [void][U]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $processId -and [U]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64; [void][U]::GetWindowText($h, $sb, 64)
            if ($sb.ToString() -eq 'Mithu') {   # the companion window, not tooltips/menus
                $r = New-Object U+RECT; [void][U]::GetWindowRect($h, [ref]$r)
                $script:hit = $r; return $false
            }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $script:hit
}

# Waits until the window rect is unchanged for 250 ms (max 4 s). Returns the rect; prints settle time.
function Wait-Stable([int]$processId) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $last = Get-Rect $processId; $stableSince = 0
    while ($sw.ElapsedMilliseconds -lt 4000) {
        Start-Sleep -Milliseconds 50
        $now = Get-Rect $processId
        if ($now.L -eq $last.L -and $now.T -eq $last.T -and $now.R -eq $last.R -and $now.B -eq $last.B) {
            if ($sw.ElapsedMilliseconds - $stableSince -ge 250) { break }
        } else { $stableSince = $sw.ElapsedMilliseconds; $last = $now }
    }
    $script:settleMs = $stableSince
    return $last
}

function Shot([string]$name, $rect) {
    $pad = 30
    $x = [Math]::Max(0, $rect.L - $pad); $y = [Math]::Max(0, $rect.T - 4)
    $w = ($rect.R - $rect.L) + 2 * $pad; $h = ($rect.B - $rect.T) + $pad
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "{0,-22} window x={1} y={2} w={3} h={4}  settled after ~{5} ms" -f $name, $rect.L, $rect.T, ($rect.R - $rect.L), ($rect.B - $rect.T), $script:settleMs
}

function Click([int]$x, [int]$y) {
    [void][U]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 400
    [U]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); [U]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$settingsPath = Join-Path $env:LOCALAPPDATA 'Mithu\settings.json'
$backup = "$settingsPath.capbak"
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }

try {
    $dock = -not $Floating
    $suffix = if ($Floating) { '-floating' } else { '' }
    @{ version = 3; dockToTop = $dock; closeOnClickAway = $true; lastMode = 'Compact'; lastVisibleMode = 'Expanded' } |
        ConvertTo-Json | Set-Content $settingsPath

    [void][U]::SetCursorPos(40, 900)
    $p = Start-Process (Resolve-Path $Exe).Path -PassThru
    $null = $p.WaitForInputIdle(10000)
    Start-Sleep -Milliseconds 600
    $r = Wait-Stable $p.Id; Shot "1-compact$suffix" $r

    # Header buttons from the right edge (DIP): pad 14 + margin 12, buttons 30 wide with 2 gaps.
    # Centers: down = 41, up = 73, options = 105 DIP from the window's right edge; y = 30 DIP.
    $scale = ($r.B - $r.T) / 74.0   # compact window = 60 body + 14 pad DIP
    function BtnX($rect, [double]$fromRight) { $rect.R - [int]($fromRight * $scale) }
    $btnY = $r.T + [int](30 * $scale)

    Click (BtnX $r 41) $btnY                      # ▼ Compact -> Expanded
    [void][U]::SetCursorPos([int](($r.L + $r.R) / 2), $r.T + [int](70 * $scale)) # off the button: no tooltip
    $r = Wait-Stable $p.Id; Shot "2-expanded$suffix" $r

    Click (BtnX $r 73) $btnY                      # ▲ Expanded -> Compact
    [void][U]::SetCursorPos([int](($r.L + $r.R) / 2) - 120, $btnY)
    $r = Wait-Stable $p.Id; Shot "3-compact-again$suffix" $r

    Click (BtnX $r 73) $btnY                      # ▲ Compact -> Hidden
    [void][U]::SetCursorPos(40, 900)
    $r = Wait-Stable $p.Id; Shot "4-hidden$suffix" $r

    if ($dock) {
        # Hover the notch for 1.5 s: must NOT open (hover is feedback only).
        [void][U]::SetCursorPos([int](($r.L + $r.R) / 2), $r.T + 6)
        Start-Sleep -Milliseconds 1500
        $script:settleMs = 1500; Shot "5-hover-stays-hidden" (Get-Rect $p.Id)

        # Click the notch: opens the full panel.
        Click ([int](($r.L + $r.R) / 2)) ($r.T + 6)
        [void][U]::SetCursorPos([int](($r.L + $r.R) / 2), $r.T + [int](200 * $scale))
        $r = Wait-Stable $p.Id; Shot "6-click-opens-full" $r

        # Click somewhere else on the desktop: closes back to the notch.
        Click 40 900
        Start-Sleep -Milliseconds 300
        $r = Wait-Stable $p.Id; Shot "7-click-away-closes" $r
    }

    $log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
    $errors = @(Select-String -Path $log -Pattern '\[ERR\]|\[CRT\]' | Where-Object { $_.Line.Substring(11, 5) -ge (Get-Date).AddMinutes(-1).ToString('HH:mm') })
    "Errors in log (last minute): $($errors.Count)"
    "--- size changes ---"
    Select-String -Path $log -Pattern 'Size changed|Shell started' | Select-Object -Last 8 | ForEach-Object { $_.Line.Substring(11, 12) + '  ' + ($_.Line -replace '^.*?: ', '') }
    $errors | Select-Object -First 3 | ForEach-Object { $_.Line }
}
finally {
    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
}
