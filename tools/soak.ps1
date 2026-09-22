# Compressed memory soak tool (Phase 4, Plan 04; NFR-01, NFR-02, ROADMAP criterion 4).
#
# Leak detection is about iteration count, not elapsed time: the tool drives the published app through the two things
# it does in real life - rotation ticks and Settings window open/close cycles - a few hundred times in a quarter of an
# hour and samples the process after every step. Flow:
#
#   1. launch publish\BingWallpaperUpdater.exe (or -NoLaunch: attach to the running instance), wait for the first tick;
#   2. baseline sample with the window closed;
#   3. -SettingsCycles (20) open/close cycles: Local\BingWallpaperUpdater.Show event opens the window, it is minimized
#      at once without activation (ShowWindow SW_SHOWMINNOACTIVE, so the focus goes straight back to whatever the user
#      was in), Process.CloseMainWindow closes it; the "settings window action=open/close" log lines are awaited; a
#      sample after every fifth cycle. The burst takes ~40 s; -NoSettingsCycles skips it entirely;
#   4. -Ticks (200) rotation ticks: the Settings window is opened once, the HWND of its "Next wallpaper" button
#      (AutomationId NextButton) is read through UI Automation while the window is still visible, then the window is
#      minimized without activation and every tick is a WM_COMMAND/BN_CLICKED posted to the form with that button HWND
#      - WinForms reflects it to the Button's Click handler with no mouse input, no focus change and no visible window
#      (a minimized window has no UI Automation subtree and restoring one activates it, so UIA cannot click here);
#      every click is one real tick
#      (catalog check, Bing API, COM apply, backfill) and the tool waits for the "tick done" log line before the next
#      one; samples every -SampleIntervalSeconds carry the tick index they were taken after; then the window is closed
#      and a post-ticks sample is taken;
#   5. final sample after a 20 s settle with the window closed, verdict, <csv>.result.txt.
#
# The whole default run takes about 7 minutes (an ETA line is printed at start). The Settings window still appears for
# a fraction of a second per cycle before it is minimized and the wallpaper changes on every tick, so run it when you
# are away from the machine.
#
# Every sample is one CSV row with the numbers Task Manager and Process Explorer show:
#   PrivateWS_MB  private working set = Task Manager's "Memory" column (Win32_PerfFormattedData_PerfProc_Process.WorkingSetPrivate)
#   WS_MB         total working set (includes ~50 MB of shared, file-backed .NET runtime pages)
#   Commit_MB     private bytes (Process.PrivateMemorySize64)
#   Handles, Threads, GDI, USER   HandleCount, Threads.Count, GetGuiResources(0 / 1) - the WinForms leak signals
#   Window        1 while the Settings window is open; TickIndex, CyclesDone, CacheCount (entries in cache\index.json)
#
# CSV layout: "# key=value" header comments (os, build, appVersion, commit, startedUtc, interval, mode, resolution,
# monitorMode, cacheAtStart, ticks, settingsCycles, sampleIntervalSeconds, tickGapSec, cycleGapSec - never the computer
# name, the user name or a profile path), then the column header, then rows with
# Phase = baseline | settings | post-settings | ticks | post-ticks | final.
#
# Verdict (printed and written to <csv>.result.txt), computed from the rows, never from wall-clock time:
#   SOAK OK privateWS=<max>MB closed=<baseline>-><final>MB ws=<max>MB commit=<max>MB slope=<KB/tick> handles=<b>-><f> gdi=<b>-><f> user=<b>-><f> ticks=<n> cycles=<n> duration=<min>min samples=<n>
#   SOAK WARN <same fields> reason=<slope|handles|gdi|user> growth
#        slope  = least-squares slope of PrivateWS_MB against TickIndex over the steady-state "ticks" rows (window open
#                 the whole time, cache already full, so the slope isolates per-tick growth) above -SlopeWarnKBPerTick
#                 (default 8 KB/tick, i.e. more than ~1.6 MB over 200 ticks or ~140 MB over a year of 48 ticks a day);
#        handles / gdi / user = final (window closed) minus the first sample after the window was opened once (the
#                 one-time WinForms caches are not growth) above -HandleGrowthWarn (100), -GdiGrowthWarn (50),
#                 -UserGrowthWarn (50): a WinForms object leak from the open/close loop or the tick loop.
#        closed=<baseline>-><final> reports the private WS with the window closed before and after everything.
#   SOAK FAIL: <reason>   private WS reached -BudgetMB (100) in any sample, or the run could not complete (exit 1)
#
#   -NoLaunch          attach to the running BingWallpaperUpdater process (leave it running afterwards). Default: stop any
#                      running instance, launch publish\BingWallpaperUpdater.exe, wait for the first tick, stop it at the end.
#   -FreshData         (launch mode only) delete %LocalAppData%\BingWallpaperUpdater first so the run starts from an
#                      install-like state: empty cache, first tick downloads, backfill fills the cache during the ticks.
#   -Ticks 200         rotation ticks driven through the Next button; -TickGapSec 1 second between ticks.
#   -SettingsCycles 20 Settings window open/close cycles; -CycleGapSec 1 second between cycles; -NoSettingsCycles = 0.
#   -SampleIntervalSeconds 15   sampling cadence during the ticks phase (a sample is taken right after a tick completes).
#   -Csv <path>        output file (default docs\soak\soak-<yyyyMMdd-HHmm>.csv under the repo).
#   -BudgetMB 100, -SlopeWarnKBPerTick 8, -HandleGrowthWarn 100, -GdiGrowthWarn 50, -UserGrowthWarn 50   thresholds.
#   -Summarize <csv>   read an existing CSV, recompute the verdict and print the markdown row the README embeds:
#                      | <os> | <max private WS> MB | <final private WS, window closed> MB | <max WS> MB | <max commit> MB | <slope> KB/tick | compressed soak, <ticks> ticks + <cycles> Settings cycles, ~<min> min | <csv file name> |
#
# The tool never changes the app or the runtime (no working-set trimming calls, no GC or runtime knobs), never touches a
# process other than BingWallpaperUpdater, and deletes nothing outside the app data folder (T-04-20 .. T-04-22).
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/soak.ps1 -FreshData -Ticks 10 -SettingsCycles 3 -SampleIntervalSeconds 5 -Csv "$env:TEMP\bwu-soak-short.csv"
#
# The run behind the README figure (Windows 11, this repo's publish\ build; ~7 min, run it while away from the machine):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/soak.ps1 -FreshData -Csv docs\soak\win11-soak.csv
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/soak.ps1 -Summarize docs\soak\win11-soak.csv
# On a bare Windows 10 1809 VM with the installed build (no repo needed, the script attaches to the running app):
#   powershell -NoProfile -ExecutionPolicy Bypass -File soak.ps1 -NoLaunch -Csv win10-1809-soak.csv

param(
    [switch]$NoLaunch,
    [switch]$FreshData,
    [switch]$NoSettingsCycles,
    [int]$Ticks = 200,
    [int]$SettingsCycles = 20,
    [int]$TickGapSec = 1,
    [int]$CycleGapSec = 1,
    [int]$SampleIntervalSeconds = 15,
    [string]$Csv = '',
    [int]$BudgetMB = 100,
    [double]$SlopeWarnKBPerTick = 8,
    [int]$HandleGrowthWarn = 100,
    [int]$GdiGrowthWarn = 50,
    [int]$UserGrowthWarn = 50,
    [string]$Summarize = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($NoSettingsCycles) { $SettingsCycles = 0 }

$repoRoot  = Split-Path -Parent $PSScriptRoot
$exePath   = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$data      = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath   = Join-Path $data 'log.txt'
$indexPath = Join-Path $data 'cache\index.json'
$settingsPath = Join-Path $data 'settings.json'
$procName  = 'BingWallpaperUpdater'
$showEventName = 'Local\BingWallpaperUpdater.Show'
$openToken  = 'settings window action=open'
$closeToken = 'settings window action=close'
$tickPattern = ' tick (done|failed) '
$columns = 'Utc,ElapsedSec,Phase,TickIndex,CyclesDone,PrivateWS_MB,WS_MB,Commit_MB,Handles,Threads,GDI,USER,Window,CacheCount'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$script:launched = $false
$script:csvPath = $null
$script:rows = @()
$script:baselineUtc = $null

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
    $lines = Get-Content $logPath -ErrorAction SilentlyContinue
    if ($null -eq $lines) { return @() }
    return @($lines)
}

function Write-Result([string]$line) {
    if ($script:csvPath) {
        [System.IO.File]::WriteAllText("$($script:csvPath).result.txt", "$line`r`n", [System.Text.Encoding]::ASCII)
    }
}

function Fail([string]$reason) {
    $line = "SOAK FAIL: $reason"
    Write-Host $line
    Write-Result $line
    $lines = @(Read-Log)
    if ($lines.Count -gt 0) {
        Write-Host '--- last 20 log lines ---'
        $lines | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
    }
    if ($script:launched) { Stop-App }
    exit 1
}

# Waits for a log line matching $Pattern among the lines after $SkipLines. The app rolls log.txt to log.1.txt at 256 KB;
# when the file shrinks below the skip count the roll happened and the fresh file is searched from its first line.
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
        if ($lines.Count -lt $SkipLines) { $SkipLines = 0 }
        if ($lines.Count -gt $SkipLines) {
            $fresh = @($lines | Select-Object -Skip $SkipLines)
            $hit = @($fresh | Where-Object { $_ -match $Pattern }) | Select-Object -First 1
            if ($hit) { return $hit }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-LastLogLine([string]$Pattern) {
    $hit = @(@(Read-Log) | Where-Object { $_ -match $Pattern }) | Select-Object -Last 1
    if ($hit) { return $hit }
    return $null
}

function Format-Num([double]$value, [string]$format = '0.0') {
    return $value.ToString($format, $inv)
}

# Least-squares slope of PrivateWS_MB against TickIndex over the steady-state "ticks" rows, in KB per tick. Steady state
# = the rows taken after the cache stopped growing (CacheCount equal to the last ticks row's count), so the UHD downloads
# of the backfill phase do not masquerade as growth; falls back to all ticks rows when fewer than 3 remain (0 below 3).
function Get-SlopeKbPerTick([object[]]$rows) {
    $all = @($rows | Where-Object { $_.Phase -eq 'ticks' })
    if ($all.Count -lt 3) { return 0.0 }
    $fullCache = $all[$all.Count - 1].CacheCount
    $pts = @($all | Where-Object { $_.CacheCount -eq $fullCache })
    if ($pts.Count -lt 3) { $pts = $all }
    $n = [double]$pts.Count
    $sx = 0.0; $sy = 0.0; $sxx = 0.0; $sxy = 0.0
    foreach ($r in $pts) {
        $x = [double]$r.TickIndex; $y = [double]$r.PrivateWS_MB
        $sx += $x; $sy += $y; $sxx += $x * $x; $sxy += $x * $y
    }
    $den = $n * $sxx - $sx * $sx
    if ($den -eq 0) { return 0.0 }
    return (($n * $sxy - $sx * $sy) / $den) * 1024.0
}

# Verdict over parsed rows. Returns @{ Line; Status = 'OK'|'WARN'|'FAIL'; MaxPriv; FinalPriv; MaxWs; MaxCommit; Slope; Ticks; Cycles; Minutes }.
function Get-Verdict([object[]]$rows) {
    if ($rows.Count -eq 0) { return @{ Line = 'SOAK FAIL: no sample rows'; Status = 'FAIL' } }
    $maxPriv   = ($rows | Measure-Object -Property PrivateWS_MB -Maximum).Maximum
    $maxWs     = ($rows | Measure-Object -Property WS_MB -Maximum).Maximum
    $maxCommit = ($rows | Measure-Object -Property Commit_MB -Maximum).Maximum
    $first = $rows[0]
    $last  = $rows[$rows.Count - 1]
    # Object-count reference: the first sample taken after the window has been opened at least once (the first
    # "settings" row), so the one-time WinForms caches (fonts, brushes, the form's own handles) are not counted as
    # growth; falls back to the baseline when no cycle ran.
    $ref = @($rows | Where-Object { $_.CyclesDone -ge 1 -or $_.TickIndex -ge 1 }) | Select-Object -First 1
    if ($null -eq $ref) { $ref = $first }
    $ticks  = [int](($rows | Measure-Object -Property TickIndex -Maximum).Maximum)
    $cycles = [int](($rows | Measure-Object -Property CyclesDone -Maximum).Maximum)
    $minutes = [math]::Round($last.ElapsedSec / 60, 1)
    $slope = Get-SlopeKbPerTick $rows
    $fields = 'privateWS={0}MB closed={1}->{2}MB ws={3}MB commit={4}MB slope={5}KB/tick handles={6}->{7} gdi={8}->{9} user={10}->{11} ticks={12} cycles={13} duration={14}min samples={15}' -f `
        (Format-Num $maxPriv), (Format-Num $first.PrivateWS_MB), (Format-Num $last.PrivateWS_MB), (Format-Num $maxWs), (Format-Num $maxCommit), `
        (Format-Num $slope '0.00'), $ref.Handles, $last.Handles, $ref.GDI, $last.GDI, $ref.USER, $last.USER, $ticks, $cycles, (Format-Num $minutes), $rows.Count
    $result = @{ MaxPriv = $maxPriv; FinalPriv = $last.PrivateWS_MB; MaxWs = $maxWs; MaxCommit = $maxCommit; Slope = $slope; Ticks = $ticks; Cycles = $cycles; Minutes = $minutes }
    if ($maxPriv -ge $BudgetMB) {
        $result.Line = 'SOAK FAIL: private WS {0} MB >= budget {1} MB ({2})' -f (Format-Num $maxPriv), $BudgetMB, $fields
        $result.Status = 'FAIL'
        return $result
    }
    $reason = $null
    if ($slope -gt $SlopeWarnKBPerTick) { $reason = 'slope' }
    elseif (($last.Handles - $ref.Handles) -gt $HandleGrowthWarn) { $reason = 'handles' }
    elseif (($last.GDI - $ref.GDI) -gt $GdiGrowthWarn) { $reason = 'gdi' }
    elseif (($last.USER - $ref.USER) -gt $UserGrowthWarn) { $reason = 'user' }
    if ($reason) { $result.Line = "SOAK WARN $fields reason=$reason growth"; $result.Status = 'WARN'; return $result }
    $result.Line = "SOAK OK $fields"; $result.Status = 'OK'
    return $result
}

# ---- -Summarize: recompute the verdict from a CSV and print the README row ---------------------------------

if ($Summarize) {
    if (-not (Test-Path $Summarize)) { Write-Host "SOAK FAIL: '$Summarize' not found"; exit 1 }
    $meta = @{}
    $parsed = @()
    $headerSeen = $false
    foreach ($line in @(Get-Content $Summarize)) {
        if ($line -match '^#\s*([A-Za-z]+)=(.*)$') { $meta[$matches[1]] = $matches[2].Trim(); continue }
        if ($line -eq $columns) { $headerSeen = $true; continue }
        if (-not $headerSeen -or [string]::IsNullOrWhiteSpace($line)) { continue }
        $f = $line.Split(',')
        if ($f.Count -lt 14) { continue }
        $parsed += [pscustomobject]@{
            Utc = $f[0]; ElapsedSec = [int]$f[1]; Phase = $f[2]; TickIndex = [int]$f[3]; CyclesDone = [int]$f[4]
            PrivateWS_MB = [double]::Parse($f[5], $inv); WS_MB = [double]::Parse($f[6], $inv); Commit_MB = [double]::Parse($f[7], $inv)
            Handles = [int]$f[8]; Threads = [int]$f[9]; GDI = [int]$f[10]; USER = [int]$f[11]; Window = [int]$f[12]; CacheCount = [int]$f[13]
        }
    }
    if ($parsed.Count -eq 0) { Write-Host "SOAK FAIL: '$Summarize' has no data rows"; exit 1 }
    $v = Get-Verdict $parsed
    Write-Host $v.Line
    $os = if ($meta.ContainsKey('os')) { $meta['os'] } else { '-' }
    Write-Host ('| {0} | {1} MB | {2} MB | {3} MB | {4} MB | {5} KB/tick | compressed soak, {6} ticks + {7} Settings cycles, ~{8} min | {9} |' -f `
        $os, (Format-Num $v.MaxPriv), (Format-Num $v.FinalPriv), (Format-Num $v.MaxWs), (Format-Num $v.MaxCommit), (Format-Num $v.Slope '0.00'), `
        $v.Ticks, $v.Cycles, [int][math]::Ceiling($v.Minutes), (Split-Path -Leaf $Summarize))
    exit 0
}

# ---- run: output file ------------------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($Csv)) {
    $Csv = Join-Path $repoRoot ('docs\soak\soak-{0}.csv' -f (Get-Date).ToString('yyyyMMdd-HHmm'))
}
if (-not [System.IO.Path]::IsPathRooted($Csv)) { $Csv = Join-Path (Get-Location).Path $Csv }
$Csv = [System.IO.Path]::GetFullPath($Csv)
$csvDir = Split-Path -Parent $Csv
if (-not (Test-Path $csvDir)) { New-Item -ItemType Directory -Path $csvDir -Force | Out-Null }
if (Test-Path $Csv) { Remove-Item $Csv -Force }
if (Test-Path "$Csv.result.txt") { Remove-Item "$Csv.result.txt" -Force }
$script:csvPath = $Csv

function Add-CsvLine([string]$line) {
    [System.IO.File]::AppendAllText($script:csvPath, "$line`r`n", $utf8NoBom)
}

# ---- 1. process ------------------------------------------------------------------------------------------

$proc = $null
if ($NoLaunch) {
    $running = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { Fail "-NoLaunch given but no $procName process is running" }
    $proc = $running | Select-Object -First 1
    Write-Host "Using running process pid=$($proc.Id)"
    if (-not (Find-LastLogLine $tickPattern)) {
        $t = Wait-LogLine -Pattern $tickPattern -SkipLines 0 -Timeout 180 -Process $proc
        if (-not $t) { Fail "no 'tick done' log line within 180 s" }
    }
} else {
    if (-not (Test-Path $exePath)) { Fail "publish\BingWallpaperUpdater.exe is missing - run dotnet publish first" }
    Stop-App
    if ($FreshData) {
        # Same guard as the smoke S0: only the folder ending in \BingWallpaperUpdater under LocalAppData is ever deleted.
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or -not $data.EndsWith('\BingWallpaperUpdater')) { Fail "refusing to delete '$data' - not the app data folder" }
        if (Test-Path $data) { Remove-Item -Path $data -Recurse -Force }
        Write-Host "Fresh data: removed $data"
    }
    $before = @(Read-Log).Count
    Write-Host "Starting $exePath"
    $proc = Start-Process -FilePath $exePath -PassThru
    $script:launched = $true
    $startLine = Wait-LogLine -Pattern ' INFO schedule start ' -SkipLines $before -Timeout 60 -Process $proc
    if (-not $startLine) { Fail "no 'schedule start' log line within 60 s" }
    Write-Host "Start line: $startLine"
    $tickLine = Wait-LogLine -Pattern $tickPattern -SkipLines $before -Timeout 180 -Process $proc
    if (-not $tickLine) { Fail "no 'tick done' log line within 180 s of launch" }
    Write-Host "First tick: $tickLine"
}

$p = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
if ($p.Count -ne 1) { Fail "expected exactly one $procName process, found $($p.Count)" }
$proc = $p[0]
$proc.Refresh()
if ($proc.MainWindowHandle -ne 0) { Fail "the tray process already has a main window (handle $($proc.MainWindowHandle)); close Settings first" }

# ---- 2. metadata (no machine name, user name or profile path) ----------------------------------------------

$osInfo = Get-CimInstance Win32_OperatingSystem
$appVersion = '-'
$startupLine = Find-LastLogLine ' INFO startup version='
if ($startupLine -and $startupLine -match 'startup version=(\S+)') { $appVersion = $matches[1] }
$commit = '-'
if ((Test-Path (Join-Path $repoRoot '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
    try { $commit = (& git -C $repoRoot rev-parse --short HEAD 2>$null).Trim() } catch { $commit = '-' }
    if ([string]::IsNullOrWhiteSpace($commit)) { $commit = '-' }
}
$interval = '-'; $mode = '-'; $resolution = '-'; $monitorMode = '-'
$scheduleLine = Find-LastLogLine ' INFO schedule start '
if ($scheduleLine) {
    if ($scheduleLine -match ' interval=(\S+)') { $interval = $matches[1] }
    if ($scheduleLine -match ' mode=(\S+)') { $mode = $matches[1] }
    if ($scheduleLine -match ' resolution=(\S+)') { $resolution = $matches[1] }
    if ($scheduleLine -match ' monitors=(\S+)') { $monitorMode = $matches[1] }
} elseif (Test-Path $settingsPath) {
    try {
        $s = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $interval = "$($s.intervalMinutes)"; $mode = "$($s.mode)"; $resolution = "$($s.resolution)"; $monitorMode = "$($s.monitorMode)"
    } catch { }
}

function Get-CacheCount {
    if (-not (Test-Path $indexPath)) { return 0 }
    try {
        $idx = Get-Content $indexPath -Raw | ConvertFrom-Json
        if ($null -eq $idx.images) { return 0 }
        return @($idx.images).Count
    } catch { return 0 }
}

$startedUtc = [DateTime]::UtcNow
Add-CsvLine "# os=$($osInfo.Caption) $($osInfo.Version)"
Add-CsvLine "# build=$($osInfo.BuildNumber)"
Add-CsvLine "# appVersion=$appVersion"
Add-CsvLine "# commit=$commit"
Add-CsvLine "# startedUtc=$($startedUtc.ToString('yyyy-MM-ddTHH:mm:ssZ', $inv))"
Add-CsvLine "# interval=$interval"
Add-CsvLine "# mode=$mode"
Add-CsvLine "# resolution=$resolution"
Add-CsvLine "# monitorMode=$monitorMode"
Add-CsvLine "# cacheAtStart=$(Get-CacheCount)"
Add-CsvLine "# ticks=$Ticks"
Add-CsvLine "# settingsCycles=$SettingsCycles"
Add-CsvLine "# sampleIntervalSeconds=$SampleIntervalSeconds"
Add-CsvLine "# tickGapSec=$TickGapSec"
Add-CsvLine "# cycleGapSec=$CycleGapSec"
Add-CsvLine $columns
Write-Host "CSV: $Csv (os=$($osInfo.Caption) $($osInfo.Version) app=$appVersion commit=$commit)"

# ---- 3. sampling -----------------------------------------------------------------------------------------

Add-Type -Namespace W -Name G -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);'
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$SW_SHOWMINNOACTIVE = 7
$WM_COMMAND = 0x0111   # wParam LOWORD = control id (0), HIWORD = BN_CLICKED (0); lParam = the button's HWND
$script:nextHwnd = [IntPtr]::Zero

# One-line ETA so the maintainer knows how long to stay away from the machine: measured tick ~0.5 s (up to ~1.5 s
# while the backfill downloads), cycle ~1.5 s, plus the 5 s post-ticks and 20 s final settles.
$etaSec = [int]($SettingsCycles * (1.5 + $CycleGapSec) + $Ticks * (0.6 + $TickGapSec) + 8 * 1.0 + 25)
Write-Host ('ETA: about {0} min ({1} Settings cycles + {2} ticks at {3} s gap); the Settings window flashes briefly per cycle and is minimized without focus while the ticks run' -f [math]::Ceiling($etaSec / 60), $SettingsCycles, $Ticks, $TickGapSec)

function Get-Sample([System.Diagnostics.Process]$p, [string]$phase, [int]$tickIndex, [int]$cycles) {
    if ($p.HasExited) { Fail "the app exited (code $($p.ExitCode)) during phase $phase" }
    $p.Refresh()
    $perf = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)" -ErrorAction SilentlyContinue)
    if ($perf.Count -eq 0) {
        Start-Sleep -Seconds 3
        $perf = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)" -ErrorAction SilentlyContinue)
        if ($perf.Count -eq 0) { Fail "Win32_PerfFormattedData_PerfProc_Process returned no row for pid $($p.Id) twice in a row" }
    }
    $now = [DateTime]::UtcNow
    if ($null -eq $script:baselineUtc) { $script:baselineUtc = $now }
    $row = [pscustomobject]@{
        Utc          = $now.ToString('yyyy-MM-ddTHH:mm:ssZ', $inv)
        ElapsedSec   = [int][math]::Round(($now - $script:baselineUtc).TotalSeconds)
        Phase        = $phase
        TickIndex    = $tickIndex
        CyclesDone   = $cycles
        PrivateWS_MB = [math]::Round($perf[0].WorkingSetPrivate / 1MB, 1)
        WS_MB        = [math]::Round($p.WorkingSet64 / 1MB, 1)
        Commit_MB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Handles      = [int]$p.HandleCount
        Threads      = [int]$p.Threads.Count
        GDI          = [int][W.G]::GetGuiResources($p.Handle, 0)
        USER         = [int][W.G]::GetGuiResources($p.Handle, 1)
        Window       = [int]($p.MainWindowHandle -ne 0)
        CacheCount   = Get-CacheCount
    }
    $script:rows += $row
    Add-CsvLine ('{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13}' -f $row.Utc, $row.ElapsedSec, $row.Phase, $row.TickIndex, $row.CyclesDone, `
        (Format-Num $row.PrivateWS_MB), (Format-Num $row.WS_MB), (Format-Num $row.Commit_MB), $row.Handles, $row.Threads, $row.GDI, $row.USER, $row.Window, $row.CacheCount)
    Write-Host ('{0} {1,5}s {2,-13} tick={3,3} cycles={4,2} priv={5}MB ws={6}MB commit={7}MB handles={8} threads={9} gdi={10} user={11} win={12} cache={13}' -f `
        $row.Utc, $row.ElapsedSec, $row.Phase, $row.TickIndex, $row.CyclesDone, (Format-Num $row.PrivateWS_MB), (Format-Num $row.WS_MB), (Format-Num $row.Commit_MB), `
        $row.Handles, $row.Threads, $row.GDI, $row.USER, $row.Window, $row.CacheCount)
    return $row
}

function Wait-Window([System.Diagnostics.Process]$p, [bool]$open, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($p.HasExited) { Fail "the app exited (code $($p.ExitCode)) while waiting for the window to $(if ($open) { 'open' } else { 'close' })" }
        $p.Refresh()
        $has = $p.MainWindowHandle -ne 0
        if ($has -eq $open) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Open-Settings([System.Diagnostics.Process]$p, [string]$what, [bool]$findNext = $false) {
    $skip = @(Read-Log).Count
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting($showEventName)
        $evt.Set() | Out-Null
        $evt.Dispose()
    } catch { Fail "cannot signal $showEventName ($what): $($_.Exception.Message)" }
    if (-not (Wait-Window $p $true 15)) { Fail "no main window within 15 s after the Show signal ($what)" }
    $p.Refresh()
    $hwnd = $p.MainWindowHandle
    if ($findNext) {
        # The window is visible (Form.Show does not take the foreground from another process) but a minimized window
        # exposes no UI Automation subtree, so the Next button's own HWND is captured now, before minimizing.
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NextButton')
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and $script:nextHwnd -eq [IntPtr]::Zero) {
            $button = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if ($null -ne $button -and $button.Current.NativeWindowHandle -ne 0) { $script:nextHwnd = [IntPtr]$button.Current.NativeWindowHandle }
            else { Start-Sleep -Milliseconds 100 }
        }
        if ($script:nextHwnd -eq [IntPtr]::Zero) { Fail "NextButton not found on the Settings window (UI Automation, $what)" }
    }
    # Give the focus back at once: minimize without activation, so whatever the user was in keeps the input. The form
    # stays alive and minimized (MainWindowHandle stays non-zero) and its child HWNDs keep working.
    [W.U]::ShowWindow($hwnd, $SW_SHOWMINNOACTIVE) | Out-Null
    Start-Sleep -Milliseconds 100
    if ([W.U]::GetForegroundWindow() -eq $hwnd) { Fail "the Settings window holds the focus after being minimized ($what)" }
    $openLine = Wait-LogLine -Pattern ([regex]::Escape($openToken)) -SkipLines $skip -Timeout 15 -Process $p
    if (-not $openLine) { Fail "no '$openToken' log line ($what)" }
}

function Close-Settings([System.Diagnostics.Process]$p, [string]$what) {
    $skip = @(Read-Log).Count
    if (-not $p.CloseMainWindow()) { Fail "CloseMainWindow returned false ($what)" }
    if (-not (Wait-Window $p $false 15)) { Fail "the Settings window is still open 15 s after CloseMainWindow ($what)" }
    $closeLine = Wait-LogLine -Pattern ([regex]::Escape($closeToken)) -SkipLines $skip -Timeout 15 -Process $p
    if (-not $closeLine) { Fail "no '$closeToken' log line ($what)" }
}

Get-Sample $proc 'baseline' 0 0 | Out-Null

# ---- 4. Settings window open/close cycles ----------------------------------------------------------------

for ($i = 1; $i -le $SettingsCycles; $i++) {
    Open-Settings $proc "cycle $i"
    Start-Sleep -Milliseconds 500
    Close-Settings $proc "cycle $i"
    Write-Host "settings cycle $i/$SettingsCycles done"
    if ($i % 5 -eq 0 -and $i -lt $SettingsCycles) { Get-Sample $proc 'settings' 0 $i | Out-Null }
    if ($i -lt $SettingsCycles -and $CycleGapSec -gt 0) { Start-Sleep -Seconds $CycleGapSec }
}
Get-Sample $proc 'post-settings' 0 $SettingsCycles | Out-Null

# ---- 5. rotation ticks through the Next button (UI Automation) --------------------------------------------

if ($Ticks -gt 0) {
    Open-Settings $proc 'ticks' $true
    $formHwnd = $proc.MainWindowHandle
    Write-Host ('Next button hwnd=0x{0:X} (window minimized, clicks are posted as WM_COMMAND/BN_CLICKED to the form)' -f $script:nextHwnd.ToInt64())
    $lastSample = [DateTime]::UtcNow
    for ($t = 1; $t -le $Ticks; $t++) {
        # The app disables the button while a tick runs (D-05); IsWindowEnabled mirrors Control.Enabled.
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and -not [W.U]::IsWindowEnabled($script:nextHwnd)) { Start-Sleep -Milliseconds 50 }
        if (-not [W.U]::IsWindowEnabled($script:nextHwnd)) { Fail "the Next button stayed disabled for 30 s before tick $t" }
        $skip = @(Read-Log).Count
        # WM_COMMAND with the button HWND in lParam is reflected by WinForms to that Button, which raises Click - the
        # same path a real BN_CLICKED takes, with no mouse input, no focus change and no visible window.
        if (-not [W.U]::PostMessage($formHwnd, $WM_COMMAND, [IntPtr]::Zero, $script:nextHwnd)) { Fail "PostMessage WM_COMMAND failed (tick $t)" }
        $tickLine = Wait-LogLine -Pattern $tickPattern -SkipLines $skip -Timeout 90 -Process $proc
        if (-not $tickLine) { Fail "no 'tick done' log line within 90 s after Next (tick $t)" }
        if ($t -le 3 -and [W.U]::GetForegroundWindow() -eq $formHwnd) { Fail "the Settings window took the focus after the Next click (tick $t)" }
        if ($t % 10 -eq 0 -or $t -eq $Ticks) { Write-Host "tick $t/$Ticks : $tickLine" }
        if ($t -eq $Ticks -or ([DateTime]::UtcNow - $lastSample).TotalSeconds -ge $SampleIntervalSeconds) {
            Get-Sample $proc 'ticks' $t $SettingsCycles | Out-Null
            $lastSample = [DateTime]::UtcNow
        }
        if ($t -lt $Ticks -and $TickGapSec -gt 0) { Start-Sleep -Seconds $TickGapSec }
    }
    Close-Settings $proc 'ticks'
    Start-Sleep -Seconds 5
    Get-Sample $proc 'post-ticks' $Ticks $SettingsCycles | Out-Null
}

# ---- 6. final sample after a settle, verdict ---------------------------------------------------------------

Start-Sleep -Seconds 20
Get-Sample $proc 'final' $Ticks $SettingsCycles | Out-Null

$verdict = Get-Verdict $script:rows
Write-Result $verdict.Line
Write-Host $verdict.Line
if ($script:launched) { Stop-App }
if ($verdict.Status -eq 'FAIL') { exit 1 }
exit 0
