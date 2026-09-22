# Installer build (Phase 4, Plan 02; INST-01, NFR-02).
#
# Publishes the app as a win-x64 self-contained folder (no pdb, -p:Version stamped into the assemblies so the
# "startup version=" log line reports the release number) and compiles installer\setup.iss with Inno Setup 6 into
# <OutDir>\BingWallpaperUpdater-<Version>-x64-Setup.exe. Prints
# "BUILD-INSTALLER OK path=<absolute exe> bytes=<n> version=<v>" (exit 0) or "BUILD-INSTALLER FAIL: <reason>" plus
# the compiler output on a compile failure (exit 1). The same command runs locally and in the release workflow.
#
#   -Version      X.Y.Z (default 0.0.0); becomes AppVersion / OutputBaseFilename and the assembly version.
#   -OutDir       output folder for the Setup.exe (default dist, gitignored).
#   -SkipPublish  reuse the existing publish\ folder instead of running dotnet publish.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1 -Version 0.0.0
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1 -Version 1.2.3 -SkipPublish

param(
    [string]$Version = '0.0.0',
    [string]$OutDir = 'dist',
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'publish'
$scriptPath = Join-Path $repoRoot 'installer\setup.iss'
$minBytes   = 20MB
$maxBytes   = 60MB

function Fail([string]$reason) {
    Write-Host "BUILD-INSTALLER FAIL: $reason"
    exit 1
}

# ---- 1. inputs -------------------------------------------------------------------------------------------

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "-Version must be X.Y.Z (got '$Version')" }
if (-not (Test-Path $scriptPath)) { Fail "installer script missing: $scriptPath" }

if ([System.IO.Path]::IsPathRooted($OutDir)) {
    $outAbs = [System.IO.Path]::GetFullPath($OutDir)
} else {
    $outAbs = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutDir))
}

# ---- 2. publish ------------------------------------------------------------------------------------------

if (-not $SkipPublish) {
    Write-Host "Publishing version $Version to $publishDir"
    Push-Location $repoRoot
    try {
        & dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none "-p:Version=$Version" -o publish
        $publishExit = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($publishExit -ne 0) { Fail "dotnet publish exited with code $publishExit" }
}

$exePath = Join-Path $publishDir 'BingWallpaperUpdater.exe'
if (-not (Test-Path $exePath)) { Fail "publish output missing: $exePath (run without -SkipPublish)" }
$pdbs = @(Get-ChildItem -Path $publishDir -Filter *.pdb -Recurse -File -ErrorAction SilentlyContinue)
if ($pdbs.Count -gt 0) { Fail "publish folder contains $($pdbs.Count) .pdb file(s); publish with -p:DebugType=none" }

# ---- 3. compiler -----------------------------------------------------------------------------------------

# The fixed Inno Setup 6 path comes first so the compiler the release workflow pins (and asserts) is the one that
# runs; an `iscc` shim on PATH (Chocolatey) could otherwise point at whatever major the image happens to ship.
$iscc = $null
$candidate = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (Test-Path $candidate) {
    $iscc = $candidate
} else {
    $shim = Get-Command iscc -ErrorAction SilentlyContinue
    if ($null -ne $shim) { $iscc = $shim.Source }
}
if ($null -eq $iscc) { Fail 'ISCC.exe not found (no "Inno Setup 6\ISCC.exe" under Program Files (x86) and no iscc on PATH)' }
Write-Host "Using $iscc"

New-Item -ItemType Directory -Path $outAbs -Force | Out-Null
$isccArgs = @('/Qp', "/DAppVersion=$Version", "/DPublishDir=$publishDir", "/O$outAbs", $scriptPath)
$compilerOutput = @(& $iscc @isccArgs 2>&1 | ForEach-Object { [string]$_ })
$isccExit = $LASTEXITCODE
if ($isccExit -ne 0) {
    $compilerOutput | ForEach-Object { Write-Host $_ }
    Fail "ISCC exited with code $isccExit"
}
$warnings = @($compilerOutput | Where-Object { $_ -match '^Warning' })
$warnings | ForEach-Object { Write-Host $_ }

# ---- 4. output -------------------------------------------------------------------------------------------

$setupExe = Join-Path $outAbs "BingWallpaperUpdater-$Version-x64-Setup.exe"
if (-not (Test-Path $setupExe)) { Fail "expected output missing: $setupExe" }
$bytes = (Get-Item $setupExe).Length
if ($bytes -lt $minBytes -or $bytes -gt $maxBytes) { Fail "installer size $bytes bytes is outside the expected 20-60 MB range" }

Write-Host "BUILD-INSTALLER OK path=$setupExe bytes=$bytes version=$Version"
exit 0
