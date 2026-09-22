# Live install probe (Phase 4, Plan 02; INST-01, INST-03, INST-04, INST-05, L10N-04).
#
# Drives the real Setup.exe end to end without a human: silent fresh install -> assert the per-user payload, the HKCU
# uninstall key, the Start-menu shortcut, the Run value (task state) and the seeded settings.json -> first launch
# (startup version=, apply ok, tick done reason=Startup, no window, no child process, only the three allowed hosts)
# -> silent upgrade over the running instance (shutdown reason=exit-signal, settings.json byte-identical, cache and
# Run value kept, hidden autostart task ignored) -> relaunch -> silent uninstall while running (second exit-signal,
# install dir / uninstall key / shortcut / Run / StartupApproved gone, data folder kept). Prints
# "INSTALL PROBE OK version=<v> mode=<default|no-autostart> installDir=<dir> tickWithin=<s>" (exit 0) or
# "INSTALL PROBE FAIL: <reason>" plus the last 20 lines of log.txt and of the current Setup log, then stops the app
# and attempts a silent uninstall (exit 1).
#
#   -Version         X.Y.Z the installer was built with (default 0.0.0); asserted against DisplayVersion and the log.
#   -SetupExe        path to the installer (default <repo>\dist\BingWallpaperUpdater-<Version>-x64-Setup.exe).
#   -NoAutostartTask install with /LANG=vi /MERGETASKS=!autostart: expects no Run value, "autostart": false and
#                    "language": "vi" in the seed, and "ui language setting=vi" at first launch.
#   -TimeoutSec      how long to wait for the first tick after launch (default 120).
#
# The probe touches only the app's own install dir, its own data folder (recursive delete limited to the folder ending
# in \BingWallpaperUpdater), its own process and the BingWallpaperUpdater value under the two HKCU autostart keys; it
# never enumerates registry keys and never deletes anything else. Run the no-autostart mode first, then the default
# mode, so the machine ends with default settings and no install.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1 -Version 0.0.0
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/install-probe.ps1 -Version 0.0.0 -NoAutostartTask
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/install-probe.ps1 -Version 0.0.0

param(
    [string]$Version = '0.0.0',
    [string]$SetupExe = '',
    [switch]$NoAutostartTask,
    [int]$TimeoutSec = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SetupExe)) { $SetupExe = Join-Path $repoRoot "dist\BingWallpaperUpdater-$Version-x64-Setup.exe" }
$installDir   = Join-Path $env:LOCALAPPDATA 'Programs\BingWallpaperUpdater'
$exe          = Join-Path $installDir 'BingWallpaperUpdater.exe'
$unins        = Join-Path $installDir 'unins000.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4AD7C4B2-6A1C-443D-95F9-74E5239D058C}_is1'
$runKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$valueName    = 'BingWallpaperUpdater'
$procName     = 'BingWallpaperUpdater'
$data         = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$logPath      = Join-Path $data 'log.txt'
$settingsPath = Join-Path $data 'settings.json'
$cacheDir     = Join-Path $data 'cache'
$shortcut     = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Bing Wallpaper Updater.lnk'
$allowedHosts = @('raw.githubusercontent.com', 'www.bing.com', 'cn.bing.com')
$expectedRun  = '"' + $exe + '" --startup'
$mode         = if ($NoAutostartTask) { 'no-autostart' } else { 'default' }
$setupLogDir  = Join-Path $env:TEMP 'bwu-install-probe'
$silentArgs   = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')

$script:setupLog = $null
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
    $lines = Get-Content $logPath -ErrorAction SilentlyContinue
    if ($null -eq $lines) { return @() }
    return @($lines)
}

function Show-Tail([string]$Path, [string]$Title) {
    if (Test-Path $Path) {
        $lines = @(Get-Content $Path -ErrorAction SilentlyContinue)
        if ($lines.Count -gt 0) {
            Write-Host "--- last 20 lines of $Title ---"
            $lines | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
        }
    }
}

function Fail([string]$reason) {
    Write-Host "INSTALL PROBE FAIL: $reason"
    Show-Tail $logPath 'log.txt'
    if ($script:setupLog) { Show-Tail $script:setupLog (Split-Path -Leaf $script:setupLog) }
    Stop-App
    if (Test-Path $unins) {
        try { Start-Process -FilePath $unins -ArgumentList $silentArgs -Wait | Out-Null } catch { }
    }
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
function Get-NamedValue([string]$Key, [string]$Name) {
    if (-not (Test-Path $Key)) { return $null }
    $item = Get-ItemProperty -Path $Key -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return $item.$Name
}

function Get-AppValue([string]$Key) { return Get-NamedValue $Key $valueName }

function Remove-AppValue([string]$Key) {
    if (Test-Path $Key) {
        Remove-ItemProperty -Path $Key -Name $valueName -ErrorAction SilentlyContinue
    }
}

function Format-Value($value) {
    if ($null -eq $value) { return '<absent>' }
    if ($value -is [byte[]]) { return (($value | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') }
    return [string]$value
}

function Get-AppProcesses { return @(Get-Process -Name $procName -ErrorAction SilentlyContinue) }

function Get-JpgCount {
    if (-not (Test-Path $cacheDir)) { return 0 }
    return @(Get-ChildItem -Path $cacheDir -Filter *.jpg -File -ErrorAction SilentlyContinue).Count
}

function Wait-ExeGone([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Test-Path $exe)) { Start-Sleep -Milliseconds 500 }
    return -not (Test-Path $exe)
}

function Invoke-Setup([string]$LogName, [string[]]$Extra) {
    New-Item -ItemType Directory -Path $setupLogDir -Force | Out-Null
    $script:setupLog = Join-Path $setupLogDir $LogName
    Remove-Item -Path $script:setupLog -ErrorAction SilentlyContinue
    $setupArgs = @($silentArgs) + @("/LOG=`"$($script:setupLog)`"") + @($Extra)
    Write-Host "  Setup args: $($setupArgs -join ' ')"
    $p = Start-Process -FilePath $SetupExe -ArgumentList $setupArgs -Wait -PassThru
    return $p.ExitCode
}

function Assert-RunValue([string]$Where) {
    $actual = Get-AppValue $runKey
    if ($NoAutostartTask) {
        if ($null -ne $actual) { Fail "$Where`: a Run value exists although the autostart task was unticked: $(Format-Value $actual)" }
    } else {
        if ($null -eq $actual) { Fail "$Where`: no '$valueName' value under $runKey" }
        if (-not ($actual -is [string])) { Fail "$Where`: the Run value is not REG_SZ (got $($actual.GetType().Name))" }
        if (-not ($actual -ceq $expectedRun)) { Fail "$Where`: Run value mismatch`n  expected: $expectedRun`n  actual:   $actual" }
    }
    $approved = Get-AppValue $approvedKey
    if ($null -ne $approved) { Fail "$Where`: StartupApproved\Run\$valueName exists ($(Format-Value $approved)); the installer must never write it" }
}

# ---- 0. preflight ----------------------------------------------------------------------------------------

Write-Host "0. Preflight (mode=$mode, setup=$SetupExe)"
if (-not (Test-Path $SetupExe)) { Fail "installer missing: $SetupExe (run tools/build-installer.ps1 -Version $Version first)" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "-Version must be X.Y.Z (got '$Version')" }
Stop-App
if (Test-Path $unins) {
    Write-Host '  previous install found - uninstalling silently'
    $p = Start-Process -FilePath $unins -ArgumentList $silentArgs -Wait -PassThru
    if (-not (Wait-ExeGone 30)) { Fail "pre-existing install could not be removed (unins000.exe exit $($p.ExitCode))" }
}
Remove-AppValue $runKey
Remove-AppValue $approvedKey
if ($null -ne (Get-AppValue $runKey)) { Fail 'pre-clean could not remove the Run value' }
if ($null -ne (Get-AppValue $approvedKey)) { Fail 'pre-clean could not remove the StartupApproved value' }
# T-01-17: the recursive delete is limited to the app's own folder built from a literal segment.
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or -not $data.EndsWith('\BingWallpaperUpdater')) { Fail "refusing to delete '$data' - not the app data folder" }
if (Test-Path $data) { Remove-Item -Path $data -Recurse -Force }
if (Test-Path $data) { Fail "could not remove $data" }
if (Test-Path $installDir) { Fail "install dir still present after the pre-clean: $installDir" }

# ---- 1. fresh install ------------------------------------------------------------------------------------

Write-Host '1. Fresh silent install'
# Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="<setup-fresh.log>" [/LANG=vi /MERGETASKS=!autostart]
$extra = @()
if ($NoAutostartTask) { $extra = @('/LANG=vi', '/MERGETASKS=!autostart') }
$code = Invoke-Setup 'setup-fresh.log' $extra
if ($code -ne 0) { Fail "fresh install exited with code $code" }

# ---- 2. assert the install -------------------------------------------------------------------------------

Write-Host '2. Assert files, uninstall key, shortcut, Run value, seeded settings'
foreach ($f in @($exe, (Join-Path $installDir 'hostfxr.dll'), (Join-Path $installDir 'vi\BingWallpaperUpdater.resources.dll'), $unins)) {
    if (-not (Test-Path $f)) { Fail "missing after install: $f" }
}
$pdbs = @(Get-ChildItem -Path $installDir -Filter *.pdb -Recurse -File -ErrorAction SilentlyContinue)
if ($pdbs.Count -gt 0) { Fail "$($pdbs.Count) .pdb file(s) were installed" }
if (-not (Test-Path $uninstallKey)) { Fail "uninstall key missing: $uninstallKey" }
$displayVersion = Get-NamedValue $uninstallKey 'DisplayVersion'
if (-not ($displayVersion -eq $Version)) { Fail "DisplayVersion is '$(Format-Value $displayVersion)', expected '$Version'" }
if (-not (Test-Path $shortcut)) { Fail "Start-menu shortcut missing: $shortcut" }
Assert-RunValue 'after install'
if (-not (Test-Path $settingsPath)) { Fail "seeded settings.json missing: $settingsPath" }
$settingsRaw = Get-Content $settingsPath -Raw
try { $seed = $settingsRaw | ConvertFrom-Json } catch { Fail "settings.json does not parse: $($_.Exception.Message)" }
$expectedLang = if ($NoAutostartTask) { 'vi' } else { 'auto' }
$expectedAuto = -not $NoAutostartTask
if ($seed.schemaVersion -ne 1) { Fail "seed schemaVersion=$($seed.schemaVersion)" }
if ($seed.market -ne 'en-US') { Fail "seed market=$($seed.market)" }
if ($seed.resolution -ne 'UHD') { Fail "seed resolution=$($seed.resolution)" }
if ($seed.intervalMinutes -ne 30) { Fail "seed intervalMinutes=$($seed.intervalMinutes)" }
if ($seed.mode -ne 'newest') { Fail "seed mode=$($seed.mode)" }
if ($seed.language -ne $expectedLang) { Fail "seed language=$($seed.language), expected $expectedLang" }
if ($seed.monitorMode -ne 'same') { Fail "seed monitorMode=$($seed.monitorMode)" }
if ($seed.autostart -ne $expectedAuto) { Fail "seed autostart=$($seed.autostart), expected $expectedAuto" }
Write-Host "  seed OK: autostart=$($seed.autostart) language=$($seed.language)"

# ---- 3. first launch -------------------------------------------------------------------------------------

Write-Host '3. First launch: startup version, apply ok, tick done, no window, no child, allowed hosts'
$before = @(Read-Log).Count
$launchAt = Get-Date
$proc = Start-Process -FilePath $exe -PassThru
$script:launched = $true
$line = Wait-LogLine -Pattern ([regex]::Escape(" INFO startup version=$Version ")) -SkipLines $before -Timeout 30 -Process $proc
if (-not $line) { Fail "no ' INFO startup version=$Version ' line within 30 s (A6: does -p:Version reach the assembly?)" }
Write-Host "  $line"
$line = Wait-LogLine -Pattern ' INFO apply ok ' -SkipLines $before -Timeout $TimeoutSec -Process $proc
if (-not $line) { Fail "no ' INFO apply ok ' line within $TimeoutSec s" }
$line = Wait-LogLine -Pattern ' INFO tick done reason=Startup ' -SkipLines $before -Timeout $TimeoutSec -Process $proc
if (-not $line) { Fail "no ' INFO tick done reason=Startup ' line within $TimeoutSec s" }
$tickWithin = [int][math]::Ceiling(((Get-Date) - $launchAt).TotalSeconds)
Write-Host "  tick done within $tickWithin s"
$proc.Refresh()
if ($proc.HasExited) { Fail "the app exited after the first tick with code $($proc.ExitCode)" }
if ($proc.MainWindowHandle -ne 0) { Fail "the app opened a window on first launch (MainWindowHandle=$($proc.MainWindowHandle))" }
$children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($proc.Id)" -ErrorAction SilentlyContinue)
if ($children.Count -gt 0) { Fail "the app spawned child process(es): $(($children | ForEach-Object { $_.Name }) -join ', ')" }
$fresh = @(Read-Log | Select-Object -Skip $before)
foreach ($l in $fresh) {
    if ($l -match '^\S+ INFO http (\S+) ') {
        $h = $Matches[1]
        if ($allowedHosts -notcontains $h) { Fail "host outside the allow-list was contacted: $l" }
    }
}
if ($NoAutostartTask) {
    if (@($fresh | Where-Object { $_ -like '*autostart run value written*' }).Count -gt 0) { Fail 'the app wrote the Run value although the task was unticked (seed ignored?)' }
    if (@($fresh | Where-Object { $_ -like '* INFO ui language setting=vi *' }).Count -eq 0) { Fail "no ' INFO ui language setting=vi ' line (installer language did not seed language=vi)" }
}
Assert-RunValue 'after first launch'

# ---- 4. upgrade over the running instance ----------------------------------------------------------------

Write-Host '4. Silent upgrade over the running instance'
$settingsBefore = Get-Content $settingsPath -Raw
$jpgsBefore = Get-JpgCount
$runBefore = Get-AppValue $runKey
$skipUpgrade = @(Read-Log).Count
# Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="<setup-upgrade.log>" [/MERGETASKS=!autostart] - the task is
# hidden on upgrade, so the deselect must not remove an existing Run value.
$extra = @()
if (-not $NoAutostartTask) { $extra = @('/MERGETASKS=!autostart') }
$code = Invoke-Setup 'setup-upgrade.log' $extra
if ($code -ne 0) { Fail "upgrade install exited with code $code" }
$line = Wait-LogLine -Pattern ([regex]::Escape(' INFO shutdown reason=exit-signal')) -SkipLines $skipUpgrade -Timeout 15
if (-not $line) { Fail "no ' INFO shutdown reason=exit-signal' line within 15 s of the upgrade" }
Write-Host "  $line"
if (-not $proc.WaitForExit(15000)) { Fail 'the pre-upgrade process is still alive 15 s after the exit signal' }
if (-not (Test-Path $exe)) { Fail "exe missing after the upgrade: $exe" }
$displayVersion = Get-NamedValue $uninstallKey 'DisplayVersion'
if (-not ($displayVersion -eq $Version)) { Fail "DisplayVersion after upgrade is '$(Format-Value $displayVersion)'" }
$settingsAfter = Get-Content $settingsPath -Raw
if (-not ($settingsAfter -ceq $settingsBefore)) { Fail "settings.json changed across the upgrade`n  before: $settingsBefore`n  after:  $settingsAfter" }
$jpgsAfter = Get-JpgCount
if ($jpgsAfter -ne $jpgsBefore) { Fail "cached jpg count changed across the upgrade: $jpgsBefore -> $jpgsAfter" }
$runAfter = Get-AppValue $runKey
if ($null -eq $runBefore) {
    if ($null -ne $runAfter) { Fail "the upgrade created a Run value: $(Format-Value $runAfter)" }
} else {
    if ($null -eq $runAfter -or -not ($runAfter -ceq $runBefore)) { Fail "the upgrade changed the Run value`n  before: $runBefore`n  after:  $(Format-Value $runAfter)" }
}
Assert-RunValue 'after upgrade'
if (@(Get-AppProcesses).Count -gt 0) { Fail 'an app process is running after the silent upgrade (skipifsilent should suppress the launch)' }
Write-Host "  settings byte-identical, $jpgsAfter jpg kept, Run value unchanged, no process running"

# ---- 5. relaunch -----------------------------------------------------------------------------------------

Write-Host '5. Relaunch the upgraded build'
$skipRelaunch = @(Read-Log).Count
$proc = Start-Process -FilePath $exe -PassThru
$line = Wait-LogLine -Pattern ' INFO startup version=' -SkipLines $skipRelaunch -Timeout 30 -Process $proc
if (-not $line) { Fail "no fresh ' INFO startup version=' line within 30 s of the relaunch" }
$line = Wait-LogLine -Pattern ' INFO schedule start ' -SkipLines $skipRelaunch -Timeout 60 -Process $proc
if (-not $line) { Fail "no ' INFO schedule start ' line within 60 s of the relaunch" }

# ---- 6. uninstall while running --------------------------------------------------------------------------

Write-Host '6. Silent uninstall while the app is running'
$skipUninstall = @(Read-Log).Count
# unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART - UninstallSilent skips the delete-data prompt, so data is kept.
$p = Start-Process -FilePath $unins -ArgumentList $silentArgs -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "unins000.exe exited with code $($p.ExitCode)" }
if (-not (Wait-ExeGone 30)) { Fail "exe still present 30 s after the uninstall: $exe" }
$line = Wait-LogLine -Pattern ([regex]::Escape(' INFO shutdown reason=exit-signal')) -SkipLines $skipUninstall -Timeout 15
if (-not $line) { Fail "no second ' INFO shutdown reason=exit-signal' line after the uninstall" }
Write-Host "  $line"
if (-not $proc.WaitForExit(15000)) { Fail 'the process is still alive 15 s after the uninstall exit signal' }
if (@(Get-AppProcesses).Count -gt 0) { Fail 'an app process is still running after the uninstall' }
$script:launched = $false
if (Test-Path $unins) { Fail "unins000.exe still present: $unins" }
if (Test-Path $installDir) {
    $left = @(Get-ChildItem -Path $installDir -Recurse -File -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) { Fail "install dir still holds $($left.Count) file(s): $(($left | Select-Object -First 5 | ForEach-Object { $_.Name }) -join ', ')" }
}
if (Test-Path $uninstallKey) { Fail "uninstall key still present: $uninstallKey" }
if (Test-Path $shortcut) { Fail "Start-menu shortcut still present: $shortcut" }
$runLeft = Get-AppValue $runKey
if ($null -ne $runLeft) { Fail "Run value still present after the uninstall: $(Format-Value $runLeft)" }
$approvedLeft = Get-AppValue $approvedKey
if ($null -ne $approvedLeft) { Fail "StartupApproved value still present after the uninstall: $(Format-Value $approvedLeft)" }
if (-not (Test-Path $settingsPath)) { Fail 'silent uninstall deleted settings.json (data must be kept without the prompt)' }
Write-Host '  install dir, uninstall key, shortcut, Run and StartupApproved values gone; data folder kept'

# ---- 7. done ---------------------------------------------------------------------------------------------

Write-Host "INSTALL PROBE OK version=$Version mode=$mode installDir=$installDir tickWithin=$tickWithin"
exit 0
