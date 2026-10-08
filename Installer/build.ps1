[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RuntimeArchive,
    [Parameter(Mandatory=$true)][string]$VCRuntimeArchive,
    [Parameter(Mandatory=$true)][string]$NsisPath,
    [string]$DotNetPath = 'dotnet',
    [string]$OutputRoot = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $source 'outputs' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$dotnet = (Get-Command $DotNetPath -ErrorAction Stop).Source
$nsis = (Get-Command $NsisPath -ErrorAction Stop).Source
$toolchain = Get-Content -LiteralPath (Join-Path $source 'nsis-toolchain.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $nsis -Algorithm SHA256).Hash.ToLowerInvariant() -cne $toolchain.compilerSha256) { throw 'Pinned NSIS compiler does not match.' }
$catalog = Get-Content -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/vc-runtime-input.json') -Raw | ConvertFrom-Json
function Verify-Input([string]$Path, [long]$Size, [string]$Hash) {
    if ((Get-Item -LiteralPath $Path).Length -ne $Size -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Hash) { throw 'Pinned input does not match the source release.' }
}
Verify-Input $RuntimeArchive 133944562 '474855cd91163828c02a992d263c7d9d9ac6169d857dfcc8b8c2250e6c949235'
Verify-Input $VCRuntimeArchive $catalog.size $catalog.sha256
Copy-Item -LiteralPath $VCRuntimeArchive -Destination (Join-Path $source ('src/Soda.Scanner.Core/' + $catalog.fileName)) -Force
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$artifacts = Join-Path $OutputRoot 'artifacts/'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$stubOutput = Join-Path $OutputRoot 'stub'
$setupOutput = Join-Path $OutputRoot 'setup'
& $dotnet publish (Join-Path $source 'src/Soda.Scanner.UninstallStub/Soda.Scanner.UninstallStub.csproj') -c Release -r win-x64 --self-contained true "-p:InstallerArtifactsRoot=$artifacts" -o $stubOutput
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller publish failed.' }
$resources = Join-Path $source 'src/Soda.Scanner.Setup/Resources'
[IO.Directory]::CreateDirectory($resources) | Out-Null
# NSIS stores payload files after the UI in its solid stream. They are not managed
# resources, so opening the first window does not expand them.
$stubPath = Join-Path $stubOutput 'Soda.Scanner.UninstallStub.exe'
$stubMetadata = [ordered]@{ size=(Get-Item -LiteralPath $stubPath).Length; sha256=(Get-FileHash -LiteralPath $stubPath -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $artifacts 'setup-payload.json'), $stubMetadata, [Text.UTF8Encoding]::new($false))
& $dotnet publish (Join-Path $source 'src/Soda.Scanner.Setup/Soda.Scanner.Setup.csproj') -c Release -r win-x64 --self-contained true "-p:InstallerArtifactsRoot=$artifacts" -o $setupOutput
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
$innerSetup = Join-Path $OutputRoot 'Soda-Scanner-Setup-inner.exe'
$finalSetup = Join-Path $OutputRoot 'Soda-Scanner-Setup.exe'
Copy-Item -LiteralPath (Join-Path $setupOutput 'Soda.Scanner.Setup.exe') -Destination $innerSetup -Force
& $nsis /NOCONFIG /INPUTCHARSET UTF8 /V3 "/DINNER_EXE=$innerSetup" "/DOUTPUT_EXE=$finalSetup" "/DSTUB_EXE=$stubPath" "/DRUNTIME_ARCHIVE=$RuntimeArchive" (Join-Path $source 'compress-setup.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Compressed installer publish failed.' }
Copy-Item -LiteralPath (Join-Path $stubOutput 'Soda.Scanner.UninstallStub.exe') -Destination (Join-Path $OutputRoot 'Soda-Scanner-Uninstall.exe') -Force
Get-FileHash -LiteralPath (Join-Path $OutputRoot 'Soda-Scanner-Setup.exe'), (Join-Path $OutputRoot 'Soda-Scanner-Uninstall.exe') -Algorithm SHA256
