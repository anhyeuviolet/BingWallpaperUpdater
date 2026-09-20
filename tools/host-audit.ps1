# Non-admin host audit for the walking skeleton (Phase 1, Plan 04; RESEARCH A6, SRC-10).
#
# Flushes the DNS client cache, snapshots it, runs publish\BingWallpaperUpdater.exe once (until "apply ok"
# or "pipeline failed"), stops it, snapshots the cache again, and prints:
#   (a) "logged hosts:"   every host named by an "http <host> <status> <bytes>" line in the run's log.txt,
#                         each marked allowed / other  -> the automated gate: exit 1 on any "other"
#   (b) "dns entries:"    DNS client-cache entries that appeared during the run window, marked the same way.
#                         Informational only: other processes resolve names in the same window (A6), and
#                         an admin-only pktmon / Wireshark capture is scheduled for Phase 4's README claim.
#
# Touches only the app's own data folder (log.txt) and the app's own process. Leaves the app stopped and
# today's wallpaper applied.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/host-audit.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath  = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$data     = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath  = Join-Path $data 'log.txt'
$procName = 'BingWallpaperUpdater'
$allowedPattern = '^(raw\.githubusercontent\.com|www\.bing\.com|cn\.bing\.com)$'
$timeoutSec = 120

function Stop-App {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { }
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 200
    }
}

function Fail([string]$reason) {
    Write-Host "HOST AUDIT FAIL: $reason"
    if (Test-Path $logPath) {
        Write-Host '--- last 40 log lines ---'
        Get-Content $logPath -Tail 40 | ForEach-Object { Write-Host $_ }
    }
    Stop-App
    exit 1
}

function Read-Log {
    if (-not (Test-Path $logPath)) { return @() }
    $lines = Get-Content $logPath -ErrorAction SilentlyContinue
    if ($null -eq $lines) { return @() }
    return @($lines)
}

function Get-DnsSnapshot {
    # Get-DnsClientCache (DnsClient module) needs no elevation; the cache may legitimately be empty.
    try {
        $entries = Get-DnsClientCache -ErrorAction Stop | Select-Object -ExpandProperty Entry
        if ($null -eq $entries) { return @() }
        return @($entries | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
    } catch {
        Write-Host "  (Get-DnsClientCache unavailable: $($_.Exception.Message))"
        return @()
    }
}

function Get-Mark([string]$HostName) {
    if ($HostName -imatch $allowedPattern) { return 'allowed' } else { return 'other' }
}

if (-not (Test-Path $exePath)) { Fail "publish\BingWallpaperUpdater.exe is missing - run dotnet publish first" }
Stop-App
if (Test-Path $logPath) { Remove-Item $logPath -Force }

Write-Host 'flushing the DNS client cache (ipconfig /flushdns)'
$flush = & ipconfig /flushdns 2>&1
Write-Host "  $($flush | Where-Object { $_ -match '\S' } | Select-Object -Last 1)"
$before = @(Get-DnsSnapshot)
Write-Host "  dns entries before the run: $($before.Count)"

Write-Host "starting $exePath"
$proc = Start-Process -FilePath $exePath -PassThru
$deadline = (Get-Date).AddSeconds($timeoutSec)
$outcome = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { Fail "process exited early with code $($proc.ExitCode)" }
    $lines = @(Read-Log)
    if ($lines.Count -eq 0) { continue }
    $outcome = $lines | Where-Object { $_ -match ' INFO apply ok ' -or $_ -match ' pipeline failed ' -or $_ -match ' apply failed ' } | Select-Object -First 1
    if ($outcome) { break }
}
if (-not $outcome) { Fail "neither 'apply ok' nor 'pipeline failed' within $timeoutSec s" }
Start-Sleep -Seconds 2   # let the post-apply state/index writes and any trailing DNS activity settle
Stop-App
Write-Host "  outcome: $outcome"

$after = @(Get-DnsSnapshot)
$log = @(Read-Log)

# (a) automated gate: hosts named by the app's own http log lines.
$loggedHosts = @{}
foreach ($line in $log) {
    if ($line -match '^\S+ INFO http (\S+) \d+ \d+\s*$') { $loggedHosts[$Matches[1].ToLowerInvariant()] = $true }
}
Write-Host 'logged hosts:'
$violations = 0
if ($loggedHosts.Count -eq 0) {
    Write-Host '  (none)'
} else {
    foreach ($h in ($loggedHosts.Keys | Sort-Object)) {
        $mark = Get-Mark $h
        if ($mark -eq 'other') { $violations++ }
        Write-Host "  $h $mark"
    }
}

# (b) informational: DNS client-cache entries that appeared during the window.
$new = @($after | Where-Object { $before -notcontains $_ })
Write-Host 'dns entries:'
if ($new.Count -eq 0) {
    Write-Host '  (none appeared during the run window)'
} else {
    foreach ($e in $new) { Write-Host "  $e $(Get-Mark $e)" }
}
Write-Host 'note: the dns section is informational - other processes resolve names in the same window (RESEARCH A6);'
Write-Host '      an admin-only pktmon / Wireshark capture is scheduled for the Phase 4 README claim.'

if ($loggedHosts.Count -eq 0) { Fail 'the run logged no http lines - nothing to audit' }
if ($violations -gt 0) { Fail "$violations logged host(s) outside the allow-list" }
Write-Host 'HOST AUDIT OK'
exit 0
