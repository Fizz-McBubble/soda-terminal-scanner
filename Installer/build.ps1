[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RuntimeArchive,
    [Parameter(Mandatory=$true)][string]$VCRuntimeArchive,
    [Parameter(Mandatory=$true)][string]$NsisPath,
    [string]$DotNetPath = 'dotnet',
    [string]$OutputRoot = '',
    [string]$TemporaryDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$RuntimeArchive = [IO.Path]::GetFullPath($RuntimeArchive)
$VCRuntimeArchive = [IO.Path]::GetFullPath($VCRuntimeArchive)
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { Join-Path $source 'outputs' }
if (-not $OutputRoot) {
    if ($env:InstallerArtifactsRoot) { $OutputRoot = $env:InstallerArtifactsRoot }
    else {
        $OutputRoot = Join-Path $derivedRoot 'scanner-installer'
    }
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$dotnet = (Get-Command $DotNetPath -ErrorAction Stop).Source
$nsis = (Get-Command $NsisPath -ErrorAction Stop).Source
$toolchain = Get-Content -LiteralPath (Join-Path $source 'nsis-toolchain.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $nsis -Algorithm SHA256).Hash.ToLowerInvariant() -cne $toolchain.compilerSha256) { throw 'Pinned NSIS compiler does not match.' }
$catalog = Get-Content -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/vc-runtime-input.json') -Raw | ConvertFrom-Json
function Verify-Input([string]$Path, [long]$Size, [string]$Hash) {
    if ((Get-Item -LiteralPath $Path).Length -ne $Size -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Hash) { throw 'Pinned input does not match the source release.' }
}
$constants = Get-Content -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/ScannerConstants.cs') -Raw
function Locked-String([string]$Name) {
    $match = [regex]::Match($constants, ('public const string ' + [regex]::Escape($Name) + ' = "([^"]+)";'))
    if (-not $match.Success) { throw ('Missing locked installer identity: ' + $Name) }
    $match.Groups[1].Value
}
$lockedSize = [regex]::Match($constants, 'public const long LockedAssetSize = (\d+)L;')
if (-not $lockedSize.Success) { throw 'Missing locked runtime size.' }
Verify-Input $RuntimeArchive ([long]$lockedSize.Groups[1].Value) (Locked-String 'LockedAssetSha256')
$runtimeAssetName = Locked-String 'LockedAssetName'
$installerVersion = Locked-String 'DisplayVersion'
Verify-Input $VCRuntimeArchive $catalog.size $catalog.sha256
if (-not $TemporaryDirectory) { $TemporaryDirectory = if ($env:SODA_BUILD_TEMP_DIRECTORY) { $env:SODA_BUILD_TEMP_DIRECTORY } else { Join-Path $derivedRoot 'temp\scanner-installer' } }
$TemporaryDirectory = [IO.Path]::GetFullPath($TemporaryDirectory)
$buildEnvironmentBefore = @{}
foreach ($name in @('TEMP', 'TMP', 'DOTNET_CLI_HOME', 'DOTNET_GENERATE_ASPNET_CERTIFICATE')) { $buildEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    [IO.Directory]::CreateDirectory($TemporaryDirectory) | Out-Null
    $env:TEMP = $TemporaryDirectory
    $env:TMP = $TemporaryDirectory
    if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $derivedRoot 'dotnet\cli-home' }
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $artifacts = Join-Path $OutputRoot 'artifacts/'
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    $stubOutput = Join-Path $OutputRoot 'stub'
    $setupOutput = Join-Path $OutputRoot 'setup'
    & $dotnet publish (Join-Path $source 'src/Soda.Scanner.UninstallStub/Soda.Scanner.UninstallStub.csproj') -c Release -r win-x64 --self-contained true "-p:InstallerArtifactsRoot=$artifacts" "-p:VCRuntimeArchivePath=$VCRuntimeArchive" -o $stubOutput
    if ($LASTEXITCODE -ne 0) { throw 'Uninstaller publish failed.' }
    $resources = Join-Path $source 'src/Soda.Scanner.Setup/Resources'
    [IO.Directory]::CreateDirectory($resources) | Out-Null
    # NSIS stores payload files after the UI in its solid stream. They are not managed
    # resources, so opening the first window does not expand them.
    $stubPath = Join-Path $stubOutput 'Soda.Scanner.UninstallStub.exe'
    $stubMetadata = [ordered]@{ size=(Get-Item -LiteralPath $stubPath).Length; sha256=(Get-FileHash -LiteralPath $stubPath -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $artifacts 'setup-payload.json'), $stubMetadata, [Text.UTF8Encoding]::new($false))
    & $dotnet publish (Join-Path $source 'src/Soda.Scanner.Setup/Soda.Scanner.Setup.csproj') -c Release -r win-x64 --self-contained true "-p:InstallerArtifactsRoot=$artifacts" "-p:VCRuntimeArchivePath=$VCRuntimeArchive" -o $setupOutput
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
    $innerSetup = Join-Path $OutputRoot 'Soda-Scanner-Setup-inner.exe'
    $finalSetup = Join-Path $OutputRoot "SodaTerminal-Scanner-Setup-$installerVersion.exe"
    Copy-Item -LiteralPath (Join-Path $setupOutput 'Soda.Scanner.Setup.exe') -Destination $innerSetup -Force
    & $nsis /NOCONFIG /INPUTCHARSET UTF8 /V3 "/DINNER_EXE=$innerSetup" "/DOUTPUT_EXE=$finalSetup" "/DSTUB_EXE=$stubPath" "/DRUNTIME_ARCHIVE=$RuntimeArchive" "/DRUNTIME_ASSET_NAME=$runtimeAssetName" "/DINSTALLER_VERSION=$installerVersion" (Join-Path $source 'compress-setup.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'Compressed installer publish failed.' }
    Copy-Item -LiteralPath (Join-Path $stubOutput 'Soda.Scanner.UninstallStub.exe') -Destination (Join-Path $OutputRoot 'Soda-Scanner-Uninstall.exe') -Force
    Get-FileHash -LiteralPath $finalSetup, (Join-Path $OutputRoot 'Soda-Scanner-Uninstall.exe') -Algorithm SHA256
} finally {
    foreach ($name in $buildEnvironmentBefore.Keys) {
        if ($null -eq $buildEnvironmentBefore[$name]) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process') }
        else { [Environment]::SetEnvironmentVariable($name, $buildEnvironmentBefore[$name], 'Process') }
    }
}
