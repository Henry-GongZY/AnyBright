; Inno Setup script for AnyBright (WinUI 3, self-contained, unpackaged)
; Version is injected by CI:  iscc /DMyAppVersion=x.y.z installer/AnyBright.iss

#define MyAppName "AnyBright"
#define MyAppExeName "AnyBright.exe"
#ifndef MyAppVersion
#define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{6E1F4C7B-9A34-4E8D-8B2F-2C50D9A1F3A6}
AppVersion={#MyAppVersion}
AppName={#MyAppName}
WizardStyle=modern
SetupIconFile=..\App\assets\app.ico
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=Output
#ifdef IsArm64
OutputBaseFilename=AnyBright-{#MyAppVersion}-setup-arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
#else
OutputBaseFilename=AnyBright-{#MyAppVersion}-setup-x64
ArchitecturesInstallIn64BitMode=x64compatible
#endif
Compression=lzma2/max
SolidCompression=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
#ifdef IsArm64
Source: "..\publish\out-arm64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
#else
Source: "..\publish\out\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
