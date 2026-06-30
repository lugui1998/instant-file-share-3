#define AppName "Instant File Share"
#define AppPublisher "Instant File Share"
#define AgentExe "InstantFileShare.Agent.exe"
#define DashboardExe "Instant File Share.exe"
#define CloudflaredWingetArgs "install --id Cloudflare.cloudflared -e --accept-source-agreements --accept-package-agreements --disable-interactivity"
#define CloudflaredWingetUninstallArgs "uninstall --id Cloudflare.cloudflared -e --disable-interactivity"

#ifndef AppVersion
  #define AppVersion "1.0.2"
#endif

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
OutputBaseFilename=InstantFileShare-Setup-{#AppVersion}
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\ui\{#DashboardExe}
ChangesEnvironment=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "addtopath"; Description: "Add Instant File Share to the current user's PATH"; GroupDescription: "Command line access:"; Flags: unchecked
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
const
  UserEnvironmentKey = 'Environment';
  UserPathValueName = 'Path';

var
  WingetAvailabilityChecked: Boolean;
  WingetAvailable: Boolean;
  UninstallCloudflared: Boolean;

function NormalizePathEntry(Value: String): String;
begin
  Result := Trim(Value);

  if (Length(Result) >= 2) and (Copy(Result, 1, 1) = '"') and (Copy(Result, Length(Result), 1) = '"') then
  begin
    Result := Copy(Result, 2, Length(Result) - 2);
  end;

  while (Length(Result) > 3) and ((Copy(Result, Length(Result), 1) = '\') or (Copy(Result, Length(Result), 1) = '/')) do
  begin
    Delete(Result, Length(Result), 1);
  end;

  Result := Lowercase(Result);
end;

function ExtractNextPathEntry(var Value: String): String;
var
  SeparatorIndex: Integer;
begin
  SeparatorIndex := Pos(';', Value);
  if SeparatorIndex = 0 then
  begin
    Result := Trim(Value);
    Value := '';
    Exit;
  end;

  Result := Trim(Copy(Value, 1, SeparatorIndex - 1));
  Delete(Value, 1, SeparatorIndex);
end;

function PathContainsEntry(PathValue: String; EntryToFind: String): Boolean;
var
  RemainingPath: String;
  CandidateEntry: String;
  NormalizedEntryToFind: String;
begin
  Result := False;
  RemainingPath := PathValue;
  NormalizedEntryToFind := NormalizePathEntry(EntryToFind);

  while RemainingPath <> '' do
  begin
    CandidateEntry := ExtractNextPathEntry(RemainingPath);
    if NormalizePathEntry(CandidateEntry) = NormalizedEntryToFind then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function RemovePathEntry(PathValue: String; EntryToRemove: String): String;
var
  RemainingPath: String;
  CandidateEntry: String;
  NormalizedEntryToRemove: String;
begin
  Result := '';
  RemainingPath := PathValue;
  NormalizedEntryToRemove := NormalizePathEntry(EntryToRemove);

  while RemainingPath <> '' do
  begin
    CandidateEntry := ExtractNextPathEntry(RemainingPath);
    if (CandidateEntry <> '') and (NormalizePathEntry(CandidateEntry) <> NormalizedEntryToRemove) then
    begin
      if Result = '' then
      begin
        Result := CandidateEntry;
      end
      else
      begin
        Result := Result + ';' + CandidateEntry;
      end;
    end;
  end;
end;

procedure AddInstallDirectoryToUserPath;
var
  CurrentPath: String;
  InstallDirectory: String;
  UpdatedPath: String;
begin
  InstallDirectory := ExpandConstant('{app}');
  if not RegQueryStringValue(HKCU, UserEnvironmentKey, UserPathValueName, CurrentPath) then
  begin
    CurrentPath := '';
  end;

  if PathContainsEntry(CurrentPath, InstallDirectory) then
  begin
    Exit;
  end;

  if Trim(CurrentPath) = '' then
  begin
    UpdatedPath := InstallDirectory;
  end
  else
  begin
    UpdatedPath := CurrentPath + ';' + InstallDirectory;
  end;

  if not RegWriteExpandStringValue(HKCU, UserEnvironmentKey, UserPathValueName, UpdatedPath) then
  begin
    MsgBox('Instant File Share was installed, but the installer could not update your user PATH.', mbError, MB_OK);
  end;
end;

procedure RemoveInstallDirectoryFromUserPath;
var
  CurrentPath: String;
  InstallDirectory: String;
  UpdatedPath: String;
begin
  InstallDirectory := ExpandConstant('{app}');
  if not RegQueryStringValue(HKCU, UserEnvironmentKey, UserPathValueName, CurrentPath) then
  begin
    Exit;
  end;

  UpdatedPath := RemovePathEntry(CurrentPath, InstallDirectory);
  if UpdatedPath = CurrentPath then
  begin
    Exit;
  end;

  RegWriteExpandStringValue(HKCU, UserEnvironmentKey, UserPathValueName, UpdatedPath);
end;

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

function ShouldUninstallCloudflared: Boolean;
begin
  Result := False;
  if UninstallSilent then
  begin
    Exit;
  end;

  if not IsWingetAvailable then
  begin
    Exit;
  end;

  Result :=
    MsgBox(
      'Do you also want to uninstall cloudflared?' #13#13
        'This optional dependency may be used by other applications and will be kept unless you choose Yes.',
      mbConfirmation,
      MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  UninstallCloudflared := ShouldUninstallCloudflared;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
  begin
    AddInstallDirectoryToUserPath;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM "{#AgentExe}" /F /T', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM "{#DashboardExe}" /F', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    Exec(ExpandConstant('{app}\instant_file_share_shell.exe'), '--unregister-file-context-menu', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    Exec(ExpandConstant('{app}\instant_file_share_shell.exe'), '--unregister-folder-zip-context-menu', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    Exec(ExpandConstant('{app}\instant_file_share_shell.exe'), '--unregister-folder-browse-context-menu', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    Exec(ExpandConstant('{app}\instant_file_share_shell.exe'), '--unregister-folder-receive-context-menu', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'InstantFileShare');
    RemoveInstallDirectoryFromUserPath;
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\*\shell\InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\ContextMenus\InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\Background\shell\InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\Background\ContextMenus\InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\DesktopBackground\Shell\InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\DesktopBackground\ContextMenus\InstantFileShare');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\InstantFileShareFolderZip');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\Background\shell\InstantFileShareFolderZip');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\DesktopBackground\Shell\InstantFileShareFolderZip');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\InstantFileShareFolderBrowse');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\Background\shell\InstantFileShareFolderBrowse');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\DesktopBackground\Shell\InstantFileShareFolderBrowse');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\InstantFileShareFolderReceive');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\Background\shell\InstantFileShareFolderReceive');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\DesktopBackground\Shell\InstantFileShareFolderReceive');
    DelTree(ExpandConstant('{localappdata}\InstantFileShare'), True, True, True);
    DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
    if UninstallCloudflared then
    begin
      Exec(ExpandConstant('{cmd}'), '/c winget {#CloudflaredWingetUninstallArgs}', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    end;
  end;
end;
