; Pocket Agent installer (Inno Setup 6.3 or later).
; Build the app first with build-windows.ps1, then: ISCC.exe installer\PocketAgent.iss
; Installs per user (no administrator rights needed). Same AppId as the 0.3 builds, so it upgrades them in place.

#define AppName "Pocket Agent"
#define AppVersion "1.0.0"
#define AppExe "PocketAgent.Windows.exe"
#define DataFolder "PocketAgentWindows"

[Setup]
AppId={{8FCA7D96-E2DF-4C62-BE0C-9E8B2EED9BE0}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\PocketAgentWindows
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=PocketAgent-Setup
SetupIconFile=..\src\PocketAgent.Windows\Assets\PocketAgent.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; The app's single-instance mutex: setup asks you to quit Pocket Agent before it replaces files.
AppMutex=PocketAgentWindows.SingleInstance
CloseApplications=yes

[Files]
Source: "..\dist\PocketAgent-Windows\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove the "Start with Windows" entry the app adds when that setting is on. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    Data := ExpandConstant('{localappdata}\{#DataFolder}');
    if (not UninstallSilent) and DirExists(Data) then
      if MsgBox('Also delete your Pocket Agent settings, saved API keys, approvals and logs?' + #13#10#13#10 + Data,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
