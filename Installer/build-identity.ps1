# Shared by the build and direct identity tests. Reads archives without launching payloads.
function Assert-InstallerSourceVersion([string]$PropsPath, [string]$DisplayVersion) {
    if ($DisplayVersion -cnotmatch '^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$') { throw 'installer_decimal_version_invalid' }
    [xml]$props = Get-Content -LiteralPath $PropsPath -Raw
    $group = $props.Project.PropertyGroup
    if ($group.Version -cne $DisplayVersion -or $group.AssemblyVersion -cne "$DisplayVersion.0" -or $group.FileVersion -cne "$DisplayVersion.0") {
        throw 'installer_source_version_mismatch'
    }
}

function Assert-InstallerFileVersion([string]$Path, [string]$DisplayVersion) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($info.FileVersion -cne "$DisplayVersion.0" -or ($info.ProductVersion -split '\+')[0] -cne $DisplayVersion) {
        throw "installer_file_version_mismatch:$Path"
    }
}

function Read-InstallerArchive([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $descriptor = $null
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName.Replace('/', '\')
            if ($relative.StartsWith('\') -or $relative.Contains(':') -or ($relative.Split('\') | Where-Object { $_ -in @('', '.', '..') }) -or $files.ContainsKey($relative)) {
                throw 'installer_archive_path_invalid'
            }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            $files.Add($relative, [pscustomobject][ordered]@{path=$relative; size=$entry.Length; sha256=$hash})
            if ($relative -ceq 'scanner-runtime.json') {
                $reader = [IO.StreamReader]::new($entry.Open())
                try { $descriptor = $reader.ReadToEnd() | ConvertFrom-Json }
                finally { $reader.Dispose() }
            }
        }
    } finally { $zip.Dispose() }
    [pscustomobject]@{ Files=$files; Descriptor=$descriptor }
}

function Assert-InstallerFileSet($Expected, $Actual) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Expected) {
        if (-not $seen.Add($entry.path) -or -not $Actual.ContainsKey($entry.path)) { throw 'installer_catalog_path_mismatch' }
        $file = $Actual[$entry.path]
        if ($entry.size -ne $file.size -or $entry.sha256 -cne $file.sha256 -or $entry.path -cne $file.path) {
            throw "installer_catalog_file_mismatch:$($entry.path)"
        }
    }
    if ($seen.Count -ne $Actual.Count) { throw 'installer_catalog_count_mismatch' }
}

function Read-InstallerPayloadCatalog([string]$RuntimeArchive, [string]$VCArchive, [string]$RuntimeVersion) {
    $runtime = Read-InstallerArchive $RuntimeArchive
    $descriptor = $runtime.Descriptor
    if ($null -eq $descriptor -or $descriptor.version -cne $RuntimeVersion -or $descriptor.accountWriteEnabled -cne $false -or $descriptor.importAccess -cne $false) {
        throw 'installer_runtime_descriptor_mismatch'
    }
    $payload = [Collections.Generic.Dictionary[string,object]]::new($runtime.Files, [StringComparer]::OrdinalIgnoreCase)
    if (-not $payload.Remove('scanner-runtime.json')) { throw 'installer_runtime_descriptor_missing' }
    Assert-InstallerFileSet $descriptor.entries $payload
    $supplement = Read-InstallerArchive $VCArchive
    foreach ($entry in $supplement.Files.Values) {
        if ($runtime.Files.ContainsKey($entry.path)) {
            $existing = $runtime.Files[$entry.path]
            if ($existing.size -ne $entry.size -or $existing.sha256 -cne $entry.sha256) { throw 'installer_supplement_collision' }
        }
        $runtime.Files[$entry.path] = $entry
    }
    @($runtime.Files.Values | Sort-Object -Property path -CaseSensitive)
}

function Assert-InstallerCatalog([string]$CatalogPath, $ActualFiles) {
    $actual = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $ActualFiles) { $actual.Add($entry.path, $entry) }
    Assert-InstallerFileSet @(Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json) $actual
}
