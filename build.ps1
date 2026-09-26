[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [Parameter(Mandatory = $true)][string]$ArtifactsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$artifacts = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd('\')
if ($artifacts.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $artifacts.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build outputs must be outside the source directory.'
}

foreach ($item in @(
    @{ Name = 'scanner'; Project = 'ZZZ-Scanner.Next.csproj' },
    @{ Name = 'helper'; Project = 'Launcher/ZZZ-Scanner.Helper.csproj' },
    @{ Name = 'ocr'; Project = 'OcrCli/ScannerPpOcrV6.csproj' }
)) {
    $obj = Join-Path $artifacts "$($item.Name)/obj/"
    $bin = Join-Path $artifacts "$($item.Name)/bin/"
    [IO.Directory]::CreateDirectory($obj) | Out-Null
    [IO.Directory]::CreateDirectory($bin) | Out-Null
    & $Dotnet build (Join-Path $source $item.Project) -c Release -r win-x64 --self-contained false `
        "-p:BaseIntermediateOutputPath=$obj" "-p:OutputPath=$bin" '-p:PublishAot=false'
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($item.Project) ($LASTEXITCODE)" }
}
