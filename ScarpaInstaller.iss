[Setup]
AppName=Scarpa Connection Manager
AppVersion=1.0.1.0
DefaultDirName={autopf}\ScarpaConnectionManager
OutputBaseFilename=ScarpaSetup_v1.0.1.0_x64
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