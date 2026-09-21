# Cross-process Settings-window probe (Phase 3, Plan 01; UI-01, UI-02, UI-06).
#
# Proves, without seeing the screen, that a second launch of the exe signals the primary instance to open exactly
# one Settings window (the primary's MainWindowHandle flips from 0 to non-zero, the process count stays 1, the
# second process exits 0 at once) and that closing that window (WM_CLOSE via Process.CloseMainWindow) leaves the
# tray process alive with no window and the "settings window action=close" log line. Prints
# "SETTINGS PROBE OK open-after=<ms>ms" (exit 0) or "SETTINGS PROBE FAIL: <reason>" plus the last 20 log lines (exit 1).
#
#   -NoLaunch      use the already-running BingWallpaperUpdater process and leave it running afterwards.
#                  Default: stop any running instance, launch publish\BingWallpaperUpdater.exe, wait up to 60 s for
#                  the "schedule start" log line, and stop the process at the end.
#   -TimeoutSec    how long to wait for the window to appear after the second launch (default 15).
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/settings-probe.ps1

param(
    [switch]$NoLaunch,
    [int]$TimeoutSec = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$exePath   = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$data      = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath   = Join-Path $data 'log.txt'
$procName  = 'BingWallpaperUpdater'
$openToken  = 'settings window action=open'
$closeToken = 'settings window action=close'

$script:launched = $false

# ---- helpers ---------------------------------------------------------------------------------------------

function Stop-App {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { }
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 200
    }
}

function Read-Log {
    if (-not (Test-Path $logPath)) { return @() }
    return @(Get-Content $logPath -ErrorAction SilentlyContinue)
}

function Fail([string]$reason) {
    Write-Host "SETTINGS PROBE FAIL: $reason"
    $lines = @(Read-Log)
    if ($lines.Count -gt 0) {
        Write-Host '--- last 20 log lines ---'
        $lines | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
    }
    if ($script:launched) { Stop-App }
    exit 1
}

function Wait-LogLine {
    param(
        [Parameter(Mandatory = $true)] [string]$Pattern,
        [int]$SkipLines = 0,
        [int]$Timeout = 60,
        [System.Diagnostics.Process]$Process = $null
    )
    $deadline = (Get-Date).AddSeconds($Timeout)
    while ((Get-Date) -lt $deadline) {
        if ($null -ne $Process -and $Process.HasExited) { Fail "process exited early with code $($Process.ExitCode)" }
        $lines = @(Read-Log)
        if ($lines.Count -gt $SkipLines) {
            $fresh = @($lines | Select-Object -Skip $SkipLines)
            $hit = @($fresh | Where-Object { $_ -match $Pattern }) | Select-Object -First 1
            if ($hit) { return $hit }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

# ---- 1. process ------------------------------------------------------------------------------------------

$proc = $null
if ($NoLaunch) {
    $running = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { Fail "-NoLaunch given but no $procName process is running" }
    $proc = $running | Select-Object -First 1
    Write-Host "Using running process pid=$($proc.Id)"
} else {
    if (-not (Test-Path $exePath)) { Fail "publish\BingWallpaperUpdater.exe is missing - run dotnet publish first" }
    Stop-App
    $before = @(Read-Log).Count
    Write-Host "Starting $exePath"
    $proc = Start-Process -FilePath $exePath -PassThru
    $script:launched = $true
    $startLine = Wait-LogLine -Pattern ' INFO schedule start ' -SkipLines $before -Timeout 60 -Process $proc
    if (-not $startLine) { Fail "no 'schedule start' log line within 60 s" }
    Write-Host "Start line: $startLine"
}

$p = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
if ($p.Count -ne 1) { Fail "expected exactly one $procName process before the probe, found $($p.Count)" }
$p[0].Refresh()
if ($p[0].MainWindowHandle -ne 0) { Fail "the tray process already has a main window (handle $($p[0].MainWindowHandle)); close Settings first" }

# ---- 2. second launch -> Show signal ---------------------------------------------------------------------

$skip = @(Read-Log).Count
$launched2 = [System.Diagnostics.Stopwatch]::StartNew()
$second = Start-Process -FilePath $exePath -PassThru
if (-not $second.WaitForExit(10000)) { Fail 'the second instance did not exit within 10 s' }
if ($second.ExitCode -ne 0) { Fail "the second instance exited with code $($second.ExitCode), expected 0" }
Write-Host "Second instance exited 0 after $([int]$launched2.ElapsedMilliseconds) ms"

$opened = $false
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    if ($p[0].HasExited) { Fail "the tray process exited during the second launch (code $($p[0].ExitCode))" }
    $p[0].Refresh()
    if ($p[0].MainWindowHandle -ne 0) { $opened = $true; break }
    Start-Sleep -Milliseconds 100
}
$openMs = [int]$launched2.ElapsedMilliseconds
if (-not $opened) { Fail "no main window on the tray process within $TimeoutSec s after the second launch" }
$hwndHex = '0x{0:X}' -f $p[0].MainWindowHandle.ToInt64()
Write-Host "Settings window: hwnd=$hwndHex title='$($p[0].MainWindowTitle)' open-after=${openMs}ms"

$count = @(Get-Process -Name $procName -ErrorAction SilentlyContinue).Count
if ($count -ne 1) { Fail "expected one $procName process after the second launch, found $count" }

$openLine = Wait-LogLine -Pattern ([regex]::Escape($openToken)) -SkipLines $skip -Timeout 5 -Process $p[0]
if (-not $openLine) { Fail "no '$openToken' log line" }
Write-Host "Open line: $openLine"

# ---- 3. close the window; the tray process must survive ---------------------------------------------------

$skip = @(Read-Log).Count
if (-not $p[0].CloseMainWindow()) { Fail 'CloseMainWindow returned false (no main window to close)' }
$closed = $false
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    if ($p[0].HasExited) { Fail "the tray process exited when the Settings window closed (code $($p[0].ExitCode))" }
    $p[0].Refresh()
    if ($p[0].MainWindowHandle -eq 0) { $closed = $true; break }
    Start-Sleep -Milliseconds 100
}
if (-not $closed) { Fail 'the Settings window is still open 10 s after CloseMainWindow' }

$closeLine = Wait-LogLine -Pattern ([regex]::Escape($closeToken)) -SkipLines $skip -Timeout 5 -Process $p[0]
if (-not $closeLine) { Fail "no '$closeToken' log line" }
Write-Host "Close line: $closeLine"

$p[0].Refresh()
if ($p[0].HasExited) { Fail 'the tray process is gone after closing Settings' }
if ($p[0].MainWindowHandle -ne 0) { Fail 'a main window reappeared after closing Settings' }

# ---- 4. done ---------------------------------------------------------------------------------------------

if ($script:launched) { Stop-App }
Write-Host "SETTINGS PROBE OK open-after=${openMs}ms"
exit 0
