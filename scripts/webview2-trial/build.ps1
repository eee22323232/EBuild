param(
  [Parameter(Mandatory)] [string] $Output,
  [Parameter(Mandatory)] [string] $Cache,
  [string] $Iscc
)
$ErrorActionPreference = 'Stop'
$Output = (New-Item -ItemType Directory -Path $Output -Force).FullName
$Cache = (New-Item -ItemType Directory -Path $Cache -Force).FullName

function Get-BuildDependency([string] $Name, [string] $Url, [string] $Sha256) {
  $file = Join-Path $Cache $Name
  if ((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file).Hash -eq $Sha256) {
    return $file
  }

  $partial = "$file.$([guid]::NewGuid().ToString('N')).download"
  try {
    & curl.exe -fLsS --retry 2 --connect-timeout 15 --max-time 120 $Url -o $partial
    if ($LASTEXITCODE -ne 0) { throw "Could not download $Name." }
    if ((Get-FileHash -LiteralPath $partial).Hash -ne $Sha256) { throw "SHA-256 mismatch for $Name." }
    Move-Item -LiteralPath $partial -Destination $file -Force
    return $file
  } finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
  }
}

$stage = (New-Item -ItemType Directory -Path (Join-Path $Output ('stage-' + [guid]::NewGuid().ToString('N')))).FullName
$sdk = Get-BuildDependency 'webview2.zip' `
  'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4314.35/microsoft.web.webview2.1.0.4314.35.nupkg' `
  '90778DEB1B77E36292828B4B5833C9255C27533F40E0DDD55891AE718944EDC4'
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
  '/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Net.Http.dll','/r:System.Security.dll','/r:System.Management.dll',
  '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
  "/r:$stage\Microsoft.Web.WebView2.Core.dll","/r:$stage\Microsoft.Web.WebView2.WinForms.dll")
$source = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName
& $compiler @args @source
if ($LASTEXITCODE) { throw 'Could not compile EBuild.' }
if ($Iscc) {
  $language = Get-BuildDependency 'ChineseSimplified.isl' `
    'https://raw.githubusercontent.com/jrsoftware/issrc/c34976f7d107e94d4fce8ac29f72ca33762293d7/Files/Languages/ChineseSimplified.isl' `
    'E0B0B350E2245F3C5E65586DFE43D574F6E7F06F2261149ABA284954B3FC9A8D'
  # Use the specific Microsoft download behind the Evergreen link for this build.
  $bootstrapper = Get-BuildDependency 'MicrosoftEdgeWebview2Setup.exe' `
    'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/068be74d-a831-494f-8f57-c173756d5a33/MicrosoftEdgeWebview2Setup.exe' `
    'CF2F2445FEF0C5C14E372045F84998771EEB8BB01D59BE4AFD00692FA160082E'
  $sign = Get-AuthenticodeSignature -LiteralPath $bootstrapper
  if ($sign.Status -ne 'Valid' -or $sign.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') { throw 'Invalid Microsoft bootstrapper signature.' }
  $iss = Join-Path $Output 'trial-setup.iss'
  [IO.File]::WriteAllText($iss,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'setup.iss')),[Text.UTF8Encoding]::new($true))
  & $Iscc /Qp "/DPayloadDir=$stage" "/DBootstrapper=$bootstrapper" "/DLanguageFile=$language" "/O$Output" $iss
  if ($LASTEXITCODE) { throw 'Could not create the installer.' }
}
Write-Output ('Stage: ' + $stage)
