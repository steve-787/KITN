#requires -Version 7.4
Set-StrictMode -Version Latest

function Get-KitnVersion {
    param([Parameter(Mandatory)][string]$VersionFile)
    $version = [IO.File]::ReadAllText($VersionFile).Trim()
    if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Invalid version '$version'. Use three numbers, such as 0.1.0."
    }
    foreach ($part in $version.Split('.')) {
        if ([long]$part -gt 65534) { throw 'Version components must be at most 65534 for .NET assembly versions.' }
    }
    return $version
}

function Get-KsaReferenceNames {
    param([Parameter(Mandatory)][string]$ProjectFile)
    $project = [xml][IO.File]::ReadAllText($ProjectFile)
    return @($project.SelectNodes('//Reference[HintPath]') |
        Where-Object { $_.HintPath -like '*$(KSAPath)*' } |
        ForEach-Object { $_.Include + '.dll' } | Sort-Object -Unique)
}

function Write-KitnZip {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][System.Collections.IDictionary]$Files)
    $stream = [IO.File]::Create($Path)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in ($Files.Keys | Sort-Object)) {
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $input = [IO.File]::OpenRead($Files[$name])
                $output = $entry.Open()
                try { $input.CopyTo($output) }
                finally { $input.Dispose(); $output.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

Export-ModuleMember -Function Get-KitnVersion, Get-KsaReferenceNames, Write-KitnZip
