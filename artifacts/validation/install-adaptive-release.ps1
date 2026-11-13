$ErrorActionPreference = 'Stop'
$adaptiveWorkspace = 'C:\Users\karst\Desktop\KSAAutopilot'
$adaptiveMod = (Resolve-Path -LiteralPath 'C:\Users\karst\Documents\My Games\Kitten Space Agency\mods\KSAAutopilot').ProviderPath
$adaptiveBuild = Join-Path $adaptiveWorkspace 'KSAAutopilot\bin\Release\net9.0'
$adaptiveDll = Join-Path $adaptiveBuild 'KSAAutopilot.dll'
$adaptiveHash = (Get-FileHash -LiteralPath $adaptiveDll -Algorithm SHA256).Hash
$adaptiveSmokeDll = (Get-Content -LiteralPath 'C:\Users\karst\AppData\Local\Temp\KSAAutopilotApiProbe-20261112\last-smoke-path.txt' -Raw).Trim()
foreach ($adaptiveVerifiedDll in @('C:\Users\karst\AppData\Local\Temp\KSAAutopilotUiProbe-20261112\bin\Release\net9.0\KSAAutopilot.dll', $adaptiveSmokeDll)) {
    if ((Get-FileHash -LiteralPath $adaptiveVerifiedDll -Algorithm SHA256).Hash -ne $adaptiveHash) {
        throw 'The release differs from the DLL used in native verification.'
    }
}
foreach ($adaptiveCheck in @('adaptive-diagnostics-tests.txt', 'adaptive-native-ui-tests.txt', 'adaptive-native-hook-tests.txt')) {
    $adaptiveCheckPath = Join-Path $adaptiveWorkspace ('artifacts\validation\' + $adaptiveCheck)
    if (Select-String -LiteralPath $adaptiveCheckPath -Pattern '^FAIL|Unhandled exception') { throw "Failed checks: $adaptiveCheck" }
}
$adaptiveBackup = Join-Path $adaptiveMod ('backups\before-rocket-adaptation-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$adaptiveStage = Join-Path $adaptiveBackup 'staging'
New-Item -ItemType Directory -Path $adaptiveStage | Out-Null
$adaptiveRootFiles = @(Get-ChildItem -LiteralPath $adaptiveMod -File)
foreach ($adaptiveFile in $adaptiveRootFiles) {
    Copy-Item -LiteralPath $adaptiveFile.FullName -Destination (Join-Path $adaptiveBackup $adaptiveFile.Name)
}
$adaptivePreserved = @()
foreach ($adaptiveName in @('flight_presets.json', 'flight_presets.json.bak', 'ui_layout.json', 'ui_layout.json.bak', 'mod.toml')) {
    $adaptivePath = Join-Path $adaptiveMod $adaptiveName
    if (Test-Path -LiteralPath $adaptivePath) {
        $adaptivePreserved += [PSCustomObject]@{Name=$adaptiveName;BeforeSHA256=(Get-FileHash -LiteralPath $adaptivePath -Algorithm SHA256).Hash}
    }
}
$adaptiveInstalledFiles = @()
foreach ($adaptiveName in @('KSAAutopilot.dll', 'KSAAutopilot.pdb', 'README.md')) {
    $adaptiveSource = if ($adaptiveName -eq 'README.md') { Join-Path $adaptiveWorkspace $adaptiveName } else { Join-Path $adaptiveBuild $adaptiveName }
    $adaptiveDestination = Join-Path $adaptiveMod $adaptiveName
    $adaptiveStagedFile = Join-Path $adaptiveStage $adaptiveName
    $adaptiveBeforeHash = if (Test-Path -LiteralPath $adaptiveDestination) { (Get-FileHash -LiteralPath $adaptiveDestination -Algorithm SHA256).Hash } else { $null }
    Copy-Item -LiteralPath $adaptiveSource -Destination $adaptiveStagedFile
    if ([IO.Path]::GetPathRoot($adaptiveStagedFile) -ne [IO.Path]::GetPathRoot($adaptiveDestination)) { throw 'Atomic replacement requires the same volume.' }
    if (Test-Path -LiteralPath $adaptiveDestination) {
        [IO.File]::Replace($adaptiveStagedFile, $adaptiveDestination, (Join-Path $adaptiveBackup ('replaced-' + $adaptiveName)))
    } else {
        [IO.File]::Move($adaptiveStagedFile, $adaptiveDestination)
    }
    $adaptiveInstalledHash = (Get-FileHash -LiteralPath $adaptiveDestination -Algorithm SHA256).Hash
    $adaptiveSourceHash = (Get-FileHash -LiteralPath $adaptiveSource -Algorithm SHA256).Hash
    if ($adaptiveInstalledHash -ne $adaptiveSourceHash) { throw "Installation hash mismatch: $adaptiveName" }
    $adaptiveInstalledFiles += [PSCustomObject]@{Name=$adaptiveName;BeforeSHA256=$adaptiveBeforeHash;InstalledSHA256=$adaptiveInstalledHash;Verified=$true}
}
foreach ($adaptiveFile in $adaptivePreserved) {
    $adaptiveFile | Add-Member -NotePropertyName AfterSHA256 -NotePropertyValue ((Get-FileHash -LiteralPath (Join-Path $adaptiveMod $adaptiveFile.Name) -Algorithm SHA256).Hash)
    $adaptiveFile | Add-Member -NotePropertyName Unchanged -NotePropertyValue ($adaptiveFile.BeforeSHA256 -eq $adaptiveFile.AfterSHA256)
}
$adaptiveFinalLogs = Join-Path $adaptiveBackup 'logs-after-install'
New-Item -ItemType Directory -Path $adaptiveFinalLogs | Out-Null
foreach ($adaptiveFile in @(Get-ChildItem -LiteralPath $adaptiveMod -File | Where-Object { $_.Name -match '^ksa_.*\.(log|csv)(\.\d+)?$' })) {
    Copy-Item -LiteralPath $adaptiveFile.FullName -Destination (Join-Path $adaptiveFinalLogs $adaptiveFile.Name)
}
$adaptiveReport = [PSCustomObject]@{
    InstalledAt=(Get-Date).ToString('o')
    ModDirectory=$adaptiveMod
    BackupDirectory=$adaptiveBackup
    Files=$adaptiveInstalledFiles
    PreservedFiles=@($adaptiveRootFiles.Name | Where-Object { $_ -notin @('KSAAutopilot.dll', 'KSAAutopilot.pdb', 'README.md') })
    PreservedConfiguration=$adaptivePreserved
    LatestLogSnapshotDirectory=$adaptiveFinalLogs
    UnitTestsPassed=(Select-String -LiteralPath (Join-Path $adaptiveWorkspace 'artifacts\validation\adaptive-diagnostics-tests.txt') -Pattern '^PASS ').Count
    NativeUIChecksPassed=(Select-String -LiteralPath (Join-Path $adaptiveWorkspace 'artifacts\validation\adaptive-native-ui-tests.txt') -Pattern '^PASS ').Count
    NativeHookChecksPassed=(Select-String -LiteralPath (Join-Path $adaptiveWorkspace 'artifacts\validation\adaptive-native-hook-tests.txt') -Pattern '^PASS ').Count
    VerifiedNativeDllSHA256=$adaptiveHash
    BuildWarnings=0
    BuildErrors=0
    GameRestartedByInstaller=$false
}
$adaptiveReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $adaptiveWorkspace 'artifacts\validation\adaptive-installation.json') -Encoding utf8
$adaptiveReport | ConvertTo-Json -Depth 5
