[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RuntimeArchive,
    [Parameter(Mandatory=$true)][string]$VCRuntimeArchive,
    [string]$DotNetPath = 'dotnet',
    [string]$OutputRoot = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $source 'outputs' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$dotnet = (Get-Command $DotNetPath -ErrorAction Stop).Source
$catalog = Get-Content -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/vc-runtime-input.json') -Raw | ConvertFrom-Json
function Verify-Input([string]$Path, [long]$Size, [string]$Hash) {
    if ((Get-Item -LiteralPath $Path).Length -ne $Size -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Hash) { throw 'Pinned input does not match the source release.' }
}
Verify-Input $RuntimeArchive 142107129 '722370a83ba93a9618d56c471e87c49d5a7ee8d75bd54e50e82f3f4f19efacc2'
Verify-Input $VCRuntimeArchive $catalog.size $catalog.sha256
Copy-Item -LiteralPath $VCRuntimeArchive -Destination (Join-Path $source ('src/Soda.Scanner.Core/' + $catalog.fileName)) -Force
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$artifacts = Join-Path $OutputRoot 'artifacts/'
$stubOutput = Join-Path $OutputRoot 'stub'
$setupOutput = Join-Path $OutputRoot 'setup'
& $dotnet publish (Join-Path $source 'src/Soda.Scanner.UninstallStub/Soda.Scanner.UninstallStub.csproj') -c Release -r win-x64 --self-contained true "-p:InstallerArtifactsRoot=$artifacts" -o $stubOutput
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller publish failed.' }
$resources = Join-Path $source 'src/Soda.Scanner.Setup/Resources'
[IO.Directory]::CreateDirectory($resources) | Out-Null
Copy-Item -LiteralPath $RuntimeArchive -Destination (Join-Path $resources 'soda-scanner-runtime-18-rc8-3-win-x64.zip') -Force
Copy-Item -LiteralPath (Join-Path $stubOutput 'Soda.Scanner.UninstallStub.exe') -Destination (Join-Path $resources 'Soda-Scanner-Uninstall.exe') -Force
& $dotnet publish (Join-Path $source 'src/Soda.Scanner.Setup/Soda.Scanner.Setup.csproj') -c Release -r win-x64 --self-contained true "-p:InstallerArtifactsRoot=$artifacts" -o $setupOutput
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
Copy-Item -LiteralPath (Join-Path $setupOutput 'Soda.Scanner.Setup.exe') -Destination (Join-Path $OutputRoot 'Soda-Scanner-Setup.exe') -Force
Copy-Item -LiteralPath (Join-Path $stubOutput 'Soda.Scanner.UninstallStub.exe') -Destination (Join-Path $OutputRoot 'Soda-Scanner-Uninstall.exe') -Force
Get-FileHash -LiteralPath (Join-Path $OutputRoot 'Soda-Scanner-Setup.exe'), (Join-Path $OutputRoot 'Soda-Scanner-Uninstall.exe') -Algorithm SHA256
