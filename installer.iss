; SpicetifyGui installer — per-user, no admin rights required.
;
; Built locally:
;   dotnet publish MarketplaceInstaller.Gui/MarketplaceInstaller.Gui.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-single
;   ISCC.exe /DAppVersion=1.0.0 installer.iss
;
; Installs the single-file build to %LocalAppData%\Programs\SpicetifyGui.

#define MyAppName "SpicetifyGui"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define MyAppPublisher "thecloudyy"
#define MyAppURL "https://github.com/thecloudyy/SpicetifyGui"
#define MyAppExeName "SpicetifyGui.exe"

[Setup]
AppId={{E1A6F978-8A69-4073-8656-91DFC83A6087}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename={#MyAppName}-Setup-v{#AppVersion}-win-x64
SetupIconFile=MarketplaceInstaller.Gui\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish-single\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; The App card "Reinstall" runs the setup /SILENT and exits: without skipifsilent the app
; would stay closed afterwards. With it removed, silent installs relaunch the app when done
; (interactive installs still show the launch checkbox via postinstall).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall
