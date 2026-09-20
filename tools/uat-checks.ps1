$ErrorActionPreference = 'Continue'
$exe = 'D:\AppDev\BingWallpaperUpdater\publish\BingWallpaperUpdater.exe'
$data = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater'
$log = Join-Path $data 'log.txt'

function Snap {
    $d = Get-ItemProperty 'HKCU:\Control Panel\Desktop'
    $t = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue
    $c = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -ErrorAction SilentlyContinue
    $ls = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lock Screen' -ErrorAction SilentlyContinue
    $dwm = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\DWM' -ErrorAction SilentlyContinue
    [ordered]@{
        Wallpaper      = $d.Wallpaper
        WallpaperStyle = $d.WallpaperStyle
        TileWallpaper  = $d.TileWallpaper
        AppsUseLight   = $t.AppsUseLightTheme
        SystemUsesLight= $t.SystemUsesLightTheme
        AccentPrefix   = $dwm.ColorPrevalence
        AccentColor    = $dwm.AccentColor
        Spotlight      = $c.RotatingLockScreenEnabled
        LockScreenAuto = $ls.SlideshowEnabled
        CurrentTheme   = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes').CurrentTheme
    }
}

Write-Host "=== Item 2: Task Manager placement"
Start-Process $exe | Out-Null
Start-Sleep -Seconds 8
$p = @(Get-Process BingWallpaperUpdater -ErrorAction SilentlyContinue)
Write-Host ("processes={0} mainWindowHandle={1} ws_MB={2}" -f $p.Count, ($p | ForEach-Object { $_.MainWindowHandle }), ($p | ForEach-Object { [math]::Round($_.WorkingSet64/1MB,1) }))
$vis = Get-Process | Where-Object { $_.MainWindowTitle -and $_.ProcessName -eq 'BingWallpaperUpdater' }
Write-Host ("visible-window-count={0}" -f @($vis).Count)
Write-Host ("ITEM2 {0}" -f $(if ($p.Count -eq 1 -and $p[0].MainWindowHandle -eq 0 -and @($vis).Count -eq 0) { 'PASS' } else { 'FAIL' }))

Write-Host "=== Item 6: personalization unchanged (snapshot before/after a fresh run)"
$before = Snap
Stop-Process -Name BingWallpaperUpdater -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Start-Process $exe | Out-Null
Start-Sleep -Seconds 10
$after = Snap
$diff = @()
foreach ($k in $before.Keys) { if ("$($before[$k])" -ne "$($after[$k])") { $diff += "$k : '$($before[$k])' -> '$($after[$k])'" } }
Write-Host ("wallpaper={0} style={1} tile={2}" -f $after.Wallpaper, $after.WallpaperStyle, $after.TileWallpaper)
if ($diff.Count) { $diff | ForEach-Object { Write-Host "  changed: $_" } } else { Write-Host "  no personalization key changed" }
$regHits = Select-String -Path 'D:\AppDev\BingWallpaperUpdater\src\BingWallpaperUpdater.App\*.cs','D:\AppDev\BingWallpaperUpdater\src\BingWallpaperUpdater.Windows\Wallpaper\*.cs' -Pattern 'Registry\.' | ForEach-Object { "$($_.Filename):$($_.LineNumber)" }
Write-Host ("registry writes in source: {0}" -f ($regHits -join ', '))
Write-Host ("ITEM6 {0}" -f $(if ($diff.Count -eq 0) { 'PASS' } else { 'FAIL' }))

Write-Host "=== Item 3: Explorer restart"
$wpBefore = (Get-ItemProperty 'HKCU:\Control Panel\Desktop').Wallpaper
$sb = New-Object System.Text.StringBuilder 1024
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern bool SystemParametersInfo(uint a, uint b, System.Text.StringBuilder c, uint d);'
[W.U]::SystemParametersInfo(0x73, 1024, $sb, 0) | Out-Null
$spiBefore = $sb.ToString()
Stop-Process -Name explorer -Force
Start-Sleep -Seconds 3
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
Start-Sleep -Seconds 10
$sb2 = New-Object System.Text.StringBuilder 1024
[W.U]::SystemParametersInfo(0x73, 1024, $sb2, 0) | Out-Null
$spiAfter = $sb2.ToString()
$p2 = @(Get-Process BingWallpaperUpdater -ErrorAction SilentlyContinue)
$taskbar = Get-Content $log -Tail 30 | Select-String -Pattern 'TaskbarCreated|tray recreated|taskbar' | Select-Object -Last 3
Write-Host ("spi before={0}" -f $spiBefore)
Write-Host ("spi after ={0}" -f $spiAfter)
Write-Host ("app alive={0} explorer alive={1}" -f ($p2.Count -eq 1), [bool](Get-Process explorer -ErrorAction SilentlyContinue))
if ($taskbar) { $taskbar | ForEach-Object { Write-Host "  log: $($_.Line)" } } else { Write-Host "  log: no TaskbarCreated line found (icon re-registration not logged)" }
Write-Host ("ITEM3 {0}" -f $(if ($spiBefore -eq $spiAfter -and $spiAfter -eq $wpBefore -and $p2.Count -eq 1) { 'PASS (wallpaper+process; icon visibility is visual)' } else { 'FAIL' }))

Write-Host "=== Item 5 (indirect): Exit route evidence"
$exitLines = Select-String -Path $log -Pattern 'shutdown reason=' | Select-Object -Last 3
if ($exitLines) { $exitLines | ForEach-Object { Write-Host "  $($_.Line)" } } else { Write-Host "  no shutdown line in log.txt yet" }
Write-Host "=== Item 4 (indirect): persistence keys"
Write-Host ("HKCU Wallpaper = {0}" -f (Get-ItemProperty 'HKCU:\Control Panel\Desktop').Wallpaper)
Write-Host ("Transcoded exists = {0}" -f (Test-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Themes\TranscodedWallpaper')))
Write-Host ("cache file exists = {0}" -f (Test-Path (Get-ItemProperty 'HKCU:\Control Panel\Desktop').Wallpaper))
