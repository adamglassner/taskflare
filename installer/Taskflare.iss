; Inno Setup script for Taskflare (Inno Setup 6.3 or newer)
; Build: run ..\build.ps1, or compile this file with ISCC.exe after publishing the exe.
;
; Installs to Program Files for all users by default (asks for administrator permission).
; The first page also offers "Install for me only", which needs no administrator rights.
;
; The Bytehawk Studio Launcher can run this setup silently:
;   Taskflare-<version>-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR="<folder>"

#define MyAppName "Taskflare"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Bytehawk Studio"
#define MyAppExeName "Taskflare.exe"
#define MyAppUrl "https://github.com/adamglassner/taskflare"
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

[Setup]
AppId={{4B7E1D92-6C3A-4F58-9E21-7A5D3C8B0F64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
AppCopyright=© 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Taskflare-{#MyAppVersion}-Setup
SetupIconFile=..\assets\taskflare.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Ask to close Taskflare if it's running (Windows Restart Manager)
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the [name/ver] Setup Wizard
FinishedHeadingLabel=[name/ver] is installed

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The icon is embedded in Taskflare.exe. /NOICONS (used by the Bytehawk Studio Launcher) skips the Start menu entry.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "To-do list"; Check: not NoIconsRequested
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "To-do list"; Tasks: desktopicon

[UninstallRun]
; Stop Taskflare (it may be running in the notification area) before removing it
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "StopTaskflare"

[Registry]
; "Start with Windows" entry, created by the app; removed on uninstall
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Taskflare"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete]: uninstalling removes the program and shortcuts.
; Your tasks and settings (%APPDATA%\Taskflare) are left alone.

[Code]
{ /NOICONS: the launcher installs Taskflare without Start menu entries. Inno Setup only honours it
  on the Start Menu folder page, which Taskflare doesn't show, so check the command line directly. }
function NoIconsRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/NOICONS') = 0 then
      Result := True;
end;
