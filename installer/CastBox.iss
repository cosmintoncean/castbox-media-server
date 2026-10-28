#define MyAppName "NestCast Server"
#define MyAppVersion "0.8.4"
#define MyAppPublisher "NestCast"
#define MyAppExeName "CastBox.exe"

[Setup]
SetupIconFile=..\CastBox.ico
AppId={{B32C51AE-0E73-45D7-9A71-73C86DDB3F31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\CastBox
DefaultGroupName=NestCast
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
; FFmpeg (GPL build by gyan.dev) is run as a separate program to capture and encode a shared screen; see ffmpeg\LICENSE.txt
Source: "..\publish\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[InstallDelete]
; Shortcuts from before the rename to NestCast
Type: files; Name: "{autoprograms}\CastBox Server.lnk"
Type: files; Name: "{autodesktop}\CastBox Server.lnk"

[Icons]
Name: "{autoprograms}\NestCast Server"; Filename: "{app}\CastBox.exe"
Name: "{autodesktop}\NestCast Server"; Filename: "{app}\CastBox.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CastBox.exe"; Description: "Launch NestCast Server"; Flags: postinstall nowait skipifsilent
