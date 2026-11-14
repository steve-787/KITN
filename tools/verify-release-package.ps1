#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$Version,
    [string]$Commit
)
$ErrorActionPreference = 'Stop'
$expected = @('KITN/KITN.dll', 'KITN/KITN.pdb', 'KITN/KITN.deps.json', 'KITN/mod.toml',
    'KITN/LICENSE', 'KITN/LUCIDE_LICENSE', 'KITN/THIRD_PARTY_NOTICES.md', 'KITN/README.md')
$zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    if (@(Compare-Object ($expected | Sort-Object) ($zip.Entries.FullName | Sort-Object)).Count) {
        throw 'The release ZIP must contain exactly the KITN payload in one KITN/ folder, with portable forward-slash paths.'
    }
    foreach ($entry in $zip.Entries) { if ($entry.Length -eq 0) { throw "Empty release payload: $($entry.FullName)" } }
    $reader = [IO.StreamReader]::new($zip.GetEntry('KITN/mod.toml').Open())
    try { $toml = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if ($toml -notmatch '(?m)^name\s*=\s*"KITN"\s*$' -or
        $toml -notmatch '(?ms)^\[StarMap\]\s+EntryAssembly\s*=\s*"KITN"\s*$') {
        throw 'The mod descriptor must declare KITN and its StarMap entry assembly.'
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('KITN/KITN.deps.json').Open())
    try { $deps = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
    finally { $reader.Dispose() }
    if ($deps.libraries.Count -ne 1 -or -not $deps.libraries.ContainsKey("KITN/$Version")) {
        throw 'The mod dependency metadata must match its version and use game/loader-provided dependencies.'
    }
    $temporaryDll = Join-Path ([IO.Path]::GetTempPath()) ('KITN-release-' + [Guid]::NewGuid().ToString('N') + '.dll')
    try {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('KITN/KITN.dll'), $temporaryDll, $false)
        if ([Reflection.AssemblyName]::GetAssemblyName($temporaryDll).Version.ToString(3) -ne $Version) {
            throw 'The packaged DLL version does not match the release.'
        }
        if ($Commit -and -not [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($temporaryDll)).Contains($Version + '+' + $Commit)) {
            throw 'The packaged DLL does not contain its release version and commit.'
        }
    }
    finally { if (Test-Path -LiteralPath $temporaryDll) { Remove-Item -LiteralPath $temporaryDll -Force } }
}
finally { $zip.Dispose() }
Write-Host "Verified StarMap/Borea package for KITN $Version."
