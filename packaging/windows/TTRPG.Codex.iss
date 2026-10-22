; Inno Setup script for the Windows installer. Built by packaging/package.ps1:
;   iscc /DAppVersion=1.2.3 /DSourceDir=<app folder> /DOutputDir=<dist> packaging\windows\TTRPG.Codex.iss
; Installs per user (no admin prompt) unless the user chooses "install for all users".
; Campaign data lives in %LOCALAPPDATA%\TTRPG.Codex and is left alone on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\win-x64\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{8F0C1E52-6C3B-4B3B-9D6E-5A2B7C1D4E90}
AppName=TTRPG.Codex
AppVersion={#AppVersion}
AppPublisher=TTRPG.Codex
AppPublisherURL=https://github.com/myarichuk/TTRPG.Codex
DefaultDirName={autopf}\TTRPG.Codex
DefaultGroupName=TTRPG.Codex
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=TTRPG.Codex-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=TTRPG.Codex

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; An upgrade replaces the app and its runtime wholesale, so files a newer version dropped don't linger.
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\content"
Type: filesandordirs; Name: "{app}\launcher"

[Icons]
; The tray launcher starts and stops the server; TTRPG.Codex.cmd is the plain server window.
Name: "{group}\TTRPG.Codex"; Filename: "{app}\launcher\ttrpg-codex-tray.exe"; WorkingDir: "{app}"
Name: "{group}\TTRPG.Codex (server window)"; Filename: "{app}\TTRPG.Codex.cmd"; WorkingDir: "{app}"
Name: "{group}\Uninstall TTRPG.Codex"; Filename: "{uninstallexe}"
Name: "{autodesktop}\TTRPG.Codex"; Filename: "{app}\launcher\ttrpg-codex-tray.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\launcher\ttrpg-codex-tray.exe"; Description: "Start TTRPG.Codex now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Stop the tray launcher (and with it the server) so its files can be removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM ttrpg-codex-tray.exe /F"; Flags: runhidden; RunOnceId: "StopLauncher"
