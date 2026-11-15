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

[Files]
Source: "..\app\Screenly.Engine.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\Screenly.Host.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\driver\parsec-vdd\ParsecVDisplay-v0.45-setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\ParsecVDisplay-v0.45-setup.exe"; Parameters: "/S"; StatusMsg: "Installing virtual display driver..."; Flags: waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Screenly"" dir=in action=allow protocol=TCP localport=7543"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Screenly"; Flags: nowait postinstall skipifsilent