Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$log = Join-Path $env:LOCALAPPDATA 'BingWallpaperUpdater\log.txt'
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$target = $null
foreach ($b in $buttons) { if ($b.Current.Name -like '*Bing Wallpaper*') { $target = $b; break } }
if (-not $target) {
    # overflow: open "Show Hidden Icons" chevron then search again
    foreach ($b in $buttons) { if ($b.Current.Name -like '*hidden icons*' -or $b.Current.Name -like '*Show Hidden*') { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; break } }
    $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $buttons) { if ($b.Current.Name -like '*Bing Wallpaper*') { $target = $b; break } }
}
if (-not $target) { Write-Host "tray icon not found via UIA"; exit 2 }
Write-Host ("found tray button: '{0}'" -f $target.Current.Name)
$r = $target.Current.BoundingRectangle
Add-Type -Namespace W -Name M -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,System.UIntPtr e);'
[W.M]::SetCursorPos([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) | Out-Null
Start-Sleep -Milliseconds 300
[W.M]::mouse_event(0x0008,0,0,0,[UIntPtr]::Zero); [W.M]::mouse_event(0x0010,0,0,0,[UIntPtr]::Zero)  # right down/up
Start-Sleep 1
$mi = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $mi)
$exit = $null
foreach ($i in $items) { if ($i.Current.Name -match '^(Exit|Thoát)$') { $exit = $i; break } }
if (-not $exit) { Write-Host ("menu items seen: {0}" -f (($items | ForEach-Object { $_.Current.Name }) -join ' | ')); exit 3 }
Write-Host ("clicking menu item '{0}'" -f $exit.Current.Name)
try { $exit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {
    $r2 = $exit.Current.BoundingRectangle
    [W.M]::SetCursorPos([int]($r2.X + $r2.Width/2), [int]($r2.Y + $r2.Height/2)) | Out-Null; Start-Sleep -Milliseconds 200
    [W.M]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); [W.M]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
}
Start-Sleep 3
$p = @(Get-Process BingWallpaperUpdater -ErrorAction SilentlyContinue)
Write-Host ("process count after exit = {0}" -f $p.Count)
Get-Content $log -Tail 3 | ForEach-Object { Write-Host "  log: $_" }
$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$ghost = $false; foreach ($b in $buttons) { if ($b.Current.Name -like '*Bing Wallpaper*') { $ghost = $true } }
Write-Host ("tray icon still present = {0}" -f $ghost)
Write-Host ("ITEM5 {0}" -f $(if ($p.Count -eq 0 -and -not $ghost -and (Select-String -Path $log -Pattern 'shutdown reason=exit' -Quiet)) { 'PASS' } else { 'CHECK' }))
