param(
  [Parameter(Mandatory)] [string] $LauncherRoot,
  [Parameter(Mandatory)] [string] $Output,
  [Parameter(Mandatory)] [string] $Iscc
)
$ErrorActionPreference = 'Stop'
$LauncherRoot = (Resolve-Path -LiteralPath $LauncherRoot).Path
$Output = (New-Item -ItemType Directory -Path $Output -Force).FullName
$cache = (New-Item -ItemType Directory -Path (Join-Path $Output 'cache') -Force).FullName
$stage = (New-Item -ItemType Directory -Path (Join-Path $Output ('stage-' + [Guid]::NewGuid().ToString('N')))).FullName
$sdk = Join-Path $cache 'webview2.zip'
$bootstrapper = Join-Path $cache 'MicrosoftEdgeWebview2Setup.exe'
$node = (Get-Command node.exe).Source
if ((& $node -p process.arch) -ne 'x64') { throw 'Build with Windows x64 Node.js.' }
if (!(Test-Path -LiteralPath $sdk)) {
  & curl.exe -fLsS 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4314.35/microsoft.web.webview2.1.0.4314.35.nupkg' -o $sdk
  if ($LASTEXITCODE) { throw 'Cannot download the WebView2 SDK.' }
}
if (!(Test-Path -LiteralPath $bootstrapper)) {
  & curl.exe -fLsS 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -o $bootstrapper
  if ($LASTEXITCODE) { throw 'Cannot download the WebView2 bootstrapper.' }
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') {
  throw 'The WebView2 bootstrapper must be signed by Microsoft.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($sdk)
try {
  foreach ($file in @('lib/net462/Microsoft.Web.WebView2.Core.dll','lib/net462/Microsoft.Web.WebView2.WinForms.dll','runtimes/win-x64/native/WebView2Loader.dll','LICENSE.txt')) {
    $outName = if ($file -eq 'LICENSE.txt') { 'WEBVIEW2-LICENSE.txt' } else { [IO.Path]::GetFileName($file) }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($file), (Join-Path $stage $outName))
  }
} finally { $archive.Dispose() }
Copy-Item -LiteralPath $node -Destination (Join-Path $stage 'node.exe')
Copy-Item -LiteralPath (Join-Path $LauncherRoot 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $LauncherRoot 'src') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'backend.cjs'), (Join-Path $PSScriptRoot 'bridge.js') -Destination $stage
& $node (Join-Path $PSScriptRoot 'production-deps.cjs') $LauncherRoot $stage
if ($LASTEXITCODE) { throw 'Cannot copy production dependencies.' }

$nodeLicense = Join-Path $cache ('node-license-' + (& $node -p process.version) + '.txt')
if (!(Test-Path -LiteralPath $nodeLicense)) {
  $tag = & $node -p process.version
  & curl.exe -fLsS ("https://raw.githubusercontent.com/nodejs/node/{0}/LICENSE" -f $tag) -o $nodeLicense
  if ($LASTEXITCODE) { throw 'Cannot download the Node.js license.' }
}
Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $stage 'NODE-LICENSE.txt')

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
  "/out:$stage\EBuild-WebView2.exe" "/win32icon:$stage\src\ui\icon.ico" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
  "/r:$stage\Microsoft.Web.WebView2.Core.dll" "/r:$stage\Microsoft.Web.WebView2.WinForms.dll" `
  (Join-Path $PSScriptRoot 'Host.cs')
if ($LASTEXITCODE) { throw 'Cannot compile the WebView2 host.' }

# Inno Setup uses the system code page for unmarked scripts. Write a UTF-8 BOM for the Chinese strings.
$iss = Join-Path $Output 'prototype-setup.iss'
[IO.File]::WriteAllText($iss, [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'setup.iss')), [Text.UTF8Encoding]::new($true))
& $Iscc /Qp "/DPayloadDir=$stage" "/DBootstrapper=$bootstrapper" "/O$Output" $iss
if ($LASTEXITCODE) { throw 'Cannot build the prototype installer.' }
Write-Output ('Stage: ' + $stage)
