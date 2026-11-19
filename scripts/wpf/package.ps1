param(
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][string]$Iscc,
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$outputDirectory = [IO.Path]::GetFullPath($Output)
$project = Join-Path $PSScriptRoot 'EBuild.Wpf.csproj'
$stage = Join-Path $outputDirectory 'stage'
& $Dotnet build $project -c Release -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'WPF build failed.' }

$launcher = Join-Path $stage 'EBuild-WPF.exe'
$version = [Reflection.AssemblyName]::GetAssemblyName($launcher).Version.ToString(3)
$assets = Join-Path $outputDirectory 'assets'
[void][IO.Directory]::CreateDirectory($assets)
$baseName = "EBuild-dev.$version-WPF-Windows-x64"
$zip = Join-Path $assets "$baseName.zip"
$installer = Join-Path $assets "$baseName-Setup.exe"
$files = @($launcher, "$launcher.config", (Join-Path $stage 'LICENSE'))
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force

$setup = Join-Path $PSScriptRoot 'setup.iss'
# Windows PowerShell and Inno Setup both need a BOM to reliably read the Chinese strings.
$iss = Join-Path $outputDirectory 'setup.iss'
$icon = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\webview2-trial\ui\icon.ico'))
$source = [IO.File]::ReadAllText($setup).Replace('..\webview2-trial\ui\icon.ico', $icon)
[IO.File]::WriteAllText($iss, $source, [Text.UTF8Encoding]::new($true))
& $Iscc /Qp "/DPayloadDir=$stage" "/DReleaseVersion=$version" "/O$assets" $iss
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'WPF installer build failed.' }

@($zip, $installer) | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_)
} | Set-Content -LiteralPath (Join-Path $assets 'WPF-SHA256SUMS.txt') -Encoding ASCII
Get-ChildItem -LiteralPath $assets -File
