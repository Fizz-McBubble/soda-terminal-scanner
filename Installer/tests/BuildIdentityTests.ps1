[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeArchive,
    [Parameter(Mandatory)][string]$VCRuntimeArchive,
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$RejectedBuildRoot,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build-identity.ps1')
if ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($OutputRoot)) -cne 'F:\' -or (Test-Path -LiteralPath $OutputRoot)) { throw 'fresh_f_test_root_required' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$source = Join-Path $PSScriptRoot '..'
$checks = [Collections.Generic.List[string]]::new()
function Test([string]$Name, [scriptblock]$Action) { & $Action; $checks.Add($Name); Write-Output "PASS $Name" }
function Refuses([scriptblock]$Action, [string]$Prefix) {
    $refused = $false
    try { & $Action } catch { if (-not $_.Exception.Message.StartsWith($Prefix)) { throw }; $refused = $true }
    if (-not $refused) { throw 'expected_identity_rejection' }
}
try {
    $versionMatch = [regex]::Match((Get-Content -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/ScannerConstants.cs') -Raw), 'DisplayVersion = "([^"]+)"')
    $version = $versionMatch.Groups[1].Value
    Test 'source versions align' { Assert-InstallerSourceVersion (Join-Path $source 'Directory.Build.props') $version }
    Test 'new owned product versions enforce decimal carry while major can grow' {
        $props = Join-Path $OutputRoot 'decimal-version.props'
        foreach ($invalid in @('1.2.10', '1.10.0', '01.2.5', '1.2.5+candidate', '1.2')) {
            Refuses { Assert-InstallerSourceVersion (Join-Path $source 'Directory.Build.props') $invalid } 'installer_decimal_version_invalid'
        }
        [IO.File]::WriteAllText($props, '<Project><PropertyGroup><Version>10.0.0</Version><AssemblyVersion>10.0.0.0</AssemblyVersion><FileVersion>10.0.0.0</FileVersion></PropertyGroup></Project>')
        Assert-InstallerSourceVersion $props '10.0.0'
    }
    Test 'delivered 124 catalog is byte exact and remains distinct from 123' {
        $history = Join-Path $source 'src/Soda.Scanner.Core/runtime-files-1.2.4.json'
        if ((Get-FileHash -LiteralPath $history -Algorithm SHA256).Hash.ToLowerInvariant() -cne '67948c905e667289718ca223e995e04b05b83bd04382671caf1fd6f0a02d4892') { throw '124_catalog_not_byte_exact' }
        if ((Get-FileHash -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/runtime-files-1.2.3.json') -Algorithm SHA256).Hash.ToLowerInvariant() -cne '3a3c8f63b6f3e8e92ae71aa638eac2cf71c9098ab228987c60a5a630ca71c6cc') { throw '123_catalog_not_byte_exact' }
    }
    Test 'delivered 125 catalog is byte exact' {
        $history = Join-Path $source 'src/Soda.Scanner.Core/runtime-files-1.2.5.json'
        if ((Get-FileHash -LiteralPath $history -Algorithm SHA256).Hash.ToLowerInvariant() -cne '47cb46b99e85069f80daa77359a84fbf9d3bf315d2911f1f3e0c9793703a8a89') { throw '125_catalog_not_byte_exact' }
    }
    Test 'real current inner and stub align' {
        Assert-InstallerFileVersion (Join-Path $BuildRoot 'Soda-Scanner-Setup-inner.exe') $version
        Assert-InstallerFileVersion (Join-Path $BuildRoot 'Soda-Scanner-Uninstall.exe') $version
    }
    Test 'first mixed build inner and stub are rejected' {
        Refuses { Assert-InstallerFileVersion (Join-Path $RejectedBuildRoot 'Soda-Scanner-Setup-inner.exe') $version } 'installer_file_version_mismatch'
        Refuses { Assert-InstallerFileVersion (Join-Path $RejectedBuildRoot 'Soda-Scanner-Uninstall.exe') $version } 'installer_file_version_mismatch'
    }
    Test 'stale source props are rejected' {
        $oldProps = Join-Path $OutputRoot 'stale.props'
        [IO.File]::WriteAllText($oldProps, '<Project><PropertyGroup><Version>1.2.3</Version><AssemblyVersion>1.2.3.0</AssemblyVersion><FileVersion>1.2.3.0</FileVersion></PropertyGroup></Project>')
        Refuses { Assert-InstallerSourceVersion $oldProps $version } 'installer_source_version_mismatch'
    }
    $catalog = Get-Content -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/runtime-files.json') -Raw | ConvertFrom-Json
    $runtimeVersion = [regex]::Match((Get-Content -LiteralPath (Join-Path $source 'src/Soda.Scanner.Core/ScannerConstants.cs') -Raw), 'LockedRuntimeVersion = "([^"]+)"').Groups[1].Value
    $actual = Read-InstallerPayloadCatalog $RuntimeArchive $VCRuntimeArchive $runtimeVersion
    Test 'real runtime and supplement exactly match current catalog' { Assert-InstallerCatalog (Join-Path $source 'src/Soda.Scanner.Core/runtime-files.json') $actual }
    Test 'real 123 catalog cannot masquerade as current payload' {
        Refuses { Assert-InstallerCatalog (Join-Path $source 'src/Soda.Scanner.Core/runtime-files-1.2.3.json') $actual } 'installer_catalog_file_mismatch'
    }
    Test 'wrong runtime version is rejected' { Refuses { Read-InstallerPayloadCatalog $RuntimeArchive $VCRuntimeArchive 'wrong-version' } 'installer_runtime_descriptor_mismatch' }
    Test 'size hash duplicate and missing file mismatches are rejected' {
        $path = Join-Path $OutputRoot 'invalid-catalog.json'
        foreach ($kind in @('size','hash','duplicate','missing')) {
            $changed = $catalog | ConvertTo-Json -Depth 5 | ConvertFrom-Json
            switch ($kind) {
                size { $changed[0].size++ }
                hash { $changed[0].sha256 = '0' * 64 }
                duplicate { $changed = @($changed) + @($changed[0]) }
                missing { $changed = @($changed | Select-Object -Skip 1) }
            }
            [IO.File]::WriteAllText($path, ($changed | ConvertTo-Json -Depth 5))
            Refuses { Assert-InstallerCatalog $path $actual } 'installer_catalog_'
        }
    }
    $report = @{exitCode=0;checks=@($checks);registryWrites=0;helperStarted=$false;actualInstall=$false;uac=$false}
} catch {
    $report = @{exitCode=1;checks=@($checks);error=$_.Exception.ToString()}
    throw
} finally {
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'build-identity.json'), ($report | ConvertTo-Json -Depth 5))
}
