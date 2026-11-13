$ErrorActionPreference = 'Stop'
$altitudeWorkspace = 'C:\Users\karst\Desktop\KSAAutopilot'
$altitudeValidation = Join-Path $altitudeWorkspace 'artifacts\validation'
$altitudeMod = (Resolve-Path -LiteralPath 'C:\Users\karst\Documents\My Games\Kitten Space Agency\mods\KSAAutopilot').ProviderPath
$altitudeBuild = Join-Path $altitudeWorkspace 'KSAAutopilot\bin\Release\net9.0'
$altitudeDll = Join-Path $altitudeBuild 'KSAAutopilot.dll'
$altitudeHash = (Get-FileHash -LiteralPath $altitudeDll -Algorithm SHA256).Hash
$altitudeSmokeDll = (Get-Content -LiteralPath (Join-Path $altitudeValidation 'altitude-smoke-dll-path.txt') -Raw).Trim()
$altitudeProbeDirectories = @(
    'C:\Users\karst\AppData\Local\Temp\KSAAutopilotUiProbe-20261112\bin\Release\net9.0',
    [IO.Path]::GetDirectoryName($altitudeSmokeDll)
)
foreach ($altitudeName in @('KSAAutopilot.dll', 'KSAAutopilot.pdb')) {
    $altitudeSourceHash = (Get-FileHash -LiteralPath (Join-Path $altitudeBuild $altitudeName) -Algorithm SHA256).Hash
    foreach ($altitudeProbeDirectory in $altitudeProbeDirectories) {
        if ((Get-FileHash -LiteralPath (Join-Path $altitudeProbeDirectory $altitudeName) -Algorithm SHA256).Hash -ne $altitudeSourceHash) {
            throw "The release differs from the file used in native verification: $altitudeName"
        }
    }
}
$altitudeChecks = @(
    [PSCustomObject]@{ Name='altitude-diagnostics-tests.txt'; Expected=64 },
    [PSCustomObject]@{ Name='altitude-native-ui-tests.txt'; Expected=48 },
    [PSCustomObject]@{ Name='altitude-native-hook-tests.txt'; Expected=5 }
)
foreach ($altitudeCheck in $altitudeChecks) {
    $altitudeCheckPath = Join-Path $altitudeValidation $altitudeCheck.Name
    if (Select-String -LiteralPath $altitudeCheckPath -Pattern '^FAIL|Unhandled exception') {
        throw "Failed checks: $($altitudeCheck.Name)"
    }
    if ((Select-String -LiteralPath $altitudeCheckPath -Pattern '^PASS ').Count -ne $altitudeCheck.Expected) {
        throw "Incomplete checks: $($altitudeCheck.Name)"
    }
}
$altitudeBackup = Join-Path $altitudeMod ('backups\before-mission-altitude-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$altitudeStage = Join-Path $altitudeBackup 'staging'
New-Item -ItemType Directory -Path $altitudeStage | Out-Null
$altitudeRootFiles = @(Get-ChildItem -LiteralPath $altitudeMod -File)
foreach ($altitudeFile in $altitudeRootFiles) {
    Copy-Item -LiteralPath $altitudeFile.FullName -Destination (Join-Path $altitudeBackup $altitudeFile.Name)
}
$altitudePreserved = @()
foreach ($altitudeName in @('flight_presets.json', 'flight_presets.json.bak', 'ui_layout.json', 'ui_layout.json.bak', 'mod.toml')) {
    $altitudePath = Join-Path $altitudeMod $altitudeName
    if (Test-Path -LiteralPath $altitudePath) {
        $altitudePreserved += [PSCustomObject]@{ Name=$altitudeName; BeforeSHA256=(Get-FileHash -LiteralPath $altitudePath -Algorithm SHA256).Hash }
    }
}
$altitudeInstalledFiles = @()
foreach ($altitudeName in @('KSAAutopilot.dll', 'KSAAutopilot.pdb', 'README.md')) {
    $altitudeSource = if ($altitudeName -eq 'README.md') { Join-Path $altitudeWorkspace $altitudeName } else { Join-Path $altitudeBuild $altitudeName }
    $altitudeDestination = Join-Path $altitudeMod $altitudeName
    $altitudeStagedFile = Join-Path $altitudeStage $altitudeName
    $altitudeBeforeHash = if (Test-Path -LiteralPath $altitudeDestination) { (Get-FileHash -LiteralPath $altitudeDestination -Algorithm SHA256).Hash } else { $null }
    Copy-Item -LiteralPath $altitudeSource -Destination $altitudeStagedFile
    if ([IO.Path]::GetPathRoot($altitudeStagedFile) -ne [IO.Path]::GetPathRoot($altitudeDestination)) {
        throw 'Atomic replacement requires the same volume.'
    }
    if (Test-Path -LiteralPath $altitudeDestination) {
        [IO.File]::Replace($altitudeStagedFile, $altitudeDestination, (Join-Path $altitudeBackup ('replaced-' + $altitudeName)))
    } else {
        [IO.File]::Move($altitudeStagedFile, $altitudeDestination)
    }
    $altitudeInstalledHash = (Get-FileHash -LiteralPath $altitudeDestination -Algorithm SHA256).Hash
    $altitudeSourceHash = (Get-FileHash -LiteralPath $altitudeSource -Algorithm SHA256).Hash
    if ($altitudeInstalledHash -ne $altitudeSourceHash) { throw "Installation hash mismatch: $altitudeName" }
    $altitudeInstalledFiles += [PSCustomObject]@{ Name=$altitudeName; BeforeSHA256=$altitudeBeforeHash; InstalledSHA256=$altitudeInstalledHash; Verified=$true }
}
foreach ($altitudeFile in $altitudePreserved) {
    $altitudeFile | Add-Member -NotePropertyName AfterSHA256 -NotePropertyValue ((Get-FileHash -LiteralPath (Join-Path $altitudeMod $altitudeFile.Name) -Algorithm SHA256).Hash)
    $altitudeFile | Add-Member -NotePropertyName Unchanged -NotePropertyValue ($altitudeFile.BeforeSHA256 -eq $altitudeFile.AfterSHA256)
}
# Real logs may continue changing while the game runs. Snapshot them without
# initializing diagnostics or asserting that their live hashes stay constant.
$altitudeFinalLogs = Join-Path $altitudeBackup 'logs-after-install'
New-Item -ItemType Directory -Path $altitudeFinalLogs | Out-Null
foreach ($altitudeFile in @(Get-ChildItem -LiteralPath $altitudeMod -File | Where-Object { $_.Name -match '^ksa_.*\.(log|csv)(\.\d+)?$' })) {
    Copy-Item -LiteralPath $altitudeFile.FullName -Destination (Join-Path $altitudeFinalLogs $altitudeFile.Name)
}
$altitudeReport = [PSCustomObject]@{
    InstalledAt=(Get-Date).ToString('o')
    ModDirectory=$altitudeMod
    BackupDirectory=$altitudeBackup
    Files=$altitudeInstalledFiles
    PreservedFiles=@($altitudeRootFiles.Name | Where-Object { $_ -notin @('KSAAutopilot.dll', 'KSAAutopilot.pdb', 'README.md') })
    PreservedConfiguration=$altitudePreserved
    LatestLogSnapshotDirectory=$altitudeFinalLogs
    UnitTestsPassed=64
    NativeUIChecksPassed=48
    NativeHookChecksPassed=5
    VerifiedNativeDllSHA256=$altitudeHash
    BuildWarnings=0
    BuildErrors=0
    LiveVulkanRenderingTested=$false
    GameRestartedByInstaller=$false
}
$altitudeReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $altitudeValidation 'altitude-installation.json') -Encoding utf8
$altitudeReport | ConvertTo-Json -Depth 5
