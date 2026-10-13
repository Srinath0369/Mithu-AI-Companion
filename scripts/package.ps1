# Builds Mithu's installer with Velopack (docs/packaging.md).
#   powershell -File scripts\package.ps1                 # version from Directory.Build.props
#   powershell -File scripts\package.ps1 -Version 1.0.1  # a specific version
#   powershell -File scripts\package.ps1 -Force          # rebuild the same version (clears earlier output)
#   powershell -File scripts\package.ps1 -UpdateUrl https://example.com/mithu   # installed copies update from there
# Output in artifacts\releases: MithuCompanion-win-Setup.exe (the installer), a portable .zip, the full .nupkg (and a
# delta from the previous release, when there is one) and releases.win.json (what an update server would host).
# Needs the vpk tool once: dotnet tool install -g vpk
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    # Rebuilding a version that was already packed (local testing): clears artifacts\releases first. Real releases
    # never need this; give them a higher version instead (updates only go up).
    [switch]$Force,
    # Where installed copies look for updates: the web address (or folder) that will host the files in
    # artifacts\releases, e.g. https://example.com/mithu or a GitHub Releases page. Empty: updates stay off.
    [string]$UpdateUrl = ''
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

# Velopack installs into %LOCALAPPDATA%\<PackId> and deletes that folder on uninstall, so the id must never be
# "Mithu": that is where the user's notes, reminders and settings live (%LOCALAPPDATA%\Mithu).
$packId = 'MithuCompanion'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' isn't SemVer (like 1.0.0)." }

$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    $vpk = Get-Command (Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe') -ErrorAction SilentlyContinue
}
if (-not $vpk) { throw "vpk not found. Install it once: dotnet tool install -g vpk" }

if (-not $SkipTests) {
    Write-Host "== Tests"
    dotnet test tests\Mithu.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: no package built." }
}

$publish = Join-Path $root "artifacts\publish\$Runtime"
$releases = Join-Path $root 'artifacts\releases'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
if ($Force -and (Test-Path $releases)) { Remove-Item -Recurse -Force $releases }

Write-Host "== Publish $Version ($Runtime, self-contained, ReadyToRun)"
dotnet publish src\Mithu.App -c Release -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none "-p:MithuUpdateUrl=$UpdateUrl" -o $publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host "== Pack"
& $vpk.Source pack --packId $packId --packVersion $Version --packDir $publish --mainExe Mithu.exe `
    --packTitle Mithu --packAuthors Mithu --icon (Join-Path $root 'assets\Mithu.ico') `
    --runtime $Runtime --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host ""
Get-ChildItem $releases -File | Where-Object { $_.Name -like "*$Version*" -or $_.Name -like '*Setup*' -or $_.Name -like 'releases.*' } |
    ForEach-Object { "{0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"Installed size: {0:N0} MB" -f ((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
