# Local full-stack smoke run for the walking skeleton.
#
# Launches publish\BingWallpaperUpdater.exe, waits for the "apply ok" log line, asserts the read-back
# path and Fill position, the cache index, the absence of a window, and the single-instance guard,
# then stops the process. Prints "SMOKE OK" (exit 0) or "SMOKE FAIL: <reason>" (exit 1).
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/smoke-run.ps1

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$exePath    = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$appRoot    = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath    = Join-Path $appRoot 'log.txt'
$indexPath  = Join-Path $appRoot 'cache\index.json'
$procName   = 'BingWallpaperUpdater'
$timeoutSec = 120

function Stop-App {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { }
    }
}

function Fail([string]$reason) {
    Write-Host "SMOKE FAIL: $reason"
    if (Test-Path $logPath) {
        Write-Host '--- last 30 log lines ---'
        Get-Content $logPath -Tail 30 | ForEach-Object { Write-Host $_ }
    }
    Stop-App
    exit 1
}

if (-not (Test-Path $exePath)) { Fail "publish\BingWallpaperUpdater.exe is missing - run dotnet publish first" }

Stop-App
if (Test-Path $logPath) { Remove-Item $logPath -Force }

Write-Host "Starting $exePath"
$proc = Start-Process -FilePath $exePath -PassThru
$deadline = (Get-Date).AddSeconds($timeoutSec)
$applyLine = $null

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    if ($proc.HasExited) { Fail "process exited early with code $($proc.ExitCode)" }
    if (-not (Test-Path $logPath)) { continue }
    $lines = Get-Content $logPath -ErrorAction SilentlyContinue
    if (-not $lines) { continue }
    $bad = $lines | Where-Object { $_ -match ' pipeline failed ' -or $_ -match ' apply failed ' } | Select-Object -First 1
    if ($bad) { Fail "failure logged: $bad" }
    $applyLine = $lines | Where-Object { $_ -match ' INFO apply ok ' } | Select-Object -First 1
    if ($applyLine) { break }
}

if (-not $applyLine) { Fail "no 'apply ok' line within $timeoutSec s" }
Write-Host "Apply line: $applyLine"

# apply ok method=com id=<id> path=<path> readback=<path or -> position=<pos or ->
if ($applyLine -notmatch ' apply ok method=(?<method>\S+) id=(?<id>\S+) path=(?<path>.+?) readback=(?<readback>.+?) position=(?<position>\S+)\s*$') {
    Fail "could not parse apply line"
}
$method   = $Matches['method']
$imageId  = $Matches['id']
$path     = $Matches['path']
$readback = $Matches['readback']
$position = $Matches['position']

if ($readback -eq '-' -or $path.ToLowerInvariant() -ne $readback.ToLowerInvariant()) { Fail "read-back mismatch: path=$path readback=$readback" }
if ($position -ne 'DWPOS_FILL') { Fail "position is '$position', expected DWPOS_FILL" }
if (-not (Test-Path $path)) { Fail "applied file does not exist: $path" }

if (-not (Test-Path $indexPath)) { Fail "cache\index.json missing" }
try { $index = Get-Content $indexPath -Raw | ConvertFrom-Json } catch { Fail "cache\index.json does not parse: $($_.Exception.Message)" }
if (-not $index.images -or @($index.images).Count -lt 1) { Fail "index.json lists no images" }
if (-not (@($index.applied) -contains $imageId)) { Fail "index.json applied does not contain $imageId" }

$running = Get-Process -Name $procName -ErrorAction SilentlyContinue
if (-not $running) { Fail "process is not running after apply" }
if (@($running).Count -ne 1) { Fail "expected 1 process after apply, found $(@($running).Count)" }
$running = $running | Select-Object -First 1
$running.Refresh()
if ($running.MainWindowHandle -ne 0) { Fail "MainWindowHandle is $($running.MainWindowHandle), expected 0 (no window)" }

Write-Host 'Launching a second instance'
$second = Start-Process -FilePath $exePath -PassThru
Start-Sleep -Seconds 3
$count = @(Get-Process -Name $procName -ErrorAction SilentlyContinue).Count
if ($count -ne 1) { Fail "expected exactly 1 process after second launch, found $count" }
if (-not $second.HasExited) { Fail "second instance is still running" }
if ($second.ExitCode -ne 0) { Fail "second instance exited with code $($second.ExitCode), expected 0" }

Stop-App
Write-Host "method=$method id=$imageId"
Write-Host 'SMOKE OK'
exit 0
