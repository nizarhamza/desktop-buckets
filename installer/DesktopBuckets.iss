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
; Also the name of the uninstall registry key (with "_is1" appended).
#define MyAppIdGuid "{6B0B8E6E-6D2C-4C5E-9C3E-6E1B4C2A9D71}"
#define MyAppPublisher "Desktop Buckets"
#define MyAppURL "https://github.com/nizarhamza/desktop-buckets"
#define MyAppExeName "DesktopBuckets.exe"
#define PublishDir "..\publish"

[Setup]
; A stable AppId ties upgrades and the uninstaller together. Do not change it.
AppId={{#MyAppIdGuid}
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

; Close / relaunch handling for a running instance (mutex set by the app; the
; Local\ name is current, the Global\ one is what builds before 0.3.0 created).
AppMutex=Local\DesktopBuckets.SingleInstance.v1,Global\DesktopBuckets.SingleInstance.v1
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
Source: "{#PublishDir}\*";               DestDir: "{app}"; Excludes: "{#MyAppExeName},*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

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
// The app's single-instance mutex names (see Services\SingleInstance.cs).
const
  AppMutexes = '{#SetupSetting("AppMutex")}';

function AppIsRunning: Boolean;
begin
  Result := CheckForMutexes(AppMutexes);
end;

// Where the currently installed exe lives. {app} isn't known yet in
// InitializeSetup, so read the previous install's location from its uninstall
// key, falling back to the default directory.
function InstalledExePath: String;
var
  loc: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppIdGuid}_is1',
                         'InstallLocation', loc) and (loc <> '') then
    Result := AddBackslash(loc) + '{#MyAppExeName}'
  else
    Result := ExpandConstant('{autopf}\{#MyAppName}\{#MyAppExeName}');
end;

// Ask a running instance to exit on its own terms (it flushes .bucket.json files
// and slides displaced desktop icons home), wait for it, and only fall back to a
// hard kill if it hasn't gone after 20 s. A forced kill mid-save is how pins and
// tile positions used to get lost.
procedure StopRunningApp(exe: String);
var
  rc, i: Integer;
begin
  if not AppIsRunning then
    exit;

  if FileExists(exe) then
  begin
    Log('Asking the running app to quit: ' + exe);
    Exec(exe, '--quit', '', SW_HIDE, ewNoWait, rc);
  end;

  for i := 1 to 200 do
  begin
    if not AppIsRunning then
    begin
      Log('App exited on request.');
      exit;
    end;
    Sleep(100);
  end;

  Log('App did not exit on request; forcing.');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}',
       '', SW_HIDE, ewWaitUntilTerminated, rc);

  // taskkill returns when termination is requested, not when the process is
  // gone; the mutex lives until it is. Inno's AppMutex check follows right
  // after this, so wait for the handle to actually disappear.
  for i := 1 to 100 do
  begin
    if not AppIsRunning then
    begin
      Log('App terminated.');
      exit;
    end;
    Sleep(100);
  end;
  Log('Mutex still present after forced kill.');
end;

// Inno's own AppMutex check runs BEFORE PrepareToInstall, and in silent mode its
// "application is running" box defaults to Cancel — so the running instance has
// to be gone by the end of InitializeSetup. Silent (the in-app updater path): ask
// it to quit and wait. Interactive: ask the user first, so merely opening the
// installer and cancelling never closes the app.
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not AppIsRunning then
    exit;

  if not WizardSilent then
  begin
    if MsgBox('{#MyAppName} is running. Close it and continue with the installation?',
              mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := False;
      exit;
    end;
  end;

  StopRunningApp(InstalledExePath);
end;

// Belt and braces: if it was relaunched while the wizard was open.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp(ExpandConstant('{app}\{#MyAppExeName}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp(ExpandConstant('{app}\{#MyAppExeName}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  root, dataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // App state (buckets.json, update.json, log.txt) lives in a hidden .app folder
    // under the bucket root. Only that folder is ever removed; the root itself holds
    // the user's buckets and is never touched.
    root := ExpandConstant('{%USERPROFILE}\Desktop Buckets');
    dataDir := root + '\.app';
    if DirExists(dataDir) then
    begin
      if MsgBox('Also remove Desktop Buckets settings and the diagnostic log?' + #13#10 +
                'Your bucket folders and the files inside them are never deleted.',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(dataDir, True, True, True);
    end;
  end;
end;
