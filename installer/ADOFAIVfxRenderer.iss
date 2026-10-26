#define MyAppName "ADOFAI VFX Renderer"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\Publish\ADOFAIVfxRenderer"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\Release"
#endif

[Setup]
AppId={{F784B82C-AEE8-48E0-91A3-1930A80B09D9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=hi175570
AppPublisherURL=https://github.com/hi175570/ADOFAIVfxRenderer
AppSupportURL=https://github.com/hi175570/ADOFAIVfxRenderer/issues
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=ADOFAIVfxRenderer-v{#MyAppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ADOFAIVfxRenderer.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷径"; GroupDescription: "其他选项："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\ADOFAIVfxRenderer.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\ADOFAIVfxRenderer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ADOFAIVfxRenderer.exe"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
