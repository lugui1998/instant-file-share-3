#define AppName "Instant File Share"
#define AppVersion "0.1.0"
#define AppPublisher "Instant File Share"
#define AgentExe "InstantFileShare.Agent.exe"
#define DashboardExe "Instant File Share.exe"
#define CloudflaredWingetArgs "install --id Cloudflare.cloudflared -e --accept-source-agreements --accept-package-agreements --disable-interactivity"

#ifndef StageDir
  #error StageDir must be provided to the compiler.
#endif

#ifndef OutputDir
  #define OutputDir "artifacts\package\installer"
#endif

[Setup]
AppId={{9A90D8A2-4FEA-4A0B-95E0-C17DBDDBE5F0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=InstantFileShare-Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\ui\{#DashboardExe}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "installcloudflared"; Description: "Install cloudflared automatically with Winget (recommended for Cloudflare publish modes)"; GroupDescription: "Optional components:"; Check: IsWingetAvailable

[Files]
Source: "{#StageDir}\agent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\shell\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\ui\*"; DestDir: "{app}\ui"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}\Dashboard"; Filename: "{app}\ui\{#DashboardExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AgentExe}"; Parameters: "--open-dashboard"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/c winget {#CloudflaredWingetArgs}"; StatusMsg: "Installing cloudflared..."; Flags: waituntilterminated runhidden; Tasks: installcloudflared; Check: IsWingetAvailable
Filename: "{app}\{#AgentExe}"; Parameters: "--open-dashboard --installer-first-run"; Description: "Start Instant File Share"; Flags: nowait skipifsilent postinstall

[Code]
var
  WingetAvailabilityChecked: Boolean;
  WingetAvailable: Boolean;

function IsWingetAvailable: Boolean;
var
  ExitCode: Integer;
begin
  if not WingetAvailabilityChecked then
  begin
    WingetAvailable :=
      Exec(
        ExpandConstant('{cmd}'),
        '/c where winget >nul 2>nul',
        '',
        SW_HIDE,
        ewWaitUntilTerminated,
        ExitCode) and
      (ExitCode = 0);
    WingetAvailabilityChecked := True;
  end;

  Result := WingetAvailable;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM "{#AgentExe}" /F', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM "{#DashboardExe}" /F', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    Exec(ExpandConstant('{app}\instant_file_share_shell.exe'), '--unregister-context-menu', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\*\shell\InstantFileShare');
    DelTree(ExpandConstant('{localappdata}\InstantFileShare'), True, True, True);
  end;
end;
