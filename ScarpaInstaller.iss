[Setup]
AppName=Scarpa Connection Manager
AppVersion=1.1.0
DefaultDirName={autopf}\ScarpaConnectionManager
OutputBaseFilename=ScarpaConnectionManagerSetup_1.1.0
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Releases
DefaultGroupName=Network
DisableProgramGroupPage=yes
SetupIconFile=Assets\scarpa_icon.ico
UninstallDisplayIcon={app}\scarpa-connection-manager_win.exe

[Files]
Source: "Publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Scarpa Connection Manager"; Filename: "{app}\scarpa-connection-manager_win.exe"; IconFilename: "{app}\scarpa-connection-manager_win.exe"

[Run]
Filename: "{app}\scarpa-connection-manager_win.exe"; Description: "Launch Scarpa Connection Manager"; Flags: nowait postinstall skipifsilent

[Code]
function SetForegroundWindow(hWnd: Integer): Boolean;
external 'SetForegroundWindow@user32.dll stdcall';

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
  begin
    WizardForm.BringToFront();
    SetForegroundWindow(WizardForm.Handle);
  end;
end;