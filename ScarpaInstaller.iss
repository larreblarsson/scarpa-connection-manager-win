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
const
  HWND_TOPMOST = -1;
  HWND_NOTOPMOST = -2;
  SWP_NOSIZE = 1;
  SWP_NOMOVE = 2;
  SWP_SHOWWINDOW = 64;

function SetWindowPos(hWnd: Integer; hWndInsertAfter: Integer; X, Y, cx, cy, uFlags: Integer): Boolean;
external 'SetWindowPos@user32.dll stdcall';

procedure InitializeWizard();
begin
  // 1. Force the installer to the absolute top layer of the Z-order
  SetWindowPos(WizardForm.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE or SWP_SHOWWINDOW);
  
  // 2. Immediately remove the 'always on top' flag so it doesn't get permanently stuck over your other apps
  SetWindowPos(WizardForm.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE or SWP_SHOWWINDOW);
end;