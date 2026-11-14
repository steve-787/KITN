#requires -Version 7.4
param(
    [ValidateSet('current', 'patch', 'minor', 'major')][string]$Bump = 'patch',
    [string]$VersionFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'version.txt')
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force

$current = Get-KitnVersion $VersionFile
$parts = @($current.Split('.') | ForEach-Object { [int]$_ })
switch ($Bump) {
    'patch' { $parts[2]++ }
    'minor' { $parts[1]++; $parts[2] = 0 }
    'major' { $parts[0]++; $parts[1] = 0; $parts[2] = 0 }
}
if (($parts | Where-Object { $_ -gt 65534 }).Count) { throw 'The bumped version exceeds .NET assembly version limits.' }
$next = $parts -join '.'
if ($next -ne $current) { [IO.File]::WriteAllText($VersionFile, $next + "`n", [Text.UTF8Encoding]::new($false)) }
$next
