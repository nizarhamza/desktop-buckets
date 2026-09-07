; Inno Setup script for Desktop Buckets
; Build:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\DesktopBuckets.iss
; Produces: installer\Output\DesktopBuckets-Setup-<version>.exe
;
; Per-user install (no admin / no UAC). Registers in Settings > Apps so the user
; can uninstall it from there like any other program.

#define MyAppName "Desktop Buckets"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Desktop Buckets"
#define MyAppURL "https://github.com/nizarhamza/desktop-buckets"
#define MyAppExeName "DesktopBuckets.exe"
#define PublishDir "..\publish"

[Setup]
; A stable AppId ties upgrades and the uninstaller together. Do not change it.
AppId={{6B0B8E6E-6D2C-4C5E-9C3E-6E1B4C2A9D71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\DesktopBuckets\Resources\app.ico

; No elevation: install into the user profile, uninstall entry under HKCU.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Close / relaunch handling for a running instance (mutex set by the app).
AppMutex=DesktopBuckets.SingleInstance.v1,Global\DesktopBuckets.SingleInstance.v1
CloseApplications=yes
RestartApplications=no

OutputDir=Output
OutputBaseFilename=DesktopBuckets-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start {#MyAppName} automatically when I sign in"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The self-contained publish folder, which also carries the shell-extension
; payload when it was built: DesktopBuckets.ShellExt.dll, DesktopBuckets.Package.msix,
; DesktopBuckets.cer. The app enables/disables it on demand (one elevation prompt).
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*";               DestDir: "{app}"; Excludes: "{#MyAppExeName}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";            Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}";  Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";      Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Launch at sign-in (per-user Run key), removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "DesktopBuckets"; ValueData: """{app}\{#MyAppExeName}"""; \
  Tasks: autostart; Flags: uninsdeletevalue

; Legacy fallback verbs (used only on builds without the MSIX shell package).
; Removed on uninstall whether or not they were ever created.
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\DesktopBuckets.NewBucket"; \
  Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\shell\DesktopBuckets.NewBucket"; \
  Flags: dontcreatekey uninsdeletekey

[Run]
; Interactive install: offer to launch on the finished page.
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent
; Silent install (the in-app updater path): relaunch automatically.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runhidden skipifnotsilent

[UninstallRun]
; Tear down shell integration first: removes the MSIX shell package (per-user,
; no elevation) or the legacy verbs, depending on what this build shipped.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister-shell"; \
  Flags: runhidden skipifdoesntexist waituntilterminated; RunOnceId: "UnregisterShell"

[Code]
procedure KillRunning;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}',
       '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

function InitializeSetup(): Boolean;
begin
  KillRunning;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  dataDir: String;
begin
  if CurUninstallStep = usUninstall then
    KillRunning;

  if CurUninstallStep = usPostUninstall then
  begin
    dataDir := ExpandConstant('{userappdata}\DesktopBuckets');
    if DirExists(dataDir) then
    begin
      if MsgBox('Also remove Desktop Buckets settings and the diagnostic log?' + #13#10 +
                'Your bucket folders and the files inside them are never deleted.',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(dataDir, True, True, True);
    end;
  end;
end;
