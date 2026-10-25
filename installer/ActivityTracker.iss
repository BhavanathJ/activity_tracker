; ActivityTracker.iss
; Inno Setup 6 script for Activity Tracker
;
; Build with:
;   iscc /DVersion=1.0.0 /DServiceDir=...\out\service /DAgentDir=...\out\agent /DCliDir=...\out\cli installer\ActivityTracker.iss
;
; Version, ServiceDir, AgentDir, CliDir must be passed as /D defines.

#ifndef Version
  #define Version "0.0.0-dev"
#endif
#ifndef ServiceDir
  #define ServiceDir "out\service"
#endif
#ifndef AgentDir
  #define AgentDir "out\agent"
#endif
#ifndef CliDir
  #define CliDir "out\cli"
#endif

[Setup]
AppId={{8F3A9C2E-5B71-4D0A-9E64-2C7B1A5D3F90}
AppName=Activity Tracker
AppVersion={#Version}
AppPublisher=Activity Tracker Project
DefaultDirName={autopf}\ActivityTracker
DefaultGroupName=Activity Tracker
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
SourceDir=..
OutputDir=installer\output
OutputBaseFilename=ActivityTracker-Setup-{#Version}
ChangesEnvironment=yes
UninstallDisplayIcon={app}\service\ActivityTracker.Service.exe
SetupIconFile=compiler:SetupClassicIcon.ico
WizardStyle=modern
LicenseFile=LICENSE
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
FinishedLabel=Setup has finished installing [name] on your computer.%n%nNext steps:%n1) open chrome://extensions or brave://extensions or edge://extensions%n2) turn on Developer mode%n3) Load unpacked and choose {app}\extension-<your browser>%n4) open a NEW terminal and run: tracker live

[Files]
; Service
Source: "{#ServiceDir}\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
; SessionAgent
Source: "{#AgentDir}\*"; DestDir: "{app}\agent"; Flags: ignoreversion recursesubdirs createallsubdirs
; CLI
Source: "{#CliDir}\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer\tracker.cmd"; DestDir: "{app}\cli"; Flags: ignoreversion
; Installer scripts
Source: "installer\scripts\install.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "installer\scripts\uninstall.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
; Browser Extensions (pre-configured per browser)
Source: "Extension\*"; DestDir: "{app}\extension-brave"; Excludes: "config.js"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer\ext-config\brave\config.js"; DestDir: "{app}\extension-brave"; Flags: ignoreversion
Source: "Extension\*"; DestDir: "{app}\extension-chrome"; Excludes: "config.js"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer\ext-config\chrome\config.js"; DestDir: "{app}\extension-chrome"; Flags: ignoreversion
Source: "Extension\*"; DestDir: "{app}\extension-edge"; Excludes: "config.js"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer\ext-config\edge\config.js"; DestDir: "{app}\extension-edge"; Flags: ignoreversion
; Docs
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Add {app}\cli to the system PATH (removes itself on uninstall)
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\cli"; \
    Check: NeedsAddPath('{app}\cli')

[Run]
Filename: "{win}\explorer.exe"; Parameters: """{app}"""; Description: "Open the browser extension folders"; Flags: postinstall skipifsilent unchecked
Filename: "{app}\README.md"; Description: "View README"; Flags: postinstall shellexec skipifsilent unchecked

[Code]
var
  ConsentPage: TWizardPage;
  ConsentCheckbox: TNewCheckBox;

// ─── PATH helper: only add to PATH if not already present ───
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath, ExpandedParam: string;
begin
  if not RegQueryStringValue(HKLM,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'Path', OrigPath)
  then begin
    Result := True;
    exit;
  end;
  ExpandedParam := ExpandConstant(Param);
  Result := Pos(';' + UpperCase(ExpandedParam) + ';', ';' + UpperCase(OrigPath) + ';') = 0;
end;

// ─── Remove from PATH on uninstall ───
procedure RemoveFromPath();
var
  OrigPath, CliPath, NewPath: string;
  P: Integer;
begin
  CliPath := ExpandConstant('{app}\cli');
  if RegQueryStringValue(HKLM,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'Path', OrigPath) then
  begin
    P := Pos(';' + UpperCase(CliPath), UpperCase(OrigPath));
    if P > 0 then
    begin
      NewPath := Copy(OrigPath, 1, P - 1) + Copy(OrigPath, P + Length(';' + CliPath), MaxInt);
      RegWriteExpandStringValue(HKLM,
        'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
        'Path', NewPath);
    end else
    begin
      // Maybe it's at the start without a leading semicolon
      P := Pos(UpperCase(CliPath) + ';', UpperCase(OrigPath));
      if P = 1 then
      begin
        NewPath := Copy(OrigPath, Length(CliPath) + 2, MaxInt);
        RegWriteExpandStringValue(HKLM,
          'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
          'Path', NewPath);
      end;
    end;
  end;
end;

// ─── Consent page ───
procedure InitializeWizard();
var
  Memo: TNewStaticText;
begin
  ConsentPage := CreateCustomPage(wpLicense,
    'What Activity Tracker Records',
    'Please read and confirm you understand what this software does.');

  Memo := TNewStaticText.Create(ConsentPage);
  Memo.Parent := ConsentPage.Surface;
  Memo.Left := 0;
  Memo.Top := 0;
  Memo.Width := ConsentPage.SurfaceWidth;
  Memo.Height := ScaleY(180);
  Memo.AutoSize := False;
  Memo.WordWrap := True;
  Memo.Caption :=
    'Activity Tracker is a privacy-first, local-only tool that records:' + #13#10 + #13#10 +
    '  - Active window titles and application names' + #13#10 +
    '  - Browser tab domains (not full URLs or page content)' + #13#10 +
    '  - Audio/media playback state (which domain is playing)' + #13#10 +
    '  - Lock screen and idle periods' + #13#10 + #13#10 +
    'All data stays on THIS computer in an encrypted database.' + #13#10 +
    'Nothing is sent to the internet. There is no cloud, no telemetry.' + #13#10 + #13#10 +
    'This tool is designed for the person who owns and uses this PC' + #13#10 +
    'to review their own activity. It is not a surveillance tool.' + #13#10 + #13#10 +
    'IMPORTANT: Run this installer as the user whose activity will' + #13#10 +
    'be tracked and approve the administrator prompt. Do not sign' + #13#10 +
    'in as a different admin account.';

  ConsentCheckbox := TNewCheckBox.Create(ConsentPage);
  ConsentCheckbox.Parent := ConsentPage.Surface;
  ConsentCheckbox.Left := 0;
  ConsentCheckbox.Top := Memo.Top + Memo.Height + ScaleY(12);
  ConsentCheckbox.Width := ConsentPage.SurfaceWidth;
  ConsentCheckbox.Height := ScaleY(20);
  ConsentCheckbox.Caption := 'I understand what Activity Tracker records and that data stays on this PC.';
  ConsentCheckbox.Checked := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = ConsentPage.ID then
  begin
    if not ConsentCheckbox.Checked then
    begin
      MsgBox('You must check the "I understand" box to continue.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// ─── PrepareToInstall: stop existing service/agent for upgrade ───
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // Stop service
  Exec('sc.exe', 'stop ActivityTrackerService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Wait briefly for it to stop
  Sleep(3000);
  // Kill agent processes
  Exec('taskkill.exe', '/F /IM ActivityTracker.SessionAgent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

// ─── Post-install: run install.ps1 ───
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  InstallDir, UserName, Params, LogPath: string;
begin
  if CurStep = ssPostInstall then
  begin
    InstallDir := ExpandConstant('{app}');
    UserName := GetUserNameString();
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + InstallDir + '\scripts\install.ps1"' +
              ' -InstallDir "' + InstallDir + '"' +
              ' -UserName "' + UserName + '"';

    if not Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      LogPath := ExpandConstant('{commonappdata}') + '\ActivityTracker\logs\install.log';
      MsgBox('Installation script failed to execute.' + #13#10 +
             'Check the install log at: ' + LogPath, mbError, MB_OK);
    end
    else if ResultCode <> 0 then
    begin
      LogPath := ExpandConstant('{commonappdata}') + '\ActivityTracker\logs\install.log';
      MsgBox('Installation completed with errors (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
             'Check the install log at: ' + LogPath, mbError, MB_OK);
    end;
  end;
end;

// ─── Uninstall: ask about data deletion ───
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Params, AppDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove CLI from PATH
    RemoveFromPath();

    AppDir := ExpandConstant('{app}');
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + AppDir + '\scripts\uninstall.ps1"';

    if MsgBox('Also delete your recorded activity data?' + #13#10 +
              '(Your activity history in %PROGRAMDATA%\ActivityTracker will be permanently removed.)',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      Params := Params + ' -DeleteData';
    end;

    Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// ─── Finished page customization ───
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedLabel.Height := ScaleY(115);
    WizardForm.FinishedLabel.Caption :=
      'Setup has finished installing Activity Tracker on your computer.' + #13#10 + #13#10 +
      'Next steps:' + #13#10 +
      '1) open chrome://extensions or brave://extensions or edge://extensions' + #13#10 +
      '2) turn on Developer mode' + #13#10 +
      '3) Load unpacked and choose ' + ExpandConstant('{app}') + '\extension-<your browser>' + #13#10 +
      '4) open a NEW terminal and run: tracker live';
  end;
end;
