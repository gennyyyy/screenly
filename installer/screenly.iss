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
; We stop the app ourselves in [Code] before files are replaced. Inno's Restart
; Manager prompt would hide the engine behind the host's watchdog and leave
; files locked, so it is disabled.
CloseApplications=no

[Files]
; Ships the self-contained publish folder built with:
;   dotnet publish windows\Screenly.Engine -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\win-x64
;   dotnet publish windows\Screenly.Host   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\win-x64
; Copied recursively on purpose: the runtime and the H264Sharp/OpenH264 natives must ship
; as-is. Never point this at app\ (it holds engine_new and runtimes).
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled Android platform-tools so USB devices work without a separate SDK install.
Source: "adb\*"; DestDir: "{app}\adb"; Flags: ignoreversion
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
; Install Parsec Virtual Display Driver (requires admin, may require reboot).
Filename: "{tmp}\ParsecVDisplay-v0.45-setup.exe"; Parameters: "/S"; StatusMsg: "Installing Parsec Virtual Display Driver..."; Flags: waituntilterminated
; Grant standard users write access to the Parsec VDD registry presets so the
; engine can register arbitrary resolutions at runtime (no per-launch UAC).
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$p='HKLM:\SOFTWARE\Parsec\vdd'; New-Item -Path $p -Force | Out-Null; $a=Get-Acl $p; $r=New-Object System.Security.AccessControl.RegistryAccessRule('Users','SetValue,CreateSubKey,Delete','ContainerInherit,ObjectInherit','None','Allow'); $a.SetAccessRule($r); Set-Acl $p $a"""; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Screenly"; Flags: nowait postinstall skipifsilent

[Code]
// An update must replace files while nothing runs. The host watches the engine on
// a 5s timer and restarts it, so the host dies first (its tree included, via /T)
// and the engine is killed again after a pause to catch a restart in between.
procedure KillScreenlyProcesses;
var
  ExitCode: Integer;
begin
  // Exit code 128 only means "not running"; nothing else is actionable here --
  // if a file is still locked, Inno reports it loudly during the copy.
  Exec('taskkill.exe', '/F /IM Screenly.Host.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Exec('taskkill.exe', '/F /IM Screenly.Engine.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Exec('taskkill.exe', '/F /IM adb.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillScreenlyProcesses;
  Sleep(1000);
  KillScreenlyProcesses;
  Result := '';
end;