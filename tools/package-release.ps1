#requires -Version 7.4
param(
    [string]$KSAPath = 'K:\Games\Kitten Space Agency',
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/releases'),
    [switch]$ContinuousIntegrationBuild
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$version = Get-KitnVersion (Join-Path $root 'version.txt')
$projectFile = Join-Path $root 'KITN/KITN.csproj'
foreach ($name in (Get-KsaReferenceNames $projectFile)) {
    if (-not (Test-Path -LiteralPath (Join-Path $KSAPath $name) -PathType Leaf)) { throw "Missing build reference $name in $KSAPath." }
}
$commit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Package releases from a Git checkout so their commit is recorded.' }
$buildArguments = @('build', $projectFile, '-c', 'Release', '--nologo', "-p:KSAPath=$KSAPath", "-p:SourceRevisionId=$commit")
if ($ContinuousIntegrationBuild) { $buildArguments += '-p:ContinuousIntegrationBuild=true' }
dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'The KITN release build failed.' }
$project = [xml][IO.File]::ReadAllText($projectFile)
$framework = $project.SelectSingleNode('//TargetFramework').InnerText
$buildOutput = Join-Path $root "KITN/bin/Release/$framework"
$assemblyInfo = [IO.File]::ReadAllText((Join-Path $root "KITN/obj/Release/$framework/KITN.AssemblyInfo.cs"))
if (-not $assemblyInfo.Contains('AssemblyInformationalVersionAttribute("' + $version + '+' + $commit + '")')) {
    throw 'The compiled build information does not match the release version and commit.'
}

# Explicit payload: game, framework, loader and reference DLLs never go into the release.
$files = [ordered]@{
    'KITN/KITN.dll' = (Join-Path $buildOutput 'KITN.dll')
    'KITN/KITN.pdb' = (Join-Path $buildOutput 'KITN.pdb')
    'KITN/KITN.deps.json' = (Join-Path $buildOutput 'KITN.deps.json')
    'KITN/mod.toml' = (Join-Path $buildOutput 'mod.toml')
    'KITN/LICENSE' = (Join-Path $root 'LICENSE')
    'KITN/LUCIDE_LICENSE' = (Join-Path $root 'KITN/Assets/Icons/LICENSE')
    'KITN/THIRD_PARTY_NOTICES.md' = (Join-Path $root 'THIRD_PARTY_NOTICES.md')
    'KITN/README.md' = (Join-Path $root 'README.md')
}
foreach ($path in $files.Values) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release payload file: $path" }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$archivePath = Join-Path $OutputDirectory "KITN-$version.zip"
Write-KitnZip -Path $archivePath -Files $files
& (Join-Path $PSScriptRoot 'verify-release-package.ps1') -ArchivePath $archivePath -Version $version -Commit $commit
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archivePath + '.sha256'), ($checksum + '  ' + [IO.Path]::GetFileName($archivePath) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Release package: $archivePath"
