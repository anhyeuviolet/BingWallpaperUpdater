# Eleven-scenario live smoke run for the Phase 2 contract (Phase 1 Plan 04 scenarios updated by Phase 2 Plan 04).
#
# Drives publish\BingWallpaperUpdater.exe against the real endpoints and reads the fixed log tokens
# (http, catalog source=, enrich hits=, cache hit, cache add, reconcile dropped=, apply ok, tick done, schedule nudge).
# Prints one "PASS S<n> <name>" line per scenario and finally "SMOKE OK" (exit 0), or "SMOKE FAIL S<n>: <reason>"
# plus the last 40 log lines (exit 1) on the first failure.
#
# Phase 2 contract (CONTEXT D-02, D-03, D-06, D-11): every launch runs one Startup tick that issues one conditional
# GET; the newest image is applied only when its ID differs from the persisted lastSeenNewestId (or the current
# file is missing); a restart with nothing new is a NoOp that never re-applies and keeps the persisted nextDueUtc;
# a past-due schedule catches up exactly once and re-arms to now + interval; a resume signal only nudges the heartbeat.
#
#   S0  reset                 stop the app, wipe %LocalAppData%\BingWallpaperUpdater, assert the exe exists
#   S1  first run             catalog source=github, enrich hits=, cache add, UHD download, index metadata, no window,
#                             tick done result=Applied decision=ApplyNew, state.json scheduler fields
#   S2  single instance       a second launch leaves exactly one process
#   S3  NoOp restart          raw.githubusercontent.com 304, cache hit, no cache add, no large Bing download,
#                             tick done result=NoOp decision=NoOp, no apply ok, nextDueUtc unchanged
#   S4  GitHub blocked        BWU_CATALOG_URL -> 404 path; catalog source=hpimagearchive; decision=NoOp and no apply ok
#                             (or, when HPImageArchive is a day ahead of the README, ApplyNew why=new exactly once);
#                             then GitHub back -> decision=NoOp, no apply ok (the older README newest is never re-applied)
#   S5  hand-deleted file     the applied image's file removed -> reconcile dropped=1, apply ok, decision=ApplyNew
#                             (why=missing-current with a fresh cache add; why=new served from the cache when the
#                             deleted image was HPImageArchive's day-ahead newest)
#   S6  host set              every "http <host>" line across S1-S5 is on the allow-list
#   S7  data folder           no *.tmp/*.part, settings.json market/interval/mode, state.json scheduler fields,
#                             state.json currentImageId == applied[0]
#   S8  past-due catch-up     state.json nextDueUtc 3 h in the past + fake lastSeenNewestId -> one Startup tick,
#                             result=Applied decision=ApplyNew why=new, apply ok, nextDueUtc re-armed to now + 30 min
#   S9  not-due restart       state.json nextDueUtc 20 min ahead + lastSeenNewestId == current -> NoOp, schedule kept
#   S10 synthetic resume      tools/power-probe.ps1 -NoLaunch against the S9 process -> POWER PROBE OK and
#                             "schedule nudge source=resume-automatic" in the live log
#
# The scripts touch only the app's own data folder and the app's own process (T-01-17 / T-02-17: the recursive delete
# and the state.json edits are limited to the folder ending in \BingWallpaperUpdater; Write-State edits only the
# named fields and never touches the cache directory). The run leaves today's wallpaper applied and the app stopped
# (forced stop; a stale tray icon clears on hover). Per-scenario log copies are kept as log.S<n>.txt inside the data
# folder for the SUMMARY / verifier.
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/smoke-verify.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$exePath   = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$probePath = Join-Path $PSScriptRoot 'power-probe.ps1'
$data      = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath   = Join-Path $data 'log.txt'
$statePath = Join-Path $data 'state.json'
$settingsPath = Join-Path $data 'settings.json'
$cacheDir  = Join-Path $data 'cache'
$indexPath = Join-Path $cacheDir 'index.json'
$procName  = 'BingWallpaperUpdater'
$allowedHosts = @('raw.githubusercontent.com', 'www.bing.com', 'cn.bing.com')
$fakeNewestId = 'OHR.SmokeFake_EN-US0000000001'
$utcFormat = 'yyyy-MM-ddTHH:mm:ssZ'

$script:currentScenario = 'S0'

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

function Fail([string]$reason) {
    Write-Host "SMOKE FAIL $($script:currentScenario): $reason"
    if (Test-Path $logPath) {
        Write-Host '--- last 40 log lines ---'
        Get-Content $logPath -Tail 40 | ForEach-Object { Write-Host $_ }
    }
    Stop-App
    exit 1
}

function Start-App([string]$CatalogUrl = $null) {
    # The override is read from the environment by CatalogService (BWU_CATALOG_URL); Start-Process inherits it.
    $previous = $env:BWU_CATALOG_URL
    try {
        if ($CatalogUrl) { $env:BWU_CATALOG_URL = $CatalogUrl } else { Remove-Item Env:\BWU_CATALOG_URL -ErrorAction SilentlyContinue }
        return Start-Process -FilePath $exePath -PassThru
    }
    finally {
        if ($null -ne $previous) { $env:BWU_CATALOG_URL = $previous } else { Remove-Item Env:\BWU_CATALOG_URL -ErrorAction SilentlyContinue }
    }
}

function Read-Log {
    if (-not (Test-Path $logPath)) { return @() }
    $lines = Get-Content $logPath -ErrorAction SilentlyContinue
    if ($null -eq $lines) { return @() }
    return @($lines)
}

function Read-Index {
    if (-not (Test-Path $indexPath)) { Fail "cache\index.json missing" }
    try { return Get-Content $indexPath -Raw | ConvertFrom-Json } catch { Fail "cache\index.json does not parse: $($_.Exception.Message)" }
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path $Path)) { Fail "$Path missing" }
    try { return Get-Content $Path -Raw | ConvertFrom-Json } catch { Fail "$Path does not parse: $($_.Exception.Message)" }
}

function Wait-LogLine {
    param(
        [Parameter(Mandatory = $true)] [string]$Pattern,
        [System.Diagnostics.Process]$Process = $null,
        [int]$TimeoutSec = 120,
        [string[]]$FailPatterns = @(' pipeline failed ', ' apply failed ', ' tick failed ')
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ($null -ne $Process -and $Process.HasExited) { Fail "process exited early with code $($Process.ExitCode)" }
        $lines = @(Read-Log)
        if ($lines.Count -eq 0) { continue }
        foreach ($fp in $FailPatterns) {
            $bad = $lines | Where-Object { $_ -match [regex]::Escape($fp) } | Select-Object -First 1
            if ($bad) { Fail "failure logged: $bad" }
        }
        $hit = $lines | Where-Object { $_ -match $Pattern } | Select-Object -First 1
        if ($hit) { return $hit }
    }
    Fail "no line matching '$Pattern' within $TimeoutSec s"
}

# Waits for the "tick done reason=<Reason> result=... decision=... why=... next=... retry=..." line that RotationService
# logs at the end of every tick (after state.json is saved) and returns it.
function Wait-TickDone([string]$Reason, [System.Diagnostics.Process]$Process = $null, [int]$TimeoutSec = 90) {
    return Wait-LogLine -Pattern (' INFO tick done reason=' + [regex]::Escape($Reason) + ' ') -Process $Process -TimeoutSec $TimeoutSec
}

# Extracts "<Name>=<value>" from a tick line (fields are space-separated key=value tokens).
function Get-TickField([string]$Line, [string]$Name) {
    if ($Line -notmatch ('(?:^| )' + [regex]::Escape($Name) + '=(?<value>\S+)')) { Fail "tick line lacks '$Name=': $Line" }
    return $Matches['value']
}

function Assert-TickField([string]$Line, [string]$Name, [string]$Expected) {
    $actual = Get-TickField $Line $Name
    if ($actual -ne $Expected) { Fail "tick line has $Name=$actual, expected $Expected`: $Line" }
}

function Read-State {
    return Read-JsonFile $statePath
}

# Raw string value of a state.json field as written on disk (for byte-identical comparisons); $null when absent/null.
function Get-StateRawValue([string]$Name) {
    if (-not (Test-Path $statePath)) { Fail "state.json missing" }
    $text = [System.IO.File]::ReadAllText($statePath)
    if ($text -notmatch ('"' + [regex]::Escape($Name) + '"\s*:\s*"(?<value>[^"]*)"')) { return $null }
    return $Matches['value']
}

# Rewrites only the named string fields of state.json in place (T-02-17). The edit is textual so every other field
# stays byte-identical and the file keeps its System.Text.Json shape (no BOM, no PowerShell date re-encoding).
function Write-State([hashtable]$Fields) {
    if (-not (Test-Path $statePath)) { Fail "state.json missing - cannot edit it" }
    if (-not $statePath.EndsWith('\BingWallpaperUpdater\state.json')) { Fail "refusing to edit '$statePath' - not the app state file" }
    $text = [System.IO.File]::ReadAllText($statePath)
    foreach ($name in @($Fields.Keys)) {
        $value = [string]$Fields[$name]
        if ($value -match '["\\]') { Fail "Write-State value for $name contains a quote or backslash: $value" }
        $pattern = '"' + [regex]::Escape($name) + '"\s*:\s*(?:"[^"]*"|null)'
        # -replace treats '$' in the replacement as a group reference ($1, ${name}, $&, ...); escape it as '$$' so the
        # value is written literally whatever it contains (IN-06). Inert for ISO dates and OHR. IDs, but not relied on.
        $replacement = ('"' + $name + '": "' + $value + '"').Replace('$', '$$')
        if ($text -match $pattern) {
            $text = $text -replace $pattern, $replacement
        } else {
            $text = $text -replace '^\s*\{', ("{`r`n  " + $replacement + ',')
        }
    }
    [System.IO.File]::WriteAllText($statePath, $text, (New-Object System.Text.UTF8Encoding($false)))
    try { $null = $text | ConvertFrom-Json } catch { Fail "state.json no longer parses after Write-State: $($_.Exception.Message)" }
}

function Assert-StateSchedulerFields([string]$What) {
    foreach ($field in 'nextDueUtc', 'lastCheckUtc', 'lastSeenNewestId') {
        if ([string]::IsNullOrWhiteSpace((Get-StateRawValue $field))) { Fail "state.json $field is missing or empty ($What)" }
    }
}

# "apply ok" is logged only after MarkApplied/state.Save (WR-07), so the index already carries the applied id
# by the time the line appears; this wait stays as a guard against log/file write reordering on disk.
function Wait-Applied([string]$ImageId, [int]$TimeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $indexPath) {
            try {
                $idx = Get-Content $indexPath -Raw | ConvertFrom-Json
                if ($idx.applied -and (@($idx.applied) -contains $ImageId)) { return }
            } catch { }
        }
        Start-Sleep -Milliseconds 300
    }
    Fail "index.json applied does not contain $ImageId after $TimeoutSec s"
}

function Get-AppliedId([string]$ApplyLine) {
    if ($ApplyLine -notmatch ' apply ok method=(?<method>\S+) id=(?<id>\S+) path=') { Fail "could not parse apply line: $ApplyLine" }
    return $Matches['id']
}

function Assert-LogContains([string[]]$Lines, [string]$Needle, [string]$What) {
    $hit = $Lines | Where-Object { $_.Contains($Needle) } | Select-Object -First 1
    if (-not $hit) { Fail "log lacks '$Needle' ($What)" }
    return $hit
}

function Assert-LogLacks([string[]]$Lines, [string]$Needle, [string]$What) {
    $hit = $Lines | Where-Object { $_.Contains($Needle) } | Select-Object -First 1
    if ($hit) { Fail "log unexpectedly contains '$Needle' ($What): $hit" }
}

# Byte counts of "http <host> <status> <bytes>" lines for the given hosts.
function Get-HttpBytes([string[]]$Lines, [string[]]$Hosts) {
    $result = @()
    foreach ($line in $Lines) {
        if ($line -match '^\S+ INFO http (?<host>\S+) (?<status>\d+) (?<bytes>\d+)\s*$') {
            if ($Hosts -contains $Matches['host']) {
                $result += [pscustomobject]@{ Host = $Matches['host']; Status = [int]$Matches['status']; Bytes = [long]$Matches['bytes']; Line = $line }
            }
        }
    }
    return $result
}

function Save-LogCopy([string]$Scenario) {
    if (Test-Path $logPath) { Copy-Item $logPath (Join-Path $data "log.$Scenario.txt") -Force }
}

function Remove-LogOnly {
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
}

function Get-CacheJpegs {
    if (-not (Test-Path $cacheDir)) { return @() }
    return @(Get-ChildItem -Path $cacheDir -Filter '*.jpg' -File)
}

function Assert-NoLeftovers {
    $leftovers = @(Get-ChildItem -Path $data -Recurse -File -Include '*.tmp', '*.part' -ErrorAction SilentlyContinue)
    if ($leftovers.Count -gt 0) { Fail "leftover temp files: $(($leftovers | ForEach-Object { $_.FullName }) -join ', ')" }
}

# ---- S0 reset -------------------------------------------------------------------------------------------

$script:currentScenario = 'S0'
if (-not (Test-Path $exePath)) { Fail "publish\BingWallpaperUpdater.exe is missing - run dotnet publish first" }
if (-not (Test-Path $probePath)) { Fail "tools\power-probe.ps1 is missing (needed by S10)" }
# T-01-17: the recursive delete is limited to the app's own folder built from a literal segment.
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or -not $data.EndsWith('\BingWallpaperUpdater')) { Fail "refusing to delete '$data' - not the app data folder" }
Stop-App
if (Test-Path $data) { Remove-Item -Path $data -Recurse -Force }
if (Test-Path $data) { Fail "could not remove $data" }
Write-Host 'PASS S0 reset'

# ---- S1 first run ---------------------------------------------------------------------------------------

$script:currentScenario = 'S1'
$proc = Start-App
$applyLine = Wait-LogLine -Pattern ' INFO apply ok ' -Process $proc
$appliedId = Get-AppliedId $applyLine
Wait-Applied $appliedId
$tickLine = Wait-TickDone 'Startup' $proc 30
Assert-TickField $tickLine 'result' 'Applied'
Assert-TickField $tickLine 'decision' 'ApplyNew'
$log = @(Read-Log)
Assert-LogContains $log 'catalog source=github' 'README catalog used' | Out-Null
Assert-LogContains $log 'enrich hits=' 'HPImageArchive enrichment ran' | Out-Null
Assert-LogContains $log 'cache add id=OHR.' 'image cached' | Out-Null
$bing200 = @(Get-HttpBytes $log @('www.bing.com', 'cn.bing.com') | Where-Object { $_.Status -eq 200 -and $_.Bytes -gt 1000000 })
if ($bing200.Count -lt 1) { Fail "no 'http www.bing.com 200 <bytes>' line above 1000000 bytes (UHD download)" }
$www = @($log | Where-Object { $_ -match '^\S+ INFO http www\.bing\.com 200 (\d+)\s*$' -and [long]$Matches[1] -gt 1000000 })
if ($www.Count -lt 1) { Fail "the UHD download was not served by www.bing.com: $($bing200[0].Line)" }

$index = Read-Index
if (@($index.images).Count -ne 1) { Fail "index.json images.Count is $(@($index.images).Count), expected 1" }
$img = @($index.images)[0]
if ($img.id -ne $appliedId) { Fail "index image id '$($img.id)' differs from applied id '$appliedId'" }
if ([string]::IsNullOrWhiteSpace([string]$img.title)) { Fail "index.json images[0].title is empty (enrichment missing)" }
if ([string]::IsNullOrWhiteSpace([string]$img.copyright)) { Fail "index.json images[0].copyright is empty (enrichment missing)" }
if (@($index.applied).Count -ne 1 -or @($index.applied)[0] -ne $img.id) { Fail "index.json applied is '$(@($index.applied) -join ',')', expected exactly '$($img.id)'" }

$jpgs = @(Get-CacheJpegs)
if ($jpgs.Count -ne 1) { Fail "expected exactly 1 *.jpg in cache, found $($jpgs.Count)" }
if ($jpgs[0].Name -notmatch '^\d{4}-\d{2}-\d{2}_[A-Za-z0-9]+_[A-Z]{2}-[A-Z]{2}[0-9]+\.jpg$') { Fail "cache file name '$($jpgs[0].Name)' does not match yyyy-MM-dd_Name_MARKETdigits.jpg" }
if ($jpgs[0].Name -ne $img.file) { Fail "cache file '$($jpgs[0].Name)' differs from index file '$($img.file)'" }
Assert-NoLeftovers

# Phase 2: the Startup tick persists the schedule (ROT-04, ROT-07, D-08).
Assert-StateSchedulerFields 'after the first Startup tick'
$state1 = Read-State
if ($state1.lastSeenNewestId -ne $appliedId) { Fail "state.json lastSeenNewestId is '$($state1.lastSeenNewestId)', expected the applied id $appliedId" }
$nextDueAfterS1 = Get-StateRawValue 'nextDueUtc'

$running = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
if ($running.Count -ne 1) { Fail "expected 1 process after apply, found $($running.Count)" }
$running[0].Refresh()
if ($running[0].MainWindowHandle -ne 0) { Fail "MainWindowHandle is $($running[0].MainWindowHandle), expected 0 (no window)" }
Write-Host "  applied id=$appliedId file=$($img.file) bytes=$($img.bytes) dims=$($img.width)x$($img.height)"
Write-Host "  title=$($img.title)"
Write-Host "  tick: $tickLine"
Write-Host "  nextDueUtc=$nextDueAfterS1"
Write-Host 'PASS S1 first run (github catalog, enrichment, UHD download, no window, Applied/ApplyNew, schedule persisted)'

# ---- S2 single instance ---------------------------------------------------------------------------------

$script:currentScenario = 'S2'
$second = Start-App
Start-Sleep -Seconds 3
$count = @(Get-Process -Name $procName -ErrorAction SilentlyContinue).Count
if ($count -ne 1) { Fail "expected exactly 1 process after a second launch, found $count" }
if (-not $second.HasExited) { Fail "second instance is still running" }
if ($second.ExitCode -ne 0) { Fail "second instance exited with code $($second.ExitCode), expected 0" }
Stop-App
Save-LogCopy 'S1'
Write-Host 'PASS S2 single instance (second launch exits 0, one process)'

# ---- S3 NoOp second run (restart with nothing new) ------------------------------------------------------

$script:currentScenario = 'S3'
Remove-LogOnly
$jpgsBefore = @(Get-CacheJpegs)
if ($jpgsBefore.Count -ne 1) { Fail "expected exactly 1 *.jpg before the second run, found $($jpgsBefore.Count)" }
$cacheWriteBefore = $jpgsBefore[0].LastWriteTimeUtc
$proc = Start-App
$tickLine = Wait-TickDone 'Startup' $proc
Assert-TickField $tickLine 'result' 'NoOp'
Assert-TickField $tickLine 'decision' 'NoOp'
$log = @(Read-Log)
Assert-LogLacks $log 'apply ok' 'a restart with nothing new never re-applies (D-11)'
Assert-LogContains $log 'http raw.githubusercontent.com 304 ' 'conditional GET answered 304' | Out-Null
# A NoOp tick never enters ImageCache.EnsureAsync, so the Phase 1 "cache hit id=" line cannot appear here; the cached
# image is proven reused by identity instead (same single file, untouched) and the "cache hit" token is asserted in S8,
# where the Phase 2 re-apply is served from the cache.
Assert-LogLacks $log 'cache add' 'nothing re-downloaded'
$large = @(Get-HttpBytes $log @('www.bing.com', 'cn.bing.com') | Where-Object { $_.Bytes -gt 100000 })
if ($large.Count -gt 0) { Fail "a Bing response above 100000 bytes was fetched on the second run: $($large[0].Line)" }
$jpgsAfter = @(Get-CacheJpegs)
if ($jpgsAfter.Count -ne 1) { Fail "expected exactly 1 *.jpg after the second run, found $($jpgsAfter.Count)" }
if ($jpgsAfter[0].Name -ne $jpgsBefore[0].Name) { Fail "cache file changed across the second run: '$($jpgsBefore[0].Name)' -> '$($jpgsAfter[0].Name)'" }
if ($jpgsAfter[0].LastWriteTimeUtc -ne $cacheWriteBefore) { Fail "cache file $($jpgsAfter[0].Name) was rewritten on the second run" }
$index = Read-Index
if (@($index.images).Count -ne 1) { Fail "index.json images.Count is $(@($index.images).Count) after the second run, expected 1" }
if (@($index.applied)[0] -ne $appliedId) { Fail "index applied[0] is '$(@($index.applied)[0])' after the second run, first run applied '$appliedId'" }
$nextDueAfterS3 = Get-StateRawValue 'nextDueUtc'
if ($nextDueAfterS3 -ne $nextDueAfterS1) { Fail "state.json nextDueUtc changed across the restart: '$nextDueAfterS1' -> '$nextDueAfterS3'" }
$state3 = Read-State
if ($state3.currentImageId -ne $appliedId) { Fail "state.json currentImageId is '$($state3.currentImageId)' after the second run, expected $appliedId" }
Stop-App
Save-LogCopy 'S3'
Write-Host "  tick: $tickLine"
Write-Host "  nextDueUtc unchanged: $nextDueAfterS3"
Write-Host 'PASS S3 NoOp second run (304, cache hit, no download, no re-apply, nextDueUtc kept)'

# ---- S4 GitHub blocked -> HPImageArchive ----------------------------------------------------------------

$script:currentScenario = 'S4'
Remove-LogOnly
$random = [guid]::NewGuid().ToString('N').Substring(0, 12)
$blockedUrl = "https://raw.githubusercontent.com/niumoo/bing-wallpaper/main/does-not-exist-$random.md"
$proc = Start-App -CatalogUrl $blockedUrl
$tickLine = Wait-TickDone 'Startup' $proc
$log = @(Read-Log)
Assert-LogContains $log 'http raw.githubusercontent.com 404 ' 'override URL answered 404' | Out-Null
Assert-LogContains $log 'catalog github failed' 'GitHub source reported as failed' | Out-Null
Assert-LogContains $log 'catalog source=hpimagearchive' 'HPImageArchive became the catalog' | Out-Null
# The two sources do not roll over at the same instant (WR-01). Most of the day they agree and the fallback is a
# NoOp; in the window where HPImageArchive already lists today's image and the README still lists yesterday's, the
# archive's newest is genuinely unseen and is applied exactly once (why=new). Both are correct. The deterministic
# part is the second half: once GitHub is back, the README's older ID must not be re-applied (no flip-flop).
$decision4 = Get-TickField $tickLine 'decision'
$archiveAhead = $false
$appliedId4 = $appliedId
switch ($decision4) {
    'NoOp' {
        Assert-LogLacks $log 'apply ok' 'HPImageArchive newest equals lastSeenNewestId, so the desktop is untouched'
    }
    'ApplyNew' {
        $archiveAhead = $true
        Assert-TickField $tickLine 'why' 'new'
        Assert-TickField $tickLine 'result' 'Applied'
        $applyLine4 = Assert-LogContains $log 'apply ok' 'HPImageArchive is a day ahead of the README: its newest is applied once'
        $appliedId4 = Get-AppliedId $applyLine4
        if ($appliedId4 -eq $appliedId) { Fail "HPImageArchive re-applied the README newest $appliedId" }
        Wait-Applied $appliedId4
    }
    default { Fail "tick line has decision=$decision4, expected NoOp or ApplyNew: $tickLine" }
}
Stop-App
Save-LogCopy 'S4'
Write-Host "  tick: $tickLine"

# GitHub back, README unchanged since S1: its newest is already cached no later than the last-seen one -> NoOp.
Remove-LogOnly
$proc = Start-App
$tickLine = Wait-TickDone 'Startup' $proc
$log = @(Read-Log)
Assert-LogContains $log 'catalog source=github' 'GitHub is the catalog again' | Out-Null
Assert-TickField $tickLine 'decision' 'NoOp'
Assert-LogLacks $log 'apply ok' 'GitHub recovering must not re-apply an older README newest (no flip-flop)'
$state4 = Read-State
if ($state4.currentImageId -ne $appliedId4) { Fail "state.json currentImageId is '$($state4.currentImageId)' after GitHub recovered, expected $appliedId4" }
Stop-App
Save-LogCopy 'S4b'
Write-Host "  tick: $tickLine"
if ($archiveAhead) { Write-Host 'PASS S4 github blocked -> hpimagearchive fallback (a day ahead: applied once); github back -> no flip-flop' }
else { Write-Host 'PASS S4 github blocked -> hpimagearchive fallback, no re-apply; github back -> no flip-flop' }

# ---- S5 hand-deleted file reconcile ---------------------------------------------------------------------

$script:currentScenario = 'S5'
# Delete the file of the image on the desktop (index applied[0]). After S4 the cache holds one image, or two when
# HPImageArchive was a day ahead; the count is only required to be consistent across the reconcile below.
$index5 = Read-Index
$imagesBefore5 = @($index5.images).Count
$currentId5 = [string]@($index5.applied)[0]
$entry5 = @($index5.images) | Where-Object { $_.id -eq $currentId5 } | Select-Object -First 1
if (-not $entry5) { Fail "index.json has no image entry for applied[0] '$currentId5'" }
$file5Before = Join-Path $cacheDir $entry5.file
if (-not (Test-Path $file5Before)) { Fail "applied image file is missing before the delete: $file5Before" }
Remove-Item $file5Before -Force
Remove-LogOnly
$proc = Start-App
$applyLine = Wait-LogLine -Pattern ' INFO apply ok ' -Process $proc
$appliedId5 = Get-AppliedId $applyLine
Wait-Applied $appliedId5
$tickLine = Wait-TickDone 'Startup' $proc 30
Assert-TickField $tickLine 'decision' 'ApplyNew'
$log = @(Read-Log)
$reconcileLine = Assert-LogContains $log 'reconcile dropped=1' 'missing file dropped from the index'
$reconcileAt = [array]::IndexOf($log, $reconcileLine)
$applyAt = [array]::IndexOf($log, $applyLine)
$why5 = Get-TickField $tickLine 'why'
$expectedImages5 = $imagesBefore5
if ($why5 -eq 'missing-current') {
    # The catalog newest is the missing image: re-downloaded, then applied.
    $addLine = Assert-LogContains $log 'cache add id=' 'image re-downloaded'
    $addAt = [array]::IndexOf($log, $addLine)
    if (-not ($reconcileAt -lt $addAt -and $addAt -lt $applyAt)) { Fail "expected order reconcile ($reconcileAt) < cache add ($addAt) < apply ok ($applyAt)" }
} elseif ($archiveAhead -and $why5 -eq 'new') {
    # The deleted image was HPImageArchive's day-ahead newest and the README still lists yesterday's: with the
    # last-seen entry gone from the cache nothing vouches for the README newest, so it is applied from its cached file.
    Assert-LogContains $log 'cache hit id=' 'README newest served from the cache' | Out-Null
    Assert-LogLacks $log 'cache add' 'nothing re-downloaded: the README newest was still cached'
    if (-not ($reconcileAt -lt $applyAt)) { Fail "expected order reconcile ($reconcileAt) < apply ok ($applyAt)" }
    $expectedImages5 = $imagesBefore5 - 1
} else {
    Fail "tick line has why=$why5, expected missing-current (or new after a day-ahead HPImageArchive apply): $tickLine"
}
$index = Read-Index
if (@($index.images).Count -ne $expectedImages5) { Fail "index.json images.Count is $(@($index.images).Count) after reconcile, expected $expectedImages5" }
$entry5After = @($index.images) | Where-Object { $_.id -eq $appliedId5 } | Select-Object -First 1
if (-not $entry5After) { Fail "index.json has no image entry for the applied id $appliedId5" }
$file5 = Join-Path $cacheDir $entry5After.file
if (-not (Test-Path $file5)) { Fail "index.json points at a missing file: $file5" }
Stop-App
Save-LogCopy 'S5'
Write-Host "  tick: $tickLine"
Write-Host "PASS S5 hand-deleted file -> reconcile dropped=1, apply ok (ApplyNew why=$why5)"

# ---- S6 host set ----------------------------------------------------------------------------------------

$script:currentScenario = 'S6'
$allLines = @()
foreach ($s in 'S1', 'S3', 'S4', 'S4b', 'S5') {
    $copy = Join-Path $data "log.$s.txt"
    if (-not (Test-Path $copy)) { Fail "log copy $copy missing" }
    $allLines += @(Get-Content $copy)
}
$seenHosts = @{}
foreach ($line in $allLines) {
    if ($line -match '^\S+ INFO http (\S+) ') {
        $h = $Matches[1]
        $seenHosts[$h] = $true
        if ($allowedHosts -notcontains $h) { Fail "host outside the allow-list was contacted: $line" }
    }
}
foreach ($required in 'raw.githubusercontent.com', 'www.bing.com') {
    if (-not $seenHosts.ContainsKey($required)) { Fail "no http line for required host $required" }
}
Write-Host "  hosts seen: $(($seenHosts.Keys | Sort-Object) -join ', ')"
Write-Host 'PASS S6 host set (only raw.githubusercontent.com / www.bing.com / cn.bing.com)'

# ---- S7 data folder sanity ------------------------------------------------------------------------------

$script:currentScenario = 'S7'
Assert-NoLeftovers
$settingsRaw = Get-Content $settingsPath -Raw
if ($settingsRaw -notmatch '"market":\s*"en-US"') { Fail 'settings.json lacks "market": "en-US"' }
if ($settingsRaw -notmatch '"intervalMinutes":\s*30') { Fail 'settings.json lacks "intervalMinutes": 30' }
if ($settingsRaw -notmatch '"mode":\s*"newest"') { Fail 'settings.json lacks "mode": "newest"' }
Assert-StateSchedulerFields 'S7 data folder'
$state = Read-State
$index = Read-Index
if (@($index.applied).Count -lt 1) { Fail 'index.json applied is empty' }
if ($state.currentImageId -ne @($index.applied)[0]) { Fail "state.json currentImageId '$($state.currentImageId)' differs from index applied[0] '$(@($index.applied)[0])'" }
Write-Host 'PASS S7 data folder (no tmp/part, settings interval/mode, state scheduler fields, state == applied)'

# ---- S8 past-due catch-up -------------------------------------------------------------------------------

$script:currentScenario = 'S8'
Stop-App
Remove-LogOnly
$pastDue = (Get-Date).ToUniversalTime().AddHours(-3).ToString($utcFormat)
Write-State @{ nextDueUtc = $pastDue; lastSeenNewestId = $fakeNewestId }
if ((Get-StateRawValue 'nextDueUtc') -ne $pastDue) { Fail "Write-State did not set nextDueUtc to $pastDue" }
if ((Get-StateRawValue 'lastSeenNewestId') -ne $fakeNewestId) { Fail "Write-State did not set lastSeenNewestId to $fakeNewestId" }
$before = Get-Date
$proc = Start-App
$tickLine = Wait-TickDone 'Startup' $proc
Assert-TickField $tickLine 'result' 'Applied'
Assert-TickField $tickLine 'decision' 'ApplyNew'
Assert-TickField $tickLine 'why' 'new'
$log = @(Read-Log)
$applyLine = Assert-LogContains $log 'apply ok' 'past-due schedule with a new catalog id applied once'
$appliedId8 = Get-AppliedId $applyLine
Wait-Applied $appliedId8
Assert-LogContains $log 'cache hit id=' 'the catch-up apply was served from the cache' | Out-Null
Assert-LogLacks $log 'cache add' 'nothing re-downloaded for the catch-up'
$applyLines = @($log | Where-Object { $_.Contains(' apply ok ') })
if ($applyLines.Count -ne 1) { Fail "expected exactly one apply ok in the catch-up run, found $($applyLines.Count)" }
$tickLines = @($log | Where-Object { $_ -match ' INFO tick reason=' })
if ($tickLines.Count -ne 1) { Fail "expected exactly one tick in the catch-up run, found $($tickLines.Count)" }
$nextDueRaw8 = Get-StateRawValue 'nextDueUtc'
if ([string]::IsNullOrWhiteSpace($nextDueRaw8)) { Fail 'state.json nextDueUtc missing after the catch-up tick' }
$nextDue8 = [DateTimeOffset]::Parse($nextDueRaw8, [cultureinfo]::InvariantCulture)
$lower = [DateTimeOffset]$before.AddMinutes(29)
$upper = [DateTimeOffset](Get-Date).AddMinutes(31)
if ($nextDue8 -lt $lower -or $nextDue8 -gt $upper) { Fail "nextDueUtc $($nextDue8.ToString('o')) is not within 29-31 min of now ($($lower.ToString('o')) .. $($upper.ToString('o')))" }
$state8 = Read-State
if ($state8.lastSeenNewestId -ne $appliedId8) { Fail "state.json lastSeenNewestId is '$($state8.lastSeenNewestId)', expected the applied id $appliedId8" }
if ($state8.currentImageId -ne $appliedId8) { Fail "state.json currentImageId is '$($state8.currentImageId)', expected $appliedId8" }
Stop-App
Save-LogCopy 'S8'
Write-Host "  tick: $tickLine"
Write-Host "  nextDueUtc before=$pastDue after=$nextDueRaw8"
Write-Host 'PASS S8 past-due state -> one catch-up tick, apply ok, nextDueUtc re-armed'

# ---- S9 not-due restart, unchanged catalog --------------------------------------------------------------

$script:currentScenario = 'S9'
Remove-LogOnly
$futureDue = (Get-Date).ToUniversalTime().AddMinutes(20).ToString($utcFormat)
$currentId = (Read-State).currentImageId
if ([string]::IsNullOrWhiteSpace([string]$currentId)) { Fail 'state.json currentImageId is empty before S9' }
Write-State @{ nextDueUtc = $futureDue; lastSeenNewestId = $currentId }
$proc = Start-App   # left running for S10
$tickLine = Wait-TickDone 'Startup' $proc
Assert-TickField $tickLine 'result' 'NoOp'
Assert-TickField $tickLine 'decision' 'NoOp'
$log = @(Read-Log)
Assert-LogLacks $log 'apply ok' 'future schedule with an unchanged catalog never re-applies'
$nextDueRaw9 = Get-StateRawValue 'nextDueUtc'
if ([string]::IsNullOrWhiteSpace($nextDueRaw9)) { Fail 'state.json nextDueUtc missing after the not-due tick' }
$written9 = [DateTimeOffset]::Parse($futureDue, [cultureinfo]::InvariantCulture)
$nextDue9 = [DateTimeOffset]::Parse($nextDueRaw9, [cultureinfo]::InvariantCulture)
$driftSec = [math]::Abs(($nextDue9 - $written9).TotalSeconds)
if ($driftSec -gt 1) { Fail "nextDueUtc moved across the restart: wrote $futureDue, read $nextDueRaw9 (drift $driftSec s)" }
$state9 = Read-State
if ($state9.lastSeenNewestId -ne $currentId) { Fail "state.json lastSeenNewestId is '$($state9.lastSeenNewestId)', expected $currentId" }
if ($proc.HasExited) { Fail "process exited before S10 with code $($proc.ExitCode)" }
Save-LogCopy 'S9'
Write-Host "  tick: $tickLine"
Write-Host "  nextDueUtc written=$futureDue read=$nextDueRaw9"
Write-Host 'PASS S9 future nextDueUtc + unchanged catalog -> NoOp, schedule kept'

# ---- S10 synthetic resume (WM_POWERBROADCAST / PBT_APMRESUMEAUTOMATIC) ------------------------------------

$script:currentScenario = 'S10'
# The probe reports through stdout only (Write-Host); stderr is left alone so a stray native error cannot become a
# terminating NativeCommandError under $ErrorActionPreference = 'Stop' in PowerShell 5.1.
$probeOutput = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $probePath -NoLaunch | ForEach-Object { [string]$_ })
$probeExit = $LASTEXITCODE
$probeOutput | ForEach-Object { Write-Host "  probe: $_" }
if ($probeExit -ne 0) { Fail "power-probe.ps1 -NoLaunch exited with code $probeExit" }
$probeOk = @($probeOutput | Where-Object { $_ -match '^POWER PROBE OK ' })
if ($probeOk.Count -lt 1) { Fail 'power-probe.ps1 output lacks "POWER PROBE OK"' }
$log = @(Read-Log)
$nudgeLine = Assert-LogContains $log 'schedule nudge source=resume-automatic' 'synthetic resume nudged the heartbeat'
Assert-LogContains $log 'power window hwnd=0x' 'hidden power window registered' | Out-Null
Assert-LogLacks $log 'apply ok' 'a resume signal only nudges, it never applies'
if ($proc.HasExited) { Fail "process exited during S10 with code $($proc.ExitCode)" }
Stop-App
Save-LogCopy 'S10'
Write-Host "  nudge: $nudgeLine"
Write-Host 'PASS S10 synthetic WM_POWERBROADCAST resume -> schedule nudge logged'

Write-Host 'SMOKE OK'
exit 0
