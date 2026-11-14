#requires -Version 7.4
param([Parameter(Mandatory)][string]$ArchivePath, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$metadata = Get-Content -LiteralPath (Join-Path $root 'build/ksa-references.json') -Raw | ConvertFrom-Json
if ($metadata.format -ne 1) { throw 'Unsupported KSA reference metadata format.' }
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $metadata.sha256) {
    throw 'The KSA reference archive does not match its pinned checksum.'
}

if ((Test-Path -LiteralPath $Destination) -and @(Get-ChildItem -LiteralPath $Destination -Force).Count) {
    throw 'Restore KSA references into an empty directory.'
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$names = Get-KsaReferenceNames (Join-Path $root 'KITN/KITN.csproj')
$stream = [IO.File]::OpenRead($ArchivePath)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $expected = @($names) + 'references.json'
        if (@(Compare-Object ($expected | Sort-Object) ($zip.Entries.FullName | Sort-Object)).Count) {
            throw 'The KSA reference archive must contain exactly the DLLs referenced by KITN and its manifest.'
        }
        $reader = [IO.StreamReader]::new($zip.GetEntry('references.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($manifest.format -ne 1 -or $manifest.gameVersion -ne $metadata.gameVersion -or
            $names.Count -ne $metadata.referenceCount) { throw 'KSA reference manifest mismatch.' }
        foreach ($name in $names) {
            $path = Join-Path $Destination $name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($name), $path, $false)
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.files.$name) {
                throw "Checksum mismatch for KSA reference $name."
            }
        }
        if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $Destination 'KSA.dll')).Version.ToString() -ne $metadata.gameVersion) {
            throw 'The KSA reference assembly version does not match its manifest.'
        }
    }
    finally { $zip.Dispose() }
}
finally { $stream.Dispose() }
Write-Host "Restored $($names.Count) compile-only references for KSA $($metadata.gameVersion)."
