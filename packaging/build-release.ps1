<#
.SYNOPSIS
    Builds the shippable Xtream Player payload, and optionally the installer.

.DESCRIPTION
    Publishes a self-contained win-x64 build to dist/XtreamPlayer, then compiles
    the Inno Setup installer if iscc is on PATH.

    Self-contained means no .NET install is required on the target machine, and
    libvlc ships with the app rather than being borrowed from whatever VLC the
    user happens to have — version skew against a user's own VLC is a support
    problem with no good answer.

.PARAMETER SkipInstaller
    Publish only. Useful when iterating on the payload.

.PARAMETER SkipTests
    Skip the test run. Only for a quick local build.

.EXAMPLE
    pwsh packaging/build-release.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$distRoot = Join-Path $repoRoot 'dist'
$payload  = Join-Path $distRoot 'XtreamPlayer'

Write-Host "Repository : $repoRoot"
Write-Host "Output     : $payload"
Write-Host ''

if (-not $SkipTests) {
    Write-Host '=== Tests ===' -ForegroundColor Cyan
    dotnet test (Join-Path $repoRoot 'XtreamPlayer.slnx') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; not building a release." }
    Write-Host ''
}

Write-Host '=== Publish ===' -ForegroundColor Cyan

# A stale payload would keep files that are no longer produced, which is how a
# renamed assembly ends up shipping alongside its replacement.
if (Test-Path $payload) {
    Remove-Item -Recurse -Force $payload
}

dotnet publish (Join-Path $repoRoot 'src/XtreamPlayer.App/XtreamPlayer.App.csproj') `
    -c Release -r win-x64 --self-contained true -o $payload --nologo

if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $payload 'XtreamPlayer.exe'
if (-not (Test-Path $exe)) { throw "Publish produced no executable at $exe." }

# libvlc scans this directory at init. If the trim target ever over-reaches,
# playback fails at runtime with a message that explains nothing, so it is
# checked here instead.
$plugins = Join-Path $payload 'libvlc/win-x64/plugins'
if (-not (Test-Path $plugins)) { throw "libvlc plugins are missing from $payload." }

$pluginCount = (Get-ChildItem $plugins -Recurse -Filter *.dll).Count
if ($pluginCount -lt 100) { throw "Only $pluginCount libvlc plugins were published; expected several hundred." }

$sizeMb = [math]::Round(((Get-ChildItem $payload -Recurse | Measure-Object Length -Sum).Sum / 1MB), 0)

Write-Host ''
Write-Host "  executable : $exe"
Write-Host "  plugins    : $pluginCount"
Write-Host "  size       : $sizeMb MB"
Write-Host ''

# Proves the runtime, DI graph, SQLite and libvlc all initialise. Exit code 2 is
# the importer rejecting a path that does not exist, which is the point: getting
# that far means everything before it worked.
Write-Host '=== Smoke test ===' -ForegroundColor Cyan
& $exe --import (Join-Path ([System.IO.Path]::GetTempPath()) 'definitely-not-a-database.sqlite') | Out-Null

if ($LASTEXITCODE -ne 2) {
    throw "Smoke test returned $LASTEXITCODE; expected 2 (importer reached, path rejected)."
}

Write-Host '  startup OK' -ForegroundColor Green
Write-Host ''

if ($SkipInstaller) {
    Write-Host 'Skipping the installer.' -ForegroundColor Yellow
    return
}

# Inno Setup does not add itself to PATH, and winget installs it per-user, so
# looking only on PATH silently skips the installer on a machine that has it.
$isccPath = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

$iscc = if ($isccPath) { [pscustomobject]@{ Source = $isccPath } } else { $null }

if (-not $iscc) {
    Write-Host 'Inno Setup (iscc) is not on PATH, so no installer was built.' -ForegroundColor Yellow
    Write-Host 'Install it from https://jrsoftware.org/isdl.php, or ship the folder as-is.'
    return
}

Write-Host '=== Installer ===' -ForegroundColor Cyan
& $iscc.Source (Join-Path $PSScriptRoot 'XtreamPlayer.iss')

if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

Get-ChildItem (Join-Path $distRoot '*.exe') |
    ForEach-Object { Write-Host ("  {0}  ({1:N0} MB)" -f $_.Name, ($_.Length / 1MB)) }
