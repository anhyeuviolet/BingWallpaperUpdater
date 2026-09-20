# Seven-scenario live smoke run for the walking skeleton (Phase 1, Plan 04).
#
# Drives publish\BingWallpaperUpdater.exe against the real endpoints and reads the fixed log tokens
# (http, catalog source=, enrich hits=, cache hit, cache add, reconcile dropped=, apply ok). Prints one
# "PASS S<n> <name>" line per scenario and finally "SMOKE OK" (exit 0), or "SMOKE FAIL S<n>: <reason>"
# plus the last 40 log lines (exit 1) on the first failure.
#
#   S0 reset               stop the app, wipe %LocalAppData%\BingWallpaperUpdater, assert the exe exists
#   S1 first run           catalog source=github, enrich hits=, cache add, UHD download, index metadata, no window
#   S2 single instance     a second launch leaves exactly one process
#   S3 idempotent 2nd run  raw.githubusercontent.com 304, cache hit, no cache add, no large Bing download
#   S4 GitHub blocked      BWU_CATALOG_URL -> 404 path; catalog source=hpimagearchive; apply ok
#   S5 hand-deleted file   reconcile dropped=1, fresh cache add, apply ok
#   S6 host set            every "http <host>" line across S1-S5 is on the allow-list
#   S7 data folder         no *.tmp/*.part, settings.json market, state.json currentImageId == applied[0]
#
# The scripts touch only the app's own data folder and the app's own process. The run leaves today's
# wallpaper applied and the app stopped (forced stop; a stale tray icon clears on hover). Per-scenario
# log copies are kept as log.S<n>.txt inside the data folder for the SUMMARY / verifier.
#
#   dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/smoke-verify.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$exePath   = Join-Path $repoRoot 'publish\BingWallpaperUpdater.exe'
$data      = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath   = Join-Path $data 'log.txt'
$cacheDir  = Join-Path $data 'cache'
$indexPath = Join-Path $cacheDir 'index.json'
$procName  = 'BingWallpaperUpdater'
$allowedHosts = @('raw.githubusercontent.com', 'www.bing.com', 'cn.bing.com')

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
        [string[]]$FailPatterns = @(' pipeline failed ', ' apply failed ')
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

# "apply ok" is logged before MarkApplied/state.Save; wait until the index carries the applied id.
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
if ($jpgs[0].Name -notmatch '^\d{4}-\d{2}-\d{2}_[A-Za-z0-9]+\.jpg$') { Fail "cache file name '$($jpgs[0].Name)' does not match yyyy-MM-dd_Name.jpg" }
if ($jpgs[0].Name -ne $img.file) { Fail "cache file '$($jpgs[0].Name)' differs from index file '$($img.file)'" }
Assert-NoLeftovers

$running = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
if ($running.Count -ne 1) { Fail "expected 1 process after apply, found $($running.Count)" }
$running[0].Refresh()
if ($running[0].MainWindowHandle -ne 0) { Fail "MainWindowHandle is $($running[0].MainWindowHandle), expected 0 (no window)" }
Write-Host "  applied id=$appliedId file=$($img.file) bytes=$($img.bytes) dims=$($img.width)x$($img.height)"
Write-Host "  title=$($img.title)"
Write-Host 'PASS S1 first run (github catalog, enrichment, UHD download, no window)'

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

# ---- S3 idempotent second run ---------------------------------------------------------------------------

$script:currentScenario = 'S3'
Remove-LogOnly
$proc = Start-App
$applyLine = Wait-LogLine -Pattern ' INFO apply ok ' -Process $proc
$appliedId3 = Get-AppliedId $applyLine
Wait-Applied $appliedId3
$log = @(Read-Log)
Assert-LogContains $log 'http raw.githubusercontent.com 304 ' 'conditional GET answered 304' | Out-Null
Assert-LogContains $log 'cache hit id=' 'cached image reused' | Out-Null
Assert-LogLacks $log 'cache add' 'nothing re-downloaded'
$large = @(Get-HttpBytes $log @('www.bing.com', 'cn.bing.com') | Where-Object { $_.Bytes -gt 100000 })
if ($large.Count -gt 0) { Fail "a Bing response above 100000 bytes was fetched on the second run: $($large[0].Line)" }
$index = Read-Index
if (@($index.images).Count -ne 1) { Fail "index.json images.Count is $(@($index.images).Count) after the second run, expected 1" }
if ($appliedId3 -ne $appliedId) { Fail "second run applied '$appliedId3', first run applied '$appliedId'" }
Stop-App
Save-LogCopy 'S3'
Write-Host 'PASS S3 idempotent second run (304, cache hit, no download)'

# ---- S4 GitHub blocked -> HPImageArchive ----------------------------------------------------------------

$script:currentScenario = 'S4'
Remove-LogOnly
$random = [guid]::NewGuid().ToString('N').Substring(0, 12)
$blockedUrl = "https://raw.githubusercontent.com/niumoo/bing-wallpaper/main/does-not-exist-$random.md"
$proc = Start-App -CatalogUrl $blockedUrl
$applyLine = Wait-LogLine -Pattern ' INFO apply ok ' -Process $proc
$appliedId4 = Get-AppliedId $applyLine
Wait-Applied $appliedId4
$log = @(Read-Log)
Assert-LogContains $log 'http raw.githubusercontent.com 404 ' 'override URL answered 404' | Out-Null
Assert-LogContains $log 'catalog github failed' 'GitHub source reported as failed' | Out-Null
Assert-LogContains $log 'catalog source=hpimagearchive' 'HPImageArchive became the catalog' | Out-Null
Assert-LogContains $log 'apply ok' 'wallpaper still applied' | Out-Null
Stop-App
Save-LogCopy 'S4'
Write-Host 'PASS S4 github blocked -> hpimagearchive fallback, apply ok'

# ---- S5 hand-deleted file reconcile ---------------------------------------------------------------------

$script:currentScenario = 'S5'
$jpgs = @(Get-CacheJpegs)
if ($jpgs.Count -ne 1) { Fail "expected exactly 1 *.jpg before the delete, found $($jpgs.Count)" }
Remove-Item $jpgs[0].FullName -Force
Remove-LogOnly
$proc = Start-App
$applyLine = Wait-LogLine -Pattern ' INFO apply ok ' -Process $proc
$appliedId5 = Get-AppliedId $applyLine
Wait-Applied $appliedId5
$log = @(Read-Log)
$reconcileLine = Assert-LogContains $log 'reconcile dropped=1' 'missing file dropped from the index'
$addLine = Assert-LogContains $log 'cache add id=' 'image re-downloaded'
$reconcileAt = [array]::IndexOf($log, $reconcileLine)
$addAt = [array]::IndexOf($log, $addLine)
$applyAt = [array]::IndexOf($log, $applyLine)
if (-not ($reconcileAt -lt $addAt -and $addAt -lt $applyAt)) { Fail "expected order reconcile ($reconcileAt) < cache add ($addAt) < apply ok ($applyAt)" }
$index = Read-Index
if (@($index.images).Count -ne 1) { Fail "index.json images.Count is $(@($index.images).Count) after reconcile, expected 1" }
$file5 = Join-Path $cacheDir (@($index.images)[0].file)
if (-not (Test-Path $file5)) { Fail "index.json points at a missing file: $file5" }
Stop-App
Save-LogCopy 'S5'
Write-Host 'PASS S5 hand-deleted file -> reconcile dropped=1, fresh cache add, apply ok'

# ---- S6 host set ----------------------------------------------------------------------------------------

$script:currentScenario = 'S6'
$allLines = @()
foreach ($s in 'S1', 'S3', 'S4', 'S5') {
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
$settingsRaw = Get-Content (Join-Path $data 'settings.json') -Raw
if ($settingsRaw -notmatch '"market":\s*"en-US"') { Fail 'settings.json lacks "market": "en-US"' }
$state = Read-JsonFile (Join-Path $data 'state.json')
$index = Read-Index
if (@($index.applied).Count -lt 1) { Fail 'index.json applied is empty' }
if ($state.currentImageId -ne @($index.applied)[0]) { Fail "state.json currentImageId '$($state.currentImageId)' differs from index applied[0] '$(@($index.applied)[0])'" }
Write-Host 'PASS S7 data folder (no tmp/part, settings market, state == applied)'

Write-Host 'SMOKE OK'
exit 0
