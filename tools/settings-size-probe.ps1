# Settings window size probe (quick task 260922-j3u; UI-04, Phase 04 deferred item 2).
#
# Proves, on the published build, that the Settings window's outer size does not follow the current image's
# title/copyright text: it opens Settings once, posts -Ticks (8) "Next wallpaper" clicks to the form and reads the
# window rectangle (GetWindowRect) after every tick while the description lengths vary from image to image.
#
# Flow:
#   1. launch publish\BingWallpaperUpdater.exe (or -NoLaunch: attach to the running instance), wait for the first
#      tick, which must be result=Applied (an offline machine fails here on purpose);
#   2. open Settings through the Local\BingWallpaperUpdater.Show event, wait for "settings window action=open", read
#      the HWND of the Next button (AutomationId NextButton) through UI Automation and print the "settings window
#      layout" log line the fixed build writes on every open (missing line = stale publish);
#   3. baseline rect, then per tick: wait for the Next button to be enabled, post WM_COMMAND/BN_CLICKED to the form
#      with the button HWND (the same path tools/soak.ps1 uses: WinForms reflects it to Button.Click, no mouse input,
#      no focus change), wait for " tick done reason=Next ", 500 ms for StateChanged -> BeginInvoke -> RefreshStatus ->
#      layout, read the rect again and the current image's title/copyright lengths from state.json + cache\index.json;
#   4. verdict: every rect must equal the baseline and at least two distinct (title, copyright) length pairs must
#      have been shown; close Settings, stop the app (launch mode only).
#
# The Settings window is NOT minimized (a minimized window reports the iconic -32000 rect), so it stays visible and may
# keep the foreground for the ~20-30 s the probe runs: keep your hands off the keyboard and mouse meanwhile.
#
# The probe never touches the registry. A fresh launch writes the HKCU Run value (Autostart defaults to true); remove
# it afterwards with:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/autostart-probe.ps1 -Cleanup
#
# Output contract:
#   SETTINGS SIZE PROBE OK ticks=<n> window=<W>x<H> distinct-descriptions=<k>   (exit 0)
#   SETTINGS SIZE PROBE FAIL: <reason>                                           (exit 1, plus the last 20 log lines)
#
#   -FreshData     (launch mode only) delete %LocalAppData%\BingWallpaperUpdater first (only a path ending in
#                  \BingWallpaperUpdater under a non-empty LOCALAPPDATA is ever removed) so the cache backfills during
#                  the ticks and Next steps through several different images.
#   -NoLaunch      attach to the running BingWallpaperUpdater process and leave it running afterwards.
#   -Ticks 8       Next clicks to post.
#   -TimeoutSec 15 window appear/close waits.
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/settings-size-probe.ps1 -FreshData -Ticks 8
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/autostart-probe.ps1 -Cleanup

param(
    [switch]$FreshData,
    [switch]$NoLaunch,
    [int]$Ticks = 8,
    [int]$TimeoutSec = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$exePath   = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$data      = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath   = Join-Path $data 'log.txt'
$statePath = Join-Path $data 'state.json'
$indexPath = Join-Path $data 'cache\index.json'
$procName  = 'BingWallpaperUpdater'
$showEventName = 'Local\BingWallpaperUpdater.Show'
$openToken   = 'settings window action=open'
$closeToken  = 'settings window action=close'
$layoutToken = 'settings window layout'
$tickPattern = ' tick (done|failed) '
$nextTickPattern = ' tick done reason=Next '

$script:launched = $false

# Any unexpected exception still ends in the FAIL contract and never leaves the launched app running.
trap {
    Write-Host "SETTINGS SIZE PROBE FAIL: unexpected error: $($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"
    if ($script:launched) { Stop-App }
    exit 1
}

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

function Fail([string]$reason) {
    Write-Host "SETTINGS SIZE PROBE FAIL: $reason"
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

# ---- 1. launch / attach ----------------------------------------------------------------------------------

if ($NoLaunch) {
    $p = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
    if ($p.Count -ne 1) { Fail "-NoLaunch: expected exactly one running $procName process, found $($p.Count)" }
    $proc = $p[0]
    Write-Host "Using running process pid=$($proc.Id)"
    if (-not (Find-LastLogLine $tickPattern)) {
        $t = Wait-LogLine -Pattern $tickPattern -SkipLines 0 -Timeout 180 -Process $proc
        if (-not $t) { Fail "no 'tick done' log line within 180 s" }
    }
} else {
    if (-not (Test-Path $exePath)) { Fail "publish\BingWallpaperUpdater.exe is missing - run dotnet publish first" }
    Stop-App
    if ($FreshData) {
        # Same guard as the soak: only the folder ending in \BingWallpaperUpdater under LocalAppData is ever deleted.
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
    if ($tickLine -notmatch ' result=Applied ') { Fail "the first tick did not apply an image (offline?): $tickLine" }
}

$p = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
if ($p.Count -ne 1) { Fail "expected exactly one $procName process, found $($p.Count)" }
$proc = $p[0]
$proc.Refresh()
if ($proc.MainWindowHandle -ne 0) { Fail "the tray process already has a main window (handle $($proc.MainWindowHandle)); close Settings first" }

# ---- 2. user32 + UI Automation ---------------------------------------------------------------------------

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace SizeProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public static class U
    {
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }
}
'@
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$WM_COMMAND = 0x0111   # wParam LOWORD = control id (0), HIWORD = BN_CLICKED (0); lParam = the button's HWND

function Get-WindowSize([IntPtr]$hwnd) {
    $rect = New-Object SizeProbe.RECT
    if (-not [SizeProbe.U]::GetWindowRect($hwnd, [ref]$rect)) { Fail "GetWindowRect failed for hwnd 0x$($hwnd.ToInt64().ToString('X'))" }
    return ('{0}x{1}' -f ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
}

# Title/copyright lengths of the current image: state.json currentImageId looked up in cache\index.json images[].
function Get-CurrentDescription {
    if (-not (Test-Path $statePath)) { Fail "state.json is missing" }
    if (-not (Test-Path $indexPath)) { Fail "cache\index.json is missing" }
    $state = Get-Content $statePath -Raw | ConvertFrom-Json
    $index = Get-Content $indexPath -Raw | ConvertFrom-Json
    $id = [string]$state.currentImageId
    if ([string]::IsNullOrEmpty($id)) { Fail "state.json has no currentImageId" }
    $img = @($index.images | Where-Object { $_.id -eq $id }) | Select-Object -First 1
    if ($null -eq $img) { Fail "current image $id is not in cache\index.json" }
    # A null title/copyright is omitted from index.json altogether, so the property may be absent (StrictMode).
    return @{ Id = $id; Title = (Get-TextLength $img 'title'); Copyright = (Get-TextLength $img 'copyright') }
}

function Get-TextLength($obj, [string]$name) {
    $prop = $obj.PSObject.Properties[$name]
    if ($null -eq $prop -or $null -eq $prop.Value) { return 0 }
    return ([string]$prop.Value).Length
}

# ---- 3. open Settings, find the Next button, print the layout line ---------------------------------------

$skip = @(Read-Log).Count
try {
    $evt = [System.Threading.EventWaitHandle]::OpenExisting($showEventName)
    $evt.Set() | Out-Null
    $evt.Dispose()
} catch { Fail "cannot signal $showEventName : $($_.Exception.Message)" }
if (-not (Wait-Window $proc $true $TimeoutSec)) { Fail "no main window within $TimeoutSec s after the Show signal" }
$proc.Refresh()
$formHwnd = $proc.MainWindowHandle
$openLine = Wait-LogLine -Pattern ([regex]::Escape($openToken)) -SkipLines $skip -Timeout $TimeoutSec -Process $proc
if (-not $openLine) { Fail "no '$openToken' log line" }

$nextHwnd = [IntPtr]::Zero
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NextButton')
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline -and $nextHwnd -eq [IntPtr]::Zero) {
    $button = [System.Windows.Automation.AutomationElement]::FromHandle($formHwnd).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -ne $button -and $button.Current.NativeWindowHandle -ne 0) { $nextHwnd = [IntPtr]$button.Current.NativeWindowHandle }
    else { Start-Sleep -Milliseconds 100 }
}
if ($nextHwnd -eq [IntPtr]::Zero) { Fail "NextButton not found on the Settings window (UI Automation)" }

$layoutLine = @(@(Read-Log) | Select-Object -Skip $skip | Where-Object { $_ -match [regex]::Escape($layoutToken) }) | Select-Object -Last 1
if (-not $layoutLine) { Fail "no '$layoutToken' log line after the open - publish is stale, rebuild (dotnet publish ...)" }
Write-Host "Layout: $layoutLine"
Write-Host ('Settings hwnd=0x{0:X} NextButton hwnd=0x{1:X} (window stays visible; clicks are posted as WM_COMMAND/BN_CLICKED)' -f $formHwnd.ToInt64(), $nextHwnd.ToInt64())

# ---- 4. baseline + ticks ---------------------------------------------------------------------------------

$baseline = Get-WindowSize $formHwnd
$d0 = Get-CurrentDescription
Write-Host ('tick 0 id={0} title={1} copyright={2} window={3}' -f $d0.Id, $d0.Title, $d0.Copyright, $baseline)
$pairs = @{}
$pairs[('{0}/{1}' -f $d0.Title, $d0.Copyright)] = $true
$failedTick = $null
$failedSize = $null

for ($t = 1; $t -le $Ticks; $t++) {
    # The app disables the button while a tick runs (D-05); IsWindowEnabled mirrors Control.Enabled.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not [SizeProbe.U]::IsWindowEnabled($nextHwnd)) { Start-Sleep -Milliseconds 50 }
    if (-not [SizeProbe.U]::IsWindowEnabled($nextHwnd)) { Fail "the Next button stayed disabled for 30 s before tick $t" }
    $skip = @(Read-Log).Count
    if (-not [SizeProbe.U]::PostMessage($formHwnd, $WM_COMMAND, [IntPtr]::Zero, $nextHwnd)) { Fail "PostMessage WM_COMMAND failed (tick $t)" }
    $tickLine = Wait-LogLine -Pattern $nextTickPattern -SkipLines $skip -Timeout 90 -Process $proc
    if (-not $tickLine) { Fail "no 'tick done reason=Next' log line within 90 s after Next (tick $t)" }
    Start-Sleep -Milliseconds 500   # StateChanged -> BeginInvoke -> RefreshStatus -> layout
    $size = Get-WindowSize $formHwnd
    $d = Get-CurrentDescription
    Write-Host ('tick {0} id={1} title={2} copyright={3} window={4}' -f $t, $d.Id, $d.Title, $d.Copyright, $size)
    $pairs[('{0}/{1}' -f $d.Title, $d.Copyright)] = $true
    if ($size -ne $baseline -and $null -eq $failedTick) { $failedTick = $t; $failedSize = $size }
}

# ---- 5. close, verdict -----------------------------------------------------------------------------------

$skip = @(Read-Log).Count
if (-not $proc.CloseMainWindow()) { Fail "CloseMainWindow returned false" }
if (-not (Wait-Window $proc $false $TimeoutSec)) { Fail "the Settings window is still open $TimeoutSec s after CloseMainWindow" }
$closeLine = Wait-LogLine -Pattern ([regex]::Escape($closeToken)) -SkipLines $skip -Timeout $TimeoutSec -Process $proc
if (-not $closeLine) { Fail "no '$closeToken' log line" }

if ($null -ne $failedTick) { Fail "window size changed at tick $failedTick`: $failedSize vs $baseline" }
$distinct = $pairs.Keys.Count
if ($distinct -lt 2) { Fail "descriptions did not vary across $Ticks ticks (need >= 2 cached images with different description lengths)" }

if ($script:launched) { Stop-App }
Write-Host ('SETTINGS SIZE PROBE OK ticks={0} window={1} distinct-descriptions={2}' -f $Ticks, $baseline, $distinct)
exit 0
