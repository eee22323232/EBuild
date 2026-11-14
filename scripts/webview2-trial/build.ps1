param(
  [Parameter(Mandatory)] [string] $Output,
  [Parameter(Mandatory)] [string] $Cache,
  [Parameter(Mandatory)] [string] $Iscc
)
$ErrorActionPreference = 'Stop'
$Output = (New-Item -ItemType Directory -Path $Output -Force).FullName
$Cache = (New-Item -ItemType Directory -Path $Cache -Force).FullName
$stage = (New-Item -ItemType Directory -Path (Join-Path $Output ('stage-' + [guid]::NewGuid().ToString('N')))).FullName
$sdk = Join-Path $Cache 'webview2.zip'
$bootstrapper = Join-Path $Cache 'MicrosoftEdgeWebview2Setup.exe'
foreach ($dependency in @(@($sdk,'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4314.35/microsoft.web.webview2.1.0.4314.35.nupkg'),
  @($bootstrapper,'https://go.microsoft.com/fwlink/p/?LinkId=2124703'))) {
  if (!(Test-Path -LiteralPath $dependency[0])) {
    & curl.exe -fLsS $dependency[1] -o $dependency[0]
    if ($LASTEXITCODE) { throw "Could not download $($dependency[1])" }
  }
}
$sign = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($sign.Status -ne 'Valid' -or $sign.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') { throw 'Invalid Microsoft bootstrapper signature.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($sdk)
try {
  foreach ($file in @('lib/net462/Microsoft.Web.WebView2.Core.dll','lib/net462/Microsoft.Web.WebView2.WinForms.dll','runtimes/win-x64/native/WebView2Loader.dll','LICENSE.txt')) {
    $outName = if ($file -eq 'LICENSE.txt') { 'WEBVIEW2-LICENSE.txt' } else { [IO.Path]::GetFileName($file) }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($file), (Join-Path $stage $outName))
  }
} finally { $archive.Dispose() }
$project = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ui') -Destination $stage -Recurse
$unused = Join-Path $stage 'ui/landscape.png'
if (Test-Path -LiteralPath $unused) { Remove-Item -LiteralPath $unused }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ui-trial.js') -Destination (Join-Path $stage 'ui')
$index = Join-Path $stage 'ui/index.html'
$html = [IO.File]::ReadAllText($index)
$html = [regex]::Replace($html, '(?m)^\s*<header class="titlebar".*</header>', '')
$html = $html.Replace('</head>', ('  <link rel="stylesheet" href="trial.css">' + [Environment]::NewLine + '  <script src="ui-trial.js" defer></script>' + [Environment]::NewLine + '</head>'))
[IO.File]::WriteAllText($index,$html)
[IO.File]::WriteAllText((Join-Path $stage 'ui/trial.css'),'.shell {height: 100vh;}')
$channelMap = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'channels.json'))
$bridge = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'bridge.js')).Replace('/* CHANNELS */ {}',$channelMap)
[IO.File]::WriteAllText((Join-Path $stage 'bridge.js'),$bridge)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$args = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',"/out:$stage\EBuild-WebView2.exe",
  "/win32manifest:$PSScriptRoot\windows.manifest","/win32icon:$stage\ui\icon.ico",
  '/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Net.Http.dll','/r:System.Security.dll',
  '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
  "/r:$stage\Microsoft.Web.WebView2.Core.dll","/r:$stage\Microsoft.Web.WebView2.WinForms.dll")
$source = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName
& $compiler @args @source
if ($LASTEXITCODE) { throw 'Could not compile the trial.' }
$iss = Join-Path $Output 'trial-setup.iss'
[IO.File]::WriteAllText($iss,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'setup.iss')),[Text.UTF8Encoding]::new($true))
& $Iscc /Qp "/DPayloadDir=$stage" "/DBootstrapper=$bootstrapper" "/O$Output" $iss
if ($LASTEXITCODE) { throw 'Could not create the installer.' }
Write-Output ('Stage: ' + $stage)
