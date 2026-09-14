#define MyAppName "Minecraft Rotating Host"
#define MyAppExeName "MinecraftHostApp.exe"
#define MyAppPublisher "emirahror"

[Setup]
AppId={{B15E7F1A-6B2E-4B1F-9C3A-8E1D2F6A9C10}}
AppName={#MyAppName}
AppVersion=1.0
AppPublisher={#MyAppPublisher}
; Ставим в папку пользователя (LocalAppData), без прав администратора -
; у друзей может не быть админских прав на своём ПК, а серверные файлы и так
; складываются рядом с exe, для чего Program Files не подходит без лишних прав.
DefaultDirName={localappdata}\Programs\MinecraftHostApp
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=installer_output
OutputBaseFilename=MinecraftHostApp-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=installer_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish\MinecraftHostApp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
