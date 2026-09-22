; Bing Wallpaper Updater - per-user Inno Setup script (Phase 4, Plan 02; INST-01, INST-03, INST-04, INST-05, L10N-04, NFR-02).
;
; Installs the win-x64 self-contained folder publish for the current user only: no UAC prompt (lowest privileges,
; no override directive), everything under %LocalAppData%\Programs\BingWallpaperUpdater,
; HKCU-only registry (the Inno uninstall key and, when the task is ticked, the Run value), a Start-menu shortcut,
; English + Vietnamese UI. A running instance is closed deterministically through the app's session-local
; Local\BingWallpaperUpdater.Exit event (see RequestAppExit) before Setup or the uninstaller touches the folder;
; AppMutex and CloseApplications stay as backstops. Fresh installs seed a complete settings.json (autostart from the
; task state, language from the installer language); upgrades never touch it. Uninstall removes the Run value and any
; Task Manager StartupApproved value unconditionally and, interactively only, offers to delete the data folder.
;
; Compile: ISCC.exe /Qp "/DAppVersion=X.Y.Z" "/DPublishDir=<abs publish dir>" "/O<abs out dir>" installer\setup.iss
; (tools/build-installer.ps1 does exactly that). Output: <out dir>\BingWallpaperUpdater-X.Y.Z-x64-Setup.exe.
; Silent flags used by tools/install-probe.ps1: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/LOG="file"] [/LANG=vi]
; [/MERGETASKS=!autostart]; uninstall: unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART (data is kept).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#define AppName "Bing Wallpaper Updater"
#define AppExe "BingWallpaperUpdater.exe"

[Setup]
; Fixed for the life of the product: the uninstall key becomes HKCU\...\Uninstall\{4AD7C4B2-6A1C-443D-95F9-74E5239D058C}_is1
; and IsUpgrade below keys on it. The doubled brace is the Inno escape for a literal "{".
AppId={{4AD7C4B2-6A1C-443D-95F9-74E5239D058C}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=anhyeuviolet
AppPublisherURL=https://github.com/anhyeuviolet/BingWallpaperUpdater
AppSupportURL=https://github.com/anhyeuviolet/BingWallpaperUpdater/issues
AppUpdatesURL=https://github.com/anhyeuviolet/BingWallpaperUpdater/releases
DefaultDirName={localappdata}\Programs\BingWallpaperUpdater
DisableDirPage=yes
DisableProgramGroupPage=yes
; Never elevates, even for an administrator; the overrides-allowed directive is deliberately absent so neither
; /ALLUSERS nor a dialog can switch to a per-machine install (CLAUDE.md Permissions row).
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 floor (NFR-02); Inno refuses older builds with its own localized message.
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=BingWallpaperUpdater-{#AppVersion}-x64-Setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
SetupIconFile=..\src\BingWallpaperUpdater.App\Resources\tray.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; Backstops for a running instance. AppMutex only checks (it never closes anything); RequestAppExit in
; InitializeSetup / usAppMutexCheck runs first and is what actually ends the app. CloseApplications uses Restart
; Manager on the [Files] set at the Preparing to Install page (Setup only); the app does not register for restart.
AppMutex=Local\BingWallpaperUpdater
CloseApplications=yes
RestartApplications=no
; Always show the picker so both languages are visible (L10N-04); UsePreviousLanguage (default yes) keeps the choice on upgrade.
ShowLanguageDialog=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "vi"; MessagesFile: "Languages\Vietnamese.isl"

[CustomMessages]
en.AutostartTask=Start with Windows (recommended)
vi.AutostartTask=Khởi động cùng Windows (khuyến nghị)
en.LaunchApp=Launch Bing Wallpaper Updater
vi.LaunchApp=Chạy Bing Wallpaper Updater
en.ElevatedWarning=Setup is running with administrator rights. It will install for the account %1 only and cannot start the app when this installer closes. Do not run this installer as administrator. Continue anyway?
vi.ElevatedWarning=Trình cài đặt đang chạy với quyền quản trị viên. Ứng dụng sẽ chỉ được cài cho tài khoản %1 và không thể tự chạy khi trình cài đặt đóng. Không nên chạy trình cài đặt với quyền quản trị viên. Vẫn tiếp tục?
en.DeleteData=Also delete the downloaded wallpapers and settings in %1?%n%nYour current desktop wallpaper stays until you change it.
vi.DeleteData=Xóa luôn ảnh nền đã tải và cài đặt trong %1?%n%nHình nền hiện tại vẫn được giữ cho đến khi bạn đổi.

[Tasks]
; Checked by default (no "unchecked" flag); hidden on upgrade so /MERGETASKS=!autostart cannot remove an existing
; Run value and the app keeps owning the state (RESEARCH Pitfall 2).
Name: "autostart"; Description: "{cm:AutostartTask}"; Check: not IsUpgrade

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"

[Registry]
; Byte-identical to AutostartCommand.For: the quoted absolute exe path, a space, --startup (T-04-10). The value name
; matches RegistryAutostartManager.ValueName. StartupApproved is never written here (only the Settings checkbox may).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BingWallpaperUpdater"; ValueData: """{app}\{#AppExe}"" --startup"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; runasoriginaluser has no effect when Setup itself was started elevated, so the launch is suppressed there (Pitfall 3).
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait runasoriginaluser skipifsilent; Check: not IsAdmin

[Code]
function OpenEventW(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL; external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL; external 'CloseHandle@kernel32.dll stdcall';

const
  EVENT_MODIFY_STATE = $0002;
  // Byte-identical to Program.MutexName / Program.ExitEventName (session-local kernel objects).
  AppMutexName  = 'Local\BingWallpaperUpdater';
  ExitEventName = 'Local\BingWallpaperUpdater.Exit';
  UninstallKey  = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4AD7C4B2-6A1C-443D-95F9-74E5239D058C}_is1';
  RunKey        = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey   = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  RunValueName  = 'BingWallpaperUpdater';

var
  // Snapshot of "a previous install of this AppId is registered for the current user", taken in InitializeSetup.
  // Setup writes the uninstall key during the install step, before ssPostInstall, so a live RegKeyExists there
  // would say "upgrade" on a fresh install too; every reader below uses this pre-install value instead.
  WasUpgrade: Boolean;

// True when a previous install of this AppId was registered when Setup started (see WasUpgrade).
function IsUpgrade: Boolean;
begin
  Result := WasUpgrade;
end;

// Asks a running instance to exit through its Shutdown funnel (log: "shutdown reason=exit-signal") and waits up to
// 10 s for the mutex to disappear. True when no instance is (or remains) running. Used by both Setup and Uninstall.
function RequestAppExit: Boolean;
var
  H: THandle;
  I: Integer;
begin
  Result := not CheckForMutexes(AppMutexName);
  if Result then
    Exit;
  H := OpenEventW(EVENT_MODIFY_STATE, False, ExitEventName);
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandle(H);
    for I := 1 to 100 do
    begin
      Sleep(100);
      if not CheckForMutexes(AppMutexName) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// Ordering fact 1: Setup calls InitializeSetup before its own AppMutex check (issrc Setup.MainFunc.pas), so a running
// instance is asked to exit here and the "is currently running" prompt only appears if it did not comply in 10 s.
// It also runs before the [Tasks] Check functions are evaluated, so the upgrade snapshot taken here is what
// "Check: not IsUpgrade" sees. The elevated warning defaults to Cancel on screen (MB_DEFBUTTON2) but answers OK
// under /SUPPRESSMSGBOXES: a plain MsgBox is never suppressed, which would block every silent install driven from
// an elevated shell (probe, CI).
function InitializeSetup(): Boolean;
begin
  WasUpgrade := RegKeyExists(HKCU, UninstallKey);
  Result := True;
  if IsAdmin then
    Result := SuppressibleMsgBox(FmtMessage(CustomMessage('ElevatedWarning'), [ExpandConstant('{username}')]), mbConfirmation, MB_OKCANCEL or MB_DEFBUTTON2, IDOK) = IDOK;
  if Result then
    RequestAppExit;
end;

// Fresh-install seed: the complete Settings defaults (camelCase, CRLF, no BOM) with autostart from the task and the
// language from the installer language (vi -> vi, else auto). Upgrades keep the existing file - the app owns it.
procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir, SettingsPath, Auto, Lang: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  DataDir := ExpandConstant('{localappdata}\BingWallpaperUpdater');
  SettingsPath := DataDir + '\settings.json';
  if FileExists(SettingsPath) then
    Exit;
  ForceDirectories(DataDir);
  // The task is hidden on upgrade; an upgrade whose data folder was wiped by hand inherits the observed Run value.
  // WasUpgrade (not a live registry read: the uninstall key already exists at this step) keeps a fresh install with
  // the task unticked from inheriting a stale Run value left by an earlier publish\ build.
  if WizardIsTaskSelected('autostart') or (WasUpgrade and RegValueExists(HKCU, RunKey, RunValueName)) then
    Auto := 'true'
  else
    Auto := 'false';
  if ExpandConstant('{language}') = 'vi' then
    Lang := 'vi'
  else
    Lang := 'auto';
  SaveStringToFile(SettingsPath,
    '{' + #13#10 +
    '  "schemaVersion": 1,' + #13#10 +
    '  "market": "en-US",' + #13#10 +
    '  "resolution": "UHD",' + #13#10 +
    '  "intervalMinutes": 30,' + #13#10 +
    '  "mode": "newest",' + #13#10 +
    '  "language": "' + Lang + '",' + #13#10 +
    '  "monitorMode": "same",' + #13#10 +
    '  "autostart": ' + Auto + #13#10 +
    '}' + #13#10, False);
end;

// Ordering fact 2: the uninstaller raises usAppMutexCheck before UninstLog.CheckMutexes (issrc Setup.Uninstall.pas),
// so the running instance is closed before the AppMutex prompt could appear. At usPostUninstall the Run value and any
// StartupApproved value are removed unconditionally (belt and braces over uninsdeletevalue) and, interactively only,
// the user is asked whether the data folder should go too; DelTree is never given anything but DataDir (T-04-09).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usAppMutexCheck then
    RequestAppExit;
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, RunKey, RunValueName);
    RegDeleteValue(HKCU, ApprovedKey, RunValueName);
    DataDir := ExpandConstant('{localappdata}\BingWallpaperUpdater');
    if not UninstallSilent then
      if MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
