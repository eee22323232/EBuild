param(
    [Parameter(Mandatory = $true)][string]$BuildOutput,
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$outputRoot = (Resolve-Path -LiteralPath $BuildOutput).Path
$project = Join-Path $PSScriptRoot '../EBuild.Wpf.csproj'
$verification = Join-Path $PSScriptRoot 'EBuild.Wpf.Verification.csproj'
$payload = @('EBuild-WPF.exe', 'EBuild-WPF.exe.config', 'LICENSE')
$exeName = $payload[0]
$baseVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $outputRoot "stage/$exeName")).Version
$nextVersion = '{0}.{1}.{2}' -f $baseVersion.Major, $baseVersion.Minor, ($baseVersion.Build + 1)
$brokenVersion = '{0}.{1}.{2}' -f $baseVersion.Major, $baseVersion.Minor, ($baseVersion.Build + 2)
$baseName = 'EBuild-dev.' + $baseVersion.ToString(3) + '-WPF-Windows-x64'
$assets = Join-Path $outputRoot 'assets'
$installer = Join-Path $assets "$baseName-Setup.exe"
$portable = Join-Path $assets "$baseName.zip"
$manifest = Get-Content -LiteralPath (Join-Path $assets 'WPF-SHA256SUMS.txt')
foreach ($file in @($installer, $portable)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest -notcontains ($hash + '  ' + [IO.Path]::GetFileName($file))) { throw "Invalid release hash for $file" }
}

$appId = '{54A55E1D-1566-44DF-BC1A-B1280E52B7E4}_is1'
$uninstallKeys = @(
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId",
    "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId",
    "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
)
if ($uninstallKeys | Where-Object { Test-Path -LiteralPath $_ }) {
    throw 'Run the installer smoke test in a Windows account with no existing WPF installation.'
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = Join-Path $tempRoot ('EBuild-UpdateVerification-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)

function Wait-Until([string]$description, [scriptblock]$read) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $read
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ($clock.Elapsed.TotalSeconds -lt 55)
    throw "Timed out waiting for $description."
}

function FixtureProcesses([string]$exe) {
    Get-Process -Name EBuild-WPF -ErrorAction SilentlyContinue | Where-Object Path -eq $exe
}

function Close-FixtureLauncher([string]$exe) {
    foreach ($process in @(FixtureProcesses $exe)) {
        if (!$process.HasExited -and (!$process.CloseMainWindow() -or !$process.WaitForExit(6000))) {
            Stop-Process -InputObject $process -Force -ErrorAction SilentlyContinue
            [void]$process.WaitForExit(6000)
        }
    }
}

function Invoke-Probe([string[]]$arguments) {
    & $probe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Update verification failed in $($arguments[0])." }
}

function Apply-Update([string]$exe, [string]$profile, [string]$tag, [version]$expected, [bool]$rollback) {
    $startup = [Diagnostics.ProcessStartInfo]::new($exe)
    $startup.WorkingDirectory = Split-Path -LiteralPath $exe
    $startup.UseShellExecute = $false
    $startup.EnvironmentVariables['EBUILD_CSHARP_PROFILE'] = $profile
    $owner = [Diagnostics.Process]::Start($startup)
    Invoke-Probe @('--confirm-update', [string]$owner.Id)
    $receiptFile = Join-Path $profile 'launcher-updates/result.json'
    $receipt = Wait-Until "receipt for $tag" {
        if (Test-Path -LiteralPath $receiptFile) {
            $current = [IO.File]::ReadAllText($receiptFile) | ConvertFrom-Json
            if ($current.tag -eq $tag) { $current }
        }
    }
    if ([bool]$receipt.success -eq $rollback -or [Reflection.AssemblyName]::GetAssemblyName($exe).Version -ne $expected -or
        ($rollback -and $receipt.failedTag -ne $tag)) { throw "Unexpected result for $tag." }
    if (!$owner.WaitForExit(7000)) { throw 'The original launcher did not close.' }
    [void](Wait-Until 'a responsive restarted launcher' {
        FixtureProcesses $exe | Where-Object { $_.Id -ne $owner.Id -and $_.MainWindowTitle -eq 'EBuild' -and $_.Responding } |
            Select-Object -First 1
    })
    $pending = [IO.File]::ReadAllText((Join-Path $profile 'launcher-updates/pending.json')) | ConvertFrom-Json
    [void](Wait-Until 'the updater finishing cleanup' { !(Test-Path -LiteralPath (Join-Path $profile "launcher-updates/$($pending.id)/package.zip")) })
    Close-FixtureLauncher $exe
}

$passed = $false
try {
    $next = Join-Path $testRoot 'next'
    $tools = Join-Path $testRoot 'tools'
    & $Dotnet build $project -c Release --nologo -o $next "-p:WpfReleaseVersion=$nextVersion"
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the upgrade fixture.' }
    & $Dotnet build $verification -c Release --nologo -o $tools
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the verification executable.' }
    $probe = Join-Path $tools 'EBuild.Wpf.Verification.exe'
    $validZip = Join-Path $testRoot 'next.zip'
    Compress-Archive -LiteralPath @($payload | ForEach-Object { Join-Path $next $_ }) -DestinationPath $validZip

    $broken = Join-Path $testRoot 'broken'
    [void][IO.Directory]::CreateDirectory($broken)
    foreach ($file in $payload | Where-Object { $_ -ne $exeName }) {
        Copy-Item -LiteralPath (Join-Path $next $file) -Destination $broken
        $fixtureMarker = if ($file -like '*.config') { '<!-- broken update fixture -->' } else { 'broken update fixture' }
        [IO.File]::AppendAllText((Join-Path $broken $file), [Environment]::NewLine + $fixtureMarker)
    }
    $stub = Join-Path $broken 'ExitImmediately.cs'
    $source = '[assembly: System.Reflection.AssemblyVersion("' + $brokenVersion + '.0")] ' +
        'internal static class Fixture { static int Main() { return 11; } }'
    [IO.File]::WriteAllText($stub, $source)
    $compiler = Join-Path $env:windir 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $stubExe = Join-Path $broken $exeName
    & $compiler /nologo /platform:x64 /target:winexe "/out:$stubExe" $stub
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the startup failure fixture.' }
    $brokenZip = Join-Path $testRoot 'broken.zip'
    Compress-Archive -LiteralPath @($payload | ForEach-Object { Join-Path $broken $_ }) -DestinationPath $brokenZip

    foreach ($kind in @('installed', 'portable')) {
        $app = Join-Path $testRoot "$kind/app"
        $profile = Join-Path $testRoot "$kind/profile"
        $data = Join-Path $profile 'game-data'
        $exe = Join-Path $app $exeName
        $save = Join-Path $data 'instances/fixture/saves/fixture.dat'
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $save))
        [IO.File]::WriteAllText((Join-Path $data '.ebuild-csharp-data'), 'EBuild C#')
        [IO.File]::WriteAllText($save, 'Update verification world')
        [IO.File]::WriteAllText((Join-Path $data 'instances/fixture/.ebuild.json'), '{"name":"Update fixture","version":"1.20.1","loader":"vanilla"}')
        $settings = Join-Path $profile 'settings.json'
        [IO.File]::WriteAllText($settings, (@{ checkUpdatesOnStart = $false; theme = 'dark'; selected = 'fixture'; dataRoot = $data } |
            ConvertTo-Json -Compress))
        $uninstaller = $null
        try {
            if ($kind -eq 'installed') {
                $log = Join-Path $testRoot 'install.log'
                $flags = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /CURRENTUSER /DIR="{0}" /LOG="{1}"' -f $app, $log
                $setup = Start-Process -FilePath $installer -ArgumentList $flags -WindowStyle Hidden -Wait -PassThru
                $uninstaller = Get-ChildItem -LiteralPath $app -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($setup.ExitCode -ne 0 -or !$uninstaller) { throw "Installer failed with exit code $($setup.ExitCode)." }
            } else { Expand-Archive -LiteralPath $portable -DestinationPath $app }
            if ([Reflection.AssemblyName]::GetAssemblyName($exe).Version -ne $baseVersion) { throw "Wrong initial version in $kind." }
            if ($kind -eq 'installed') {
                Invoke-Probe @('--stage-update', $exe, $profile, $validZip, "dev.$nextVersion", 'checksum')
                Invoke-Probe @('--stage-update', $exe, $profile, $portable, "dev.$nextVersion", 'version')
            }
            Invoke-Probe @('--stage-update', $exe, $profile, $validZip, "dev.$nextVersion", 'accept')
            Apply-Update $exe $profile "dev.$nextVersion" ([version]"$nextVersion.0") $false
            $targetFiles = @($payload | ForEach-Object { Join-Path $app $_ })
            foreach ($name in $payload) {
                if ((Get-FileHash -LiteralPath (Join-Path $next $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $app $name)).Hash) {
                    throw "Upgrade did not replace $name in $kind."
                }
            }
            $previousHashes = @(Get-FileHash -LiteralPath $targetFiles | ForEach-Object Hash)
            Invoke-Probe @('--stage-update', $exe, $profile, $brokenZip, "dev.$brokenVersion", 'accept')
            Apply-Update $exe $profile "dev.$brokenVersion" ([version]"$nextVersion.0") $true
            $restored = @(Get-FileHash -LiteralPath $targetFiles | ForEach-Object Hash)
            $preferences = [IO.File]::ReadAllText($settings) | ConvertFrom-Json
            if (($previousHashes -join ',') -ne ($restored -join ',') -or $preferences.theme -ne 'dark' -or
                $preferences.selected -ne 'fixture' -or [IO.File]::ReadAllText($save) -ne 'Update verification world') {
                throw "Rollback changed a launcher file or player data in $kind."
            }
            Write-Output "PASS: $kind upgrade, failed startup rollback, restart, file hashes and player data."
        } finally {
            Close-FixtureLauncher $exe
            if ($uninstaller -and (Test-Path -LiteralPath $uninstaller.FullName)) {
                $remove = Start-Process -FilePath $uninstaller.FullName -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
                if ($remove.ExitCode -ne 0 -or ($uninstallKeys | Where-Object { Test-Path -LiteralPath $_ })) {
                    throw 'Test uninstallation failed.'
                }
                Write-Output 'PASS: the updated installation still uninstalls.'
            }
        }
    }
    $passed = $true
} finally {
    if ($passed) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).Path
        if ((Split-Path -Parent $resolved) -ne $tempRoot -or (Split-Path -Leaf $resolved) -notmatch '^EBuild-UpdateVerification-[a-f0-9]{32}$') {
            throw 'Unexpected test directory; leaving it in place.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else { Write-Warning "Update verification files: $testRoot" }
}
