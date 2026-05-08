; ===========================================================================
;  WinGrid11 installer script (Inno Setup 6)
;
;  Build via build-installer.ps1 in the repo root:
;     pwsh .\build-installer.ps1
;
;  The build script publishes a self-contained .NET 8 build (no separate
;  .NET runtime install needed by end users) and passes PublishDir,
;  OutputDir and AppVersion in via /D defines. Defaults below let you
;  also build standalone from the Inno Setup IDE for quick iteration.
; ===========================================================================

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define MyAppName     "WinGrid11"
#define MyAppExeName  "WinGrid11.exe"
#define MyAppPublisher "WinGrid11"
#define MyAppURL      "https://github.com/jamesgreaves/WinGrid11"

[Setup]
; AppId is the stable identity of this product across versions. Do NOT
; change it: Inno Setup uses it to recognise an existing install and
; perform an in-place upgrade (preserves Start Menu entries, lets the
; uninstaller find old files, keeps the autostart Run-key entry pointing
; at the new exe location since AppId-keyed install dir stays the same).
AppId={{F2A1B5E0-3C9D-4A2E-9F1B-5D8E4C7A3B2D}}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

OutputDir={#OutputDir}
OutputBaseFilename=WinGrid11-{#AppVersion}-Setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern

; x64 only. The app pins x64 in the csproj and the publish runtime is win-x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Default to admin install (Program Files). User can downgrade to a
; per-user install via the elevation dialog if they don't have admin.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline

; If the app is running, ask the user via Restart Manager to close it
; before files are replaced. WinGrid11 doesn't keep open file handles
; that block writes, but this also avoids two instances after upgrade.
CloseApplications=yes
RestartApplications=no

; Self-contained .NET publish; no prerequisite installer needed.
UninstallDisplayName={#MyAppName} {#AppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Launch {#MyAppName} when Windows starts"; GroupDescription: "Additional options:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Per-user autostart. uninsdeletevalue keeps it tidy if the user
; uninstalls. The in-app "Launch on Windows startup" toggle reads/writes
; this same value so the two stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort kill so the uninstaller can remove the install dir without
; "files in use" errors. taskkill is part of every supported Windows.
Filename: "taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillRunningInstance"
