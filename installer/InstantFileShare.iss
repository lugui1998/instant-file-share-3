#define AppName "Instant File Share"
#define AppVersion "0.1.0"
#define AppPublisher "Instant File Share"
#define AgentExe "InstantFileShare.Agent.exe"
#define DashboardExe "Instant File Share.exe"

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

[Files]
Source: "{#StageDir}\agent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\shell\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\ui\*"; DestDir: "{app}\ui"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}\Dashboard"; Filename: "{app}\ui\{#DashboardExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ui\{#DashboardExe}"

[Run]
Filename: "{app}\{#AgentExe}"; Description: "Start Instant File Share"; Flags: nowait skipifsilent
Filename: "{app}\ui\{#DashboardExe}"; Description: "Open Instant File Share Dashboard"; Flags: nowait skipifsilent

[Code]
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
