[Setup]
AppId={{54A55E1D-1566-44DF-BC1A-B1280E52B7E4}
AppName=EBuild 启动器（WPF）
AppVersion=dev.{#ReleaseVersion}
AppPublisher=EBuild
VersionInfoVersion={#ReleaseVersion}.0
DefaultDirName={localappdata}\Programs\EBuild-WPF
DefaultGroupName=EBuild 启动器（WPF）
DisableProgramGroupPage=yes
AllowRootDirectory=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\webview2-trial\ui\icon.ico
UninstallDisplayIcon={app}\EBuild-WPF.exe
OutputBaseFilename=EBuild-dev.{#ReleaseVersion}-WPF-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PayloadDir}\EBuild-WPF.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\EBuild-WPF.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\EBuild 启动器（WPF）"; Filename: "{app}\EBuild-WPF.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\EBuild-WPF.exe"; WorkingDir: "{app}"; Description: "打开 EBuild 启动器"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
var
  FrameworkRelease: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release', FrameworkRelease) and (FrameworkRelease >= 528040);
  if not Result then
    SuppressibleMsgBox('EBuild 需要 .NET Framework 4.8 或更高版本。请安装微软运行时后再试。', mbError, MB_OK, IDOK);
end;
