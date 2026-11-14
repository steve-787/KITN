#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$versionFile = Join-Path ([IO.Path]::GetTempPath()) ('KITN-version-test-' + [Guid]::NewGuid().ToString('N') + '.txt')
try {
    foreach ($case in @(
        @('0.1.0', 'current', '0.1.0'), @('0.1.0', 'patch', '0.1.1'),
        @('0.1.7', 'minor', '0.2.0'), @('0.9.8', 'major', '1.0.0'),
        @('1.9.9', 'patch', '1.9.10'), @('2.9.7', 'minor', '2.10.0'),
        @('9.8.7', 'major', '10.0.0')
    )) {
        [IO.File]::WriteAllText($versionFile, $case[0] + "`n")
        $timestamp = (Get-Item -LiteralPath $versionFile).LastWriteTimeUtc
        $result = & (Join-Path $root 'tools/bump-version.ps1') -VersionFile $versionFile -Bump $case[1]
        if ($result -ne $case[2] -or [IO.File]::ReadAllText($versionFile).Trim() -ne $case[2]) {
            throw "Incorrect $($case[1]) bump from $($case[0])."
        }
        if ($case[1] -eq 'current' -and (Get-Item -LiteralPath $versionFile).LastWriteTimeUtc -ne $timestamp) {
            throw 'Publishing the current version must leave the version file unchanged.'
        }
    }
    foreach ($invalid in @('v0.1.0', '01.2.3', '1.2', '1.2.3.4', '1.2.3-beta', '65535.0.0', 'text')) {
        [IO.File]::WriteAllText($versionFile, $invalid)
        $rejected = $false
        try { & (Join-Path $root 'tools/bump-version.ps1') -VersionFile $versionFile -Bump patch | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected -or [IO.File]::ReadAllText($versionFile) -ne $invalid) { throw "Invalid version changed the file: $invalid" }
    }
    [IO.File]::WriteAllText($versionFile, '0.0.65534')
    $rejected = $false
    try { & (Join-Path $root 'tools/bump-version.ps1') -VersionFile $versionFile -Bump patch | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText($versionFile) -ne '0.0.65534') { throw 'An overflowing bump must preserve the version file.' }
    Write-Host 'PASS current/patch/minor/major versions, component resets, invalid versions and overflow preserve the right files.'
}
finally { if (Test-Path -LiteralPath $versionFile) { Remove-Item -LiteralPath $versionFile -Force } }
