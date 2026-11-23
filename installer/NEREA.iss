; NEREA installer (Inno Setup 6). Built by .github/workflows/build-windows.yml
;   iscc /DSourceDir=<publish folder> /DOutputDir=<where to write NEREA-Setup.exe> NEREA.iss

#ifndef SourceDir
  #define SourceDir "..\..\publish\NEREA"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\"
#endif

#define AppName "NEREA"
#define AppVersion "1.0.0"
#define AppExe "NEREA.exe"

[Setup]
AppId={{6B1D0E5A-4F2C-4C57-9A62-4E52454121A0}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=NEREA
AppComments=Let the music create the world.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile=..\Assets\Icons\NEREA.ico
OutputDir={#OutputDir}
OutputBaseFilename=NEREA-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; User data (library, settings) lives in %LOCALAPPDATA%\NEREA and is intentionally kept on uninstall.
Type: filesandordirs; Name: "{app}"
