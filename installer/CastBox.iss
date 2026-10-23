#define MyAppName "CastBox Server"
#define MyAppVersion "0.1.5"
#define MyAppPublisher "CastBox"
#define MyAppExeName "CastBox.exe"

[Setup]
SetupIconFile=..\CastBox.ico
AppId={{B32C51AE-0E73-45D7-9A71-73C86DDB3F31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\CastBox
DefaultGroupName=CastBox
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\installer-output
OutputBaseFilename=CastBox-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\CastBox.exe
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\CastBox.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CastBox Server"; Filename: "{app}\CastBox.exe"
Name: "{autodesktop}\CastBox Server"; Filename: "{app}\CastBox.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CastBox.exe"; Description: "Launch CastBox Server"; Flags: postinstall nowait skipifsilent
