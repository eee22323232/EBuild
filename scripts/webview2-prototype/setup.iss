[Setup]
AppId={{B2E45AFC-8F3E-4446-8C9C-CFD11CEE7BF3}
AppName=EBuild WebView2 实验版
AppVersion=0.1
DefaultDirName={localappdata}\Programs\EBuild-WebView2-Prototype
DefaultGroupName=EBuild WebView2 实验版
DisableProgramGroupPage=yes
AllowRootDirectory=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.14393
WizardStyle=modern
SetupIconFile={#PayloadDir}\src\ui\icon.ico
UninstallDisplayIcon={app}\EBuild-WebView2.exe
OutputBaseFilename=EBuild-WebView2-Prototype-Windows-x64-Setup
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#Bootstrapper}"; Flags: dontcopy

[Icons]
Name: "{group}\EBuild WebView2 实验版"; Filename: "{app}\EBuild-WebView2.exe"

[Run]
Filename: "{app}\EBuild-WebView2.exe"; Description: "打开 WebView2 实验版"; Flags: nowait postinstall skipifsilent; Check: CanLaunch

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
    SuppressibleMsgBox('无法安装 WebView2 运行时。请检查网络，并从 Microsoft 安装 Evergreen WebView2 运行时后重新打开实验版。', mbError, MB_OK, IDOK);
end;

function CanLaunch: Boolean;
begin
  Result := RuntimeReady;
end;
