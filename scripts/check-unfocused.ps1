<#
.SYNOPSIS
  Safety-net check: Mithu launches open (never focused, pointer far away) and must close to the
  notch by itself after ClickAwayPolicy.UnfocusedTimeout (3 s). Restores settings afterwards.
#>
param([string]$Exe = "$PSScriptRoot\..\src\Mithu.App\bin\Release\net10.0-windows10.0.19041.0\Mithu.exe")

$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class V {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@

Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$settingsPath = Join-Path $env:LOCALAPPDATA 'Mithu\settings.json'
$backup = "$settingsPath.unfbak"
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
$log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }

try {
    @{ version = 3; dockToTop = $true; closeOnClickAway = $true; lastMode = 'Expanded' } | ConvertTo-Json | Set-Content $settingsPath
    [void][V]::SetCursorPos(40, 900)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process (Resolve-Path $Exe).Path -PassThru
    $null = $p.WaitForInputIdle(10000)
    Start-Sleep -Seconds 5

    $lines = @(Get-Content $log | Select-Object -Skip $before | Select-String 'Shell started|Size changed')
    $lines | ForEach-Object { $_.Line.Substring(11, 12) + '  ' + ($_.Line -replace '^.*?: ', '') }

    $started = $lines | Where-Object { $_.Line -match 'Shell started' } | Select-Object -First 1
    $hidden = $lines | Where-Object { $_.Line -match 'Size changed to Hidden' } | Select-Object -First 1
    if ($started -and $hidden) {
        $t0 = [datetime]::ParseExact($started.Line.Substring(11, 12), 'HH:mm:ss.fff', $null)
        $t1 = [datetime]::ParseExact($hidden.Line.Substring(11, 12), 'HH:mm:ss.fff', $null)
        "PASS: closed to the notch {0:N1} s after launch (expected ~3 s)" -f ($t1 - $t0).TotalSeconds
    } else {
        "FAIL: did not close by itself within 5 s"
    }
}
finally {
    Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
}
