; Ghast — MC Connection Booster · Inno Setup 6 script
; Compile on Windows with the Inno Setup Compiler (https://jrsoftware.org/isinfo.php):
;
;   1) dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true ^
;        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
;   2) "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Ghast.iss
;
; Output: dist\Ghast-Setup.exe (relative to the project folder).
; Nothing is downloaded — the installer only bundles the already-published Ghast.exe.

#define MyAppName "Ghast"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Ghast"
#define MyAppExeName "Ghast.exe"
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; Fixed GUID so upgrades replace the same Apps & Features entry. Never change it.
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
OutputDir=..\dist
OutputBaseFilename=Ghast-Setup
SetupIconFile=..\Assets\ghast.ico
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
; Remove the per-user "start with Windows" entry so uninstall leaves no launcher behind.
Filename: "reg"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v Ghast /f"; \
    Flags: runhidden; RunOnceId: "RemoveGhastRunKey"

; NOTE: %AppData%\Ghast (config, presets and — importantly — backup.json with the
; machine's pre-Ghast values) is deliberately KEPT on uninstall, so "Restore Defaults"
; is still possible after a reinstall. Delete that folder manually for a full wipe.
