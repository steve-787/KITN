#requires -Version 7.4
param([string]$KSAPath = 'K:\Games\Kitten Space Agency')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$KSAPath = [IO.Path]::GetFullPath($KSAPath)
$names = Get-KsaReferenceNames (Join-Path $root 'KITN/KITN.csproj')
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $KSAPath $name) -PathType Leaf)) { throw "Missing game reference: $name in $KSAPath" }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('KITN-KSARefs-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore Refasmer.' }
    $refs = Join-Path $scratch 'refs'
    New-Item -ItemType Directory -Path $refs | Out-Null
    $inputs = @($names | ForEach-Object { Join-Path $KSAPath $_ })
    # Preserve type/layout metadata and strip executable method bodies.
    dotnet tool run refasmer -- --all --outputdir $refs @inputs
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the KSA reference assemblies.' }
    $files = [ordered]@{}
    $hashes = [ordered]@{}
    foreach ($name in $names) {
        $path = Join-Path $refs $name
        $files[$name] = $path
        $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $gameVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $KSAPath 'KSA.dll')).Version.ToString()
    $manifest = [ordered]@{ format = 1; gameVersion = $gameVersion; files = $hashes }
    $manifestPath = Join-Path $scratch 'references.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $files['references.json'] = $manifestPath
    $output = Join-Path $root '.local'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $archivePath = Join-Path $output 'ksa-references.zip'
    Write-KitnZip -Path $archivePath -Files $files
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $metadata = [ordered]@{
        format = 1
        gameVersion = $gameVersion
        refasmerVersion = '2.0.3'
        sha256 = $checksum
        referenceCount = $names.Count
        ref = "ksa-$gameVersion-$($checksum.Substring(0, 12))"
    }
    $lockDirectory = Join-Path $root 'build'
    New-Item -ItemType Directory -Path $lockDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $lockDirectory 'ksa-references.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host ("Created compile-only references for KSA {0}: {1:N2} MiB. Archive: {2}" -f $gameVersion, ((Get-Item $archivePath).Length / 1MB), $archivePath)
}
finally {
    Pop-Location
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $scratchPath = [IO.Path]::GetFullPath($scratch)
    if (-not $scratchPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($scratchPath) -notlike 'KITN-KSARefs-*') { throw 'Refusing to remove an unexpected temporary path.' }
    Remove-Item -LiteralPath $scratchPath -Recurse -Force
}
