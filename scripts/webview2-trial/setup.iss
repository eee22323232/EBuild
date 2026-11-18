[Setup]
AppId={{A96E3633-82E7-434D-9DE8-BC60DA4C3BF4}
AppName=EBuild 启动器
AppVersion=dev.0.1.2
DefaultDirName={localappdata}\Programs\EBuild
DefaultGroupName=EBuild 启动器
DisableProgramGroupPage=yes
AllowRootDirectory=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
WizardStyle=modern
SetupIconFile={#PayloadDir}\ui\icon.ico
UninstallDisplayIcon={app}\EBuild-WebView2.exe
OutputBaseFilename=EBuild-dev.0.1.2-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "zhcn"; MessagesFile: "{#LanguageFile}"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#Bootstrapper}"; Flags: dontcopy

[Icons]
Name: "{group}\EBuild 启动器"; Filename: "{app}\EBuild-WebView2.exe"

[Run]
Filename: "{app}\EBuild-WebView2.exe"; Description: "打开 EBuild 启动器"; Flags: nowait postinstall skipifsilent; Check: CanLaunch

[Code]
var
  RuntimeReady: Boolean;

function DetectRuntime: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{app}\EBuild-WebView2.exe'), '--check-runtime', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure CurStepChanged(Step: TSetupStep);
var
  Code: Integer;
begin
  if Step <> ssPostInstall then Exit;
  RuntimeReady := DetectRuntime;
  if RuntimeReady then Exit;
  WizardForm.StatusLabel.Caption := '正在下载并安装 Microsoft Edge WebView2 运行时…';
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log(Format('WebView2 bootstrapper exit code: %d', [Code]));
  RuntimeReady := DetectRuntime;
  if not RuntimeReady then
    SuppressibleMsgBox('无法安装 WebView2 运行时。请检查网络；安装微软 WebView2 运行时后再打开 EBuild。', mbError, MB_OK, IDOK);
end;

function CanLaunch: Boolean;
begin
  Result := RuntimeReady;
end;
