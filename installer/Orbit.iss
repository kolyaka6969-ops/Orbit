; Установщик Orbit (Inno Setup 6). Собирается из build.ps1:  iscc /DAppVersion=1.0.0 installer\Orbit.iss
; Ставится для текущего пользователя, без прав администратора — как приложения из Microsoft Store.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{5E0C2B7A-3A41-4C59-9D0B-6E2E0B7C9A11}
AppName=Orbit
AppVersion={#AppVersion}
AppVerName=Orbit {#AppVersion}
AppPublisher=Orbit
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Установщик Orbit
DefaultDirName={localappdata}\Programs\Orbit
DefaultGroupName=Orbit
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=Orbit-{#AppVersion}-Setup-x64
SetupIconFile=..\src\Orbit\Assets\Orbit.ico
UninstallDisplayIcon={app}\Orbit.exe
UninstallDisplayName=Orbit
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=110
WizardImageFile=wizard-large.png,wizard-large-2x.png
WizardSmallImageFile=wizard-small.png,wizard-small-2x.png
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.Autostart=Запускать Orbit при входе в Windows
english.Autostart=Start Orbit when I sign in to Windows
russian.ContextMenu=Добавить «Открыть в Orbit» в контекстное меню Проводника
english.ContextMenu=Add "Open in Orbit" to the File Explorer context menu
russian.Integration=Интеграция с Windows:
english.Integration=Windows integration:

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"; GroupDescription: "{cm:Integration}"
Name: "contextmenu"; Description: "{cm:ContextMenu}"; GroupDescription: "{cm:Integration}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\Orbit"; Filename: "{app}\Orbit.exe"
Name: "{userdesktop}\Orbit"; Filename: "{app}\Orbit.exe"; Tasks: desktopicon

[Registry]
; Автозапуск — тот же ключ, что переключается в настройках программы.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Orbit"; ValueData: """{app}\Orbit.exe"" --autostart"; Tasks: autostart; Flags: uninsdeletevalue
; Пункт контекстного меню для файлов и папок.
Root: HKCU; Subkey: "Software\Classes\*\shell\Orbit"; ValueType: string; ValueName: ""; ValueData: "Открыть в Orbit"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Orbit"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Orbit.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Orbit"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Orbit\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Orbit.exe"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Orbit"; ValueType: string; ValueName: ""; ValueData: "Открыть в Orbit"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Orbit"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Orbit.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Orbit"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Orbit\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Orbit.exe"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\Orbit.exe"; Description: "{cm:LaunchProgram,Orbit}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Убирает автозапуск и пункт меню (даже если их включили в настройках программы) и закрывает запущенный Orbit.
Filename: "{app}\Orbit.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// При обновлении просим запущенную копию закрыться, чтобы файлы можно было заменить (настройки не трогаем).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\Orbit.exe')) then
  begin
    Exec(ExpandConstant('{app}\Orbit.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(500);
  end;
  Result := '';
end;
