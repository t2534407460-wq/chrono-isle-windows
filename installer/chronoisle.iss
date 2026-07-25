; ChronoIsle · Inno Setup script
;
; 通过 CI 调用：APP_VERSION 和 STAGING_DIR 环境变量必须先设。
;   APP_VERSION  e.g. 0.1.0
;   STAGING_DIR  指向已经准备好的 self-contained 目录（含 ChronoIsle.exe + hooks + setup + deps）
;
; 编译命令：
;   & 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' /Qp installer\chronoisle.iss

#define MyAppName "时屿 ChronoIsle"
#define MyAppVersion GetEnv("APP_VERSION")
#define MyAppPublisher "Tr11111"
#define MyAppURL "https://gitee.com/Tr11111/chrono-isle-windows"
#define MyAppExeName "ChronoIsle.exe"
#define StagingDir GetEnv("STAGING_DIR")

#if MyAppVersion == ""
  #define MyAppVersion "0.0.0-dev"
#endif

[Setup]
; ChronoIsle 使用独立 GUID，与 OpenIsland 的安装记录并存。
AppId={{26B1C074-87EB-4425-B345-3B83FAAF21A6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
; 装到 %LOCALAPPDATA%\ChronoIsle —— 不需要管理员权限
DefaultDirName={localappdata}\ChronoIsle
DefaultGroupName=ChronoIsle
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\installer-output
OutputBaseFilename=ChronoIsle-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
CloseApplications=force
RestartApplications=no

[Languages]
; 仅英文 —— GitHub Actions 的 windows-latest 上 Inno Setup 默认安装不带 ChineseSimplified.isl。
; 想要中文界面需要把 ChineseSimplified.isl vendored 到 installer/ 下并改 MessagesFile 路径。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start 时屿 ChronoIsle automatically when I log in"; GroupDescription: "Startup options:"; Flags: unchecked

[Files]
; 整个 staging dir（已经有完整运行时 + hooks + setup + 文档）—— 复制到安装目录
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启（HKCU\Run）—— 用户在 Tasks 勾了才写；卸载时清掉
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ChronoIsle"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startupicon; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 安装目录里 .NET 运行时生成的少量临时文件，卸载时一并清理
Type: filesandordirs; Name: "{app}\Locales"
