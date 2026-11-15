#define MyAppName "Screenly"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Screenly"
#define MyAppExeName "Screenly.Host.exe"

[Setup]
AppId={{B8A1F2C3-4D5E-6F7A-8B9C-0D1E2F3A4B5C}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\app
OutputBaseFilename=Screenly-Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "..\app\Screenly.Engine.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\Screenly.Host.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\Screenly.Host.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\Screenly.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\Screenly.Host.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\Screenly.Host.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\driver\parsec-vdd\ParsecVDisplay-v0.45-setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Parsec\vdd\0"; ValueType: dword; ValueName: "w"; ValueData: "1920"; Flags: createvalueifdoesntexist
Root: HKLM; Subkey: "SOFTWARE\Parsec\vdd\0"; ValueType: dword; ValueName: "h"; ValueData: "1080"; Flags: createvalueifdoesntexist
Root: HKLM; Subkey: "SOFTWARE\Parsec\vdd\0"; ValueType: dword; ValueName: "hz"; ValueData: "60"; Flags: createvalueifdoesntexist

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Screenly"; Flags: nowait postinstall skipifsilent