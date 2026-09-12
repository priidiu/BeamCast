; BeamCast v0.1 — Inno Setup 6 (Windows 11 x64, unpackaged WinUI 3).
; Build: installer\build.ps1 (requires .NET 10 SDK + Inno Setup 6).

#define MyAppName "BeamCast"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Rhizako"
#define MyAppExeName "AirNext.App.exe"

[Setup]
AppId={{A8E3C4D1-7B2F-4E91-9C5A-1F6D8B0E4A27}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\BeamCast
DefaultGroupName=BeamCast
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=BeamCast-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\AirNext.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=AirNext.App.exe
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch BeamCast"; Flags: nowait postinstall skipifsilent
