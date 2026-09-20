# Cross-process synthetic resume probe for the hidden power window (Phase 2, Plan 03; D-10, ROT-04).
#
# Finds the app's hidden top-level window by its caption ("BingWallpaperUpdater.PowerWindow"), posts
# WM_POWERBROADCAST (0x218) with wParam PBT_APMRESUMEAUTOMATIC (0x12) to it from this process, and waits for the
# app to log "schedule nudge source=resume-automatic" - the proof that the window path re-arms the heartbeat without
# sleeping the machine. Prints "POWER PROBE OK hwnd=0x... nudge-after=<ms>ms" (exit 0) or
# "POWER PROBE FAIL: <reason>" plus the last 20 log lines (exit 1).
#
#   -NoLaunch      use the already-running BingWallpaperUpdater process and leave it running afterwards.
#                  Default: stop any running instance, launch publish\BingWallpaperUpdater.exe, wait up to 60 s for
#                  the "schedule start" log line, and stop the process at the end.
#   -TimeoutSec    how long to wait for the nudge line after posting (default 15).
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/power-probe.ps1

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
$caption   = 'BingWallpaperUpdater.PowerWindow'
$wmPowerBroadcast = 0x218
$pbtApmResumeAutomatic = 0x12
$nudgeToken = 'schedule nudge source=resume-automatic'

$script:launched = $false

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PowerProbeNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
'@

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
    Write-Host "POWER PROBE FAIL: $reason"
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

# ---- 2. window -------------------------------------------------------------------------------------------

$hwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    # PowerShell coerces $null to "" for string parameters; [NullString]::Value marshals a real NULL class name.
    $hwnd = [PowerProbeNative]::FindWindow([NullString]::Value, $caption)
    if ($hwnd -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 250
}
if ($hwnd -eq [IntPtr]::Zero) { Fail "window not found (caption '$caption')" }
$hwndHex = '0x{0:X}' -f $hwnd.ToInt64()
Write-Host "Power window: hwnd=$hwndHex"

$windowLine = @(Read-Log | Where-Object { $_ -match ' INFO power window hwnd=0x' }) | Select-Object -Last 1
if ($windowLine) { Write-Host "Window line: $windowLine" }

# ---- 3. synthetic resume ---------------------------------------------------------------------------------

$skip = @(Read-Log).Count
$posted = [System.Diagnostics.Stopwatch]::StartNew()
$ok = [PowerProbeNative]::PostMessage($hwnd, [uint32]$wmPowerBroadcast, [IntPtr]$pbtApmResumeAutomatic, [IntPtr]::Zero)
if (-not $ok) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Fail "PostMessage(WM_POWERBROADCAST, PBT_APMRESUMEAUTOMATIC) returned false (error $err)"
}
Write-Host "Posted WM_POWERBROADCAST wParam=PBT_APMRESUMEAUTOMATIC to $hwndHex"

$nudgeLine = Wait-LogLine -Pattern ([regex]::Escape($nudgeToken)) -SkipLines $skip -Timeout $TimeoutSec -Process $proc
$elapsedMs = [int]$posted.ElapsedMilliseconds
if (-not $nudgeLine) { Fail "no '$nudgeToken' log line within $TimeoutSec s" }
Write-Host "Nudge line: $nudgeLine"

# ---- 4. done ---------------------------------------------------------------------------------------------

if ($script:launched) { Stop-App }
Write-Host "POWER PROBE OK hwnd=$hwndHex nudge-after=${elapsedMs}ms"
exit 0
