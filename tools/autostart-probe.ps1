# Live autostart probe (Phase 3, Plan 03; INST-02).
#
# Proves that a real launch of the published exe (with Settings.Autostart true, the default) rewrites the per-user
# Run value byte-exactly as '"<absolute exe path>" --startup' and logs "autostart run value written", and that the
# start path never writes Task Manager's StartupApproved value (a "Disabled" set there must survive a manual launch).
# Prints "AUTOSTART PROBE OK value=<command>" (exit 0) or "AUTOSTART PROBE FAIL: <reason>" plus the last 20 log
# lines (exit 1). Only the BingWallpaperUpdater value is ever read or removed under either key; no other product's
# entry (e.g. Microsoft's BingWallpaperApp) is touched.
#
#   -NoLaunch      use the already-running BingWallpaperUpdater process (leave it running) and assert against the
#                  log line and registry values it already produced; no pre-clean.
#                  Default: stop any running instance, delete the app's value under both keys (clean slate), launch
#                  publish\BingWallpaperUpdater.exe, wait for a fresh "autostart run value written" line, assert,
#                  and stop the process at the end.
#   -Cleanup       delete the app's value under both keys and exit 0 without launching anything.
#   -TimeoutSec    how long to wait for the log line after launch (default 30).
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/autostart-probe.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/autostart-probe.ps1 -Cleanup

param(
    [switch]$NoLaunch,
    [switch]$Cleanup,
    [int]$TimeoutSec = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$exePath    = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$data       = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath    = Join-Path $data 'log.txt'
$procName   = 'BingWallpaperUpdater'
$valueName  = 'BingWallpaperUpdater'
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$writtenToken = ' autostart run value written '

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
    Write-Host "AUTOSTART PROBE FAIL: $reason"
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

# Reads ONE named value under a key (null when the key or the value is absent); never enumerates other values.
function Get-AppValue([string]$Key) {
    if (-not (Test-Path $Key)) { return $null }
    $item = Get-ItemProperty -Path $Key -Name $valueName -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return $item.$valueName
}

function Remove-AppValue([string]$Key) {
    if (Test-Path $Key) {
        Remove-ItemProperty -Path $Key -Name $valueName -ErrorAction SilentlyContinue
    }
}

function Format-Bytes($value) {
    if ($null -eq $value) { return '<absent>' }
    if ($value -is [byte[]]) { return (($value | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') }
    return [string]$value
}

# ---- cleanup mode ----------------------------------------------------------------------------------------

if ($Cleanup) {
    Remove-AppValue $runKey
    Remove-AppValue $approvedKey
    $leftRun = Get-AppValue $runKey
    $leftApproved = Get-AppValue $approvedKey
    if ($null -ne $leftRun -or $null -ne $leftApproved) { Fail "cleanup left a value behind (Run=$(Format-Bytes $leftRun) StartupApproved=$(Format-Bytes $leftApproved))" }
    Write-Host "AUTOSTART PROBE CLEANUP OK: no '$valueName' value under Run or StartupApproved\Run"
    exit 0
}

# ---- 1. process ------------------------------------------------------------------------------------------

$proc = $null
$skip = 0
if ($NoLaunch) {
    $running = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { Fail "-NoLaunch given but no $procName process is running" }
    $proc = $running | Select-Object -First 1
    Write-Host "Using running process pid=$($proc.Id)"
    try {
        $exePath = $proc.MainModule.FileName
    } catch {
        Write-Host "MainModule not readable; assuming publish exe at $exePath"
    }
} else {
    if (-not (Test-Path $exePath)) { Fail "publish\BingWallpaperUpdater.exe is missing - run dotnet publish first" }
    Stop-App
    Remove-AppValue $runKey
    Remove-AppValue $approvedKey
    if ($null -ne (Get-AppValue $runKey)) { Fail 'pre-clean could not remove the Run value' }
    if ($null -ne (Get-AppValue $approvedKey)) { Fail 'pre-clean could not remove the StartupApproved value' }
    $skip = @(Read-Log).Count
    Write-Host "Starting $exePath"
    $proc = Start-Process -FilePath $exePath -PassThru
    $script:launched = $true
}

$exePath = [System.IO.Path]::GetFullPath($exePath)
$expected = '"' + $exePath + '" --startup'

# ---- 2. the log line -------------------------------------------------------------------------------------

$line = Wait-LogLine -Pattern ([regex]::Escape($writtenToken)) -SkipLines $skip -Timeout $TimeoutSec -Process $proc
if (-not $line) { Fail "no '$($writtenToken.Trim())' log line within $TimeoutSec s (is Settings.Autostart false in settings.json?)" }
Write-Host "Written line: $line"

if (-not $NoLaunch) {
    $startLine = Wait-LogLine -Pattern ' INFO schedule start ' -SkipLines $skip -Timeout 60 -Process $proc
    if (-not $startLine) { Fail "no 'schedule start' log line within 60 s" }
}

# ---- 3. registry assertions ------------------------------------------------------------------------------

$actual = Get-AppValue $runKey
if ($null -eq $actual) { Fail "no '$valueName' value under $runKey" }
if (-not ($actual -is [string])) { Fail "the Run value is not REG_SZ (got $($actual.GetType().Name))" }
if (-not ($actual -ceq $expected)) { Fail "Run value mismatch`n  expected: $expected`n  actual:   $actual" }
Write-Host "Run value OK: $actual"

$approved = Get-AppValue $approvedKey
if ($null -ne $approved) { Fail "the start path wrote StartupApproved\Run\$valueName = $(Format-Bytes $approved); it must only be written by the Settings checkbox" }
Write-Host "StartupApproved\Run has no '$valueName' value (start path never writes it) OK"

# ---- 4. done ---------------------------------------------------------------------------------------------

if ($script:launched) { Stop-App }
Write-Host "AUTOSTART PROBE OK value=$actual"
exit 0
