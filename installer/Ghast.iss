; Ghast — MC Connection Booster · Inno Setup 6 script
; Built in CI (.github/workflows/build-installer.yml) on windows-latest:
;
;   dotnet publish Ghast/Ghast.csproj -c Release -r win-x64 -p:PublishSingleFile=true ^
;     --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
;   ISCC.exe installer\Ghast.iss
;
; Output: installer\Output\Ghast-Setup.exe. Nothing is downloaded — the installer
; only bundles the already-published Ghast.exe.
;
; Upgrade behaviour: the fixed AppId below lets this installer find a previous
; Ghast install, back up %AppData%\Ghast (settings, presets AND backup.json with
; the machine's pre-Ghast values), silently uninstall the old version, install
; clean, then restore the user data. Never change the AppId.

#define MyAppName "Ghast"
#define MyAppVersion "3.0.0"
#define MyAppPublisher "Ghast"
#define MyAppExeName "Ghast.exe"
#define PublishDir "..\publish"

[Setup]
; Fixed GUID — identical in every build since v1 so upgrades replace in place. Keep forever.
AppId={{C7E3F9D4-52A1-4B7E-9C0D-8AB1F3E6A2D5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The app writes HKLM / netsh, so both install and app run elevated.
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=Ghast-Setup
SetupIconFile=..\Ghast\Assets\ghast.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} — MC Connection Booster
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Desktop shortcut: checkbox, default ON (no 'unchecked' flag).
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Installer already runs elevated, so the launched app inherits admin — no extra UAC prompt here.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the per-user "start with Windows" entry so a real uninstall leaves no dangling launcher.
; (%AppData%\Ghast itself is deliberately never touched — see note below.)
Filename: "reg"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v Ghast /f"; \
    Flags: runhidden; RunOnceId: "RemoveGhastRunKey"

; NOTE: there is intentionally NO [UninstallDelete] for {userappdata}\Ghast — config,
; presets and backup.json (the machine's pre-Ghast values) survive uninstall, so
; "Restore Defaults" still works after a reinstall. Delete the folder manually for a wipe.

[Code]
// Normalizes the AppId for registry lookups: the [Setup] value uses '{{' to escape
// a literal '{', so SetupSetting returns '{{GUID}' — the Uninstall key uses '{GUID}'.
function AppIdForRegistry: String;
begin
  Result := '{#SetupSetting("AppId")}';
  if (Length(Result) >= 2) and (Copy(Result, 1, 2) = '{{') then
    Delete(Result, 1, 1);
end;

function GetUninstallString: String;
var
  sUnInstPath, sUnInstall: String;
begin
  sUnInstPath := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdForRegistry + '_is1';
  sUnInstall := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstall) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstall);
  Result := sUnInstall;
end;

procedure BackupUserData;   // settings + presets + backup.json
var
  Src, Dst: String;
  ResultCode: Integer;
begin
  Src := ExpandConstant('{userappdata}\Ghast');
  Dst := ExpandConstant('{tmp}\GhastUserDataBackup');
  if DirExists(Src) then
  begin
    ForceDirectories(Dst);
    Exec('cmd.exe', '/C xcopy "' + Src + '" "' + Dst + '" /E /I /Y /Q /H',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure RestoreUserData;
var
  Src, Dst: String;
  ResultCode: Integer;
begin
  Src := ExpandConstant('{tmp}\GhastUserDataBackup');
  Dst := ExpandConstant('{userappdata}\Ghast');
  if DirExists(Src) then
  begin
    ForceDirectories(Dst);
    Exec('cmd.exe', '/C xcopy "' + Src + '" "' + Dst + '" /E /I /Y /Q /H',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  sUnInstall: String;
  iResult, iWait: Integer;
begin
  if CurStep = ssInstall then
  begin
    BackupUserData;                             // 1. save settings + presets first
    sUnInstall := RemoveQuotes(GetUninstallString);
    if sUnInstall <> '' then
    begin
      // 2. silently uninstall the old version.
      Exec(sUnInstall, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
        '', SW_HIDE, ewWaitUntilTerminated, iResult);
      // The uninstaller hands off to a temp copy and returns early; it deletes
      // unins###.exe as its last act, so poll for that (max ~15s) before installing
      // over the top.
      iWait := 0;
      while FileExists(sUnInstall) and (iWait < 30) do
      begin
        Sleep(500);
        iWait := iWait + 1;
      end;
    end;
  end;
  if CurStep = ssPostInstall then
    RestoreUserData;                            // 3. put settings + presets back
end;
