; Build-Release.ps1 supplies all paths and the csproj version to this definition.
#ifndef AppVersion
  #error Run scripts/Build-Release.ps1 to supply release build parameters.
#endif

[Setup]
; Keep AppId stable across versions so installs upgrade the same application.
AppId={{50F582B7-A12E-46BA-B7DB-157546C1D7F9}
AppName=Codex Quota Widget
AppVersion={#AppVersion}
AppVerName=Codex Quota Widget {#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=Codex Quota Widget contributors
DefaultDirName={localappdata}\Programs\CodexQuotaWidget
DefaultGroupName=Codex Quota Widget
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; The x64 spelling also supports existing Inno Setup 6.2 installations.
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
SetupIconFile={#ProjectDir}\assets\app.ico
UninstallDisplayIcon={app}\CodexQuotaWidget.exe
LicenseFile={#ProjectDir}\LICENSE
OutputDir={#ArtifactDir}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; The application can later toggle this same HKCU value from Settings. Remove only its own value on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexQuotaWidget"; ValueData: """{app}\CodexQuotaWidget.exe"""; Flags: uninsdeletevalue

[Icons]
Name: "{autoprograms}\Codex Quota Widget"; Filename: "{app}\CodexQuotaWidget.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Codex Quota Widget"; Filename: "{app}\CodexQuotaWidget.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CodexQuotaWidget.exe"; Description: "Launch Codex Quota Widget"; Flags: nowait postinstall skipifsilent
