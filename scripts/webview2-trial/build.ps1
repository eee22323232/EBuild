param(
  [Parameter(Mandatory)] [string] $Output,
  [Parameter(Mandatory)] [string] $Cache,
  [string] $Iscc
)
$ErrorActionPreference = 'Stop'
$Output = (New-Item -ItemType Directory -Path $Output -Force).FullName
$Cache = (New-Item -ItemType Directory -Path $Cache -Force).FullName
$stage = (New-Item -ItemType Directory -Path (Join-Path $Output ('stage-' + [guid]::NewGuid().ToString('N')))).FullName
$sdk = Join-Path $Cache 'webview2.zip'
if (!(Test-Path -LiteralPath $sdk)) {
  & curl.exe -fLsS 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4314.35/microsoft.web.webview2.1.0.4314.35.nupkg' -o $sdk
  if ($LASTEXITCODE) { throw 'Could not download the WebView2 SDK.' }
}
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
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge.js'), (Join-Path $PSScriptRoot 'channels.json') -Destination $stage
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$args = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',"/out:$stage\EBuild-WebView2.exe",
  "/win32manifest:$PSScriptRoot\windows.manifest","/win32icon:$stage\ui\icon.ico",
  '/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Net.Http.dll','/r:System.Security.dll',
  '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
  "/r:$stage\Microsoft.Web.WebView2.Core.dll","/r:$stage\Microsoft.Web.WebView2.WinForms.dll")
$source = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName
& $compiler @args @source
if ($LASTEXITCODE) { throw 'Could not compile the trial.' }
if ($Iscc) {
  $bootstrapper = Join-Path $Cache 'MicrosoftEdgeWebview2Setup.exe'
  if (!(Test-Path -LiteralPath $bootstrapper)) {
    & curl.exe -fLsS 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -o $bootstrapper
    if ($LASTEXITCODE) { throw 'Could not download the WebView2 bootstrapper.' }
  }
  $sign = Get-AuthenticodeSignature -LiteralPath $bootstrapper
  if ($sign.Status -ne 'Valid' -or $sign.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') { throw 'Invalid Microsoft bootstrapper signature.' }
  $iss = Join-Path $Output 'trial-setup.iss'
  [IO.File]::WriteAllText($iss,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'setup.iss')),[Text.UTF8Encoding]::new($true))
  & $Iscc /Qp "/DPayloadDir=$stage" "/DBootstrapper=$bootstrapper" "/O$Output" $iss
  if ($LASTEXITCODE) { throw 'Could not create the installer.' }
}
Write-Output ('Stage: ' + $stage)
