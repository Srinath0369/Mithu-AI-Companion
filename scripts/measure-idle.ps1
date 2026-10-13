<#
.SYNOPSIS
  Launches the Release build, waits for warm-up, then measures idle CPU and memory.
  Results feed docs/perf.md. Don't touch the companion while it runs.
.EXAMPLE
  ./scripts/measure-idle.ps1 -WarmupSeconds 20 -SampleSeconds 120
#>
param(
    [int]$WarmupSeconds = 20,
    [int]$SampleSeconds = 120,
    [int]$Samples = 1,
    [string]$Exe = "$PSScriptRoot\..\src\Mithu.App\bin\Release\net10.0-windows10.0.19041.0\Mithu.exe"
)

$ErrorActionPreference = 'Stop'

Get-Process Mithu -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$log = Join-Path $env:LOCALAPPDATA "Mithu\logs\mithu-$(Get-Date -Format yyyyMMdd).log"
$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }

$exePath = (Resolve-Path $Exe).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $exePath -PassThru
$null = $p.WaitForInputIdle(10000)
$startupMs = $sw.ElapsedMilliseconds

Start-Sleep -Seconds $WarmupSeconds
$cores = [Environment]::ProcessorCount

# Consecutive windows show whether CPU is a warm-up effect (falls) or a steady cost (flat).
$p.Refresh(); $start = $p.TotalProcessorTime; $c0 = $start
for ($i = 1; $i -le $Samples; $i++) {
    Start-Sleep -Seconds $SampleSeconds
    $p.Refresh(); $c1 = $p.TotalProcessorTime
    $ms = ($c1 - $c0).TotalMilliseconds
    "Sample {0}: {1,6:N0} ms CPU  ({2:N3} % of one core)" -f $i, $ms, ($ms / ($SampleSeconds * 1000) * 100)
    $c0 = $c1
}

$cpuMs = ($c1 - $start).TotalMilliseconds
$SampleSeconds = $SampleSeconds * $Samples

[pscustomobject]@{
    StartupToInputIdleMs = $startupMs
    SampleSeconds        = $SampleSeconds
    CpuTimeMs            = [math]::Round($cpuMs)
    CpuPercentOfMachine  = [math]::Round($cpuMs / ($SampleSeconds * 1000) / $cores * 100, 3)
    CpuPercentOfOneCore  = [math]::Round($cpuMs / ($SampleSeconds * 1000) * 100, 3)
    WorkingSetMB         = [math]::Round($p.WorkingSet64 / 1MB, 1)
    PrivateBytesMB       = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    Handles              = $p.HandleCount
    Threads              = $p.Threads.Count
} | Format-List

# An idle measurement is only valid if nobody used the companion while it ran.
$interactions = if (Test-Path $log) {
    @(Get-Content $log | Select-Object -Skip $before | Select-String 'Size changed').Count
} else { 0 }
if ($interactions -gt 1) {
    Write-Warning "NOT IDLE: $interactions size changes were logged during the run (someone used Mithu). Discard these numbers and re-run without touching it."
} else {
    "Idle check: OK ($interactions size change(s) logged — startup safety-net close only)"
}

"--- new log lines ---"
if (Test-Path $log) { Get-Content $log | Select-Object -Skip $before }
