# KITN - Installer fuer Kitten Space Agency (nur ASCII-Zeichen, damit PowerShell 5.1 es korrekt liest)

param(
    [string]$KSAPath = "K:\Games\Kitten Space Agency",
    [string]$ModsPath = "$env:USERPROFILE\Documents\My Games\Kitten Space Agency\mods"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  KITN - Mod Installation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Projektdatei suchen (Unterordner oder gleicher Ordner)
$csproj = Join-Path $root "KITN\KITN.csproj"
if (-not (Test-Path $csproj)) { $csproj = Join-Path $root "KITN.csproj" }
if (-not (Test-Path $csproj)) {
    Write-Host "KITN.csproj nicht gefunden. Skript im Projektordner ausfuehren." -ForegroundColor Red
    exit 1
}
$projDir = Split-Path $csproj -Parent

# 1. Build
Write-Host "[1/4] Baue KITN..." -ForegroundColor Yellow
dotnet build "$csproj" -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Fehler beim Buildvorgang!" -ForegroundColor Red
    exit 1
}
Write-Host "Build erfolgreich!" -ForegroundColor Green

# 2. Mod-Ordner erstellen und Dateien kopieren
$targetModDir = Join-Path $ModsPath "KITN"
Write-Host "[2/4] Kopiere Mod-Dateien nach $targetModDir ..." -ForegroundColor Yellow
if (-not (Test-Path $targetModDir)) {
    New-Item -ItemType Directory -Path $targetModDir -Force | Out-Null
}

$dllSource = Join-Path $projDir "bin\Release\net9.0\KITN.dll"
$pdbSource = Join-Path $projDir "bin\Release\net9.0\KITN.pdb"
$tomlSource = Join-Path $projDir "mod.toml"
if (-not (Test-Path $tomlSource)) { $tomlSource = Join-Path $root "mod.toml" }

Copy-Item -Path $dllSource -Destination (Join-Path $targetModDir "KITN.dll") -Force
if (Test-Path -LiteralPath $pdbSource) {
    Copy-Item -LiteralPath $pdbSource -Destination (Join-Path $targetModDir "KITN.pdb") -Force
}
Copy-Item -Path $tomlSource -Destination (Join-Path $targetModDir "mod.toml") -Force
$noticesSource = Join-Path $projDir "bin\Release\net9.0\THIRD_PARTY_NOTICES.md"
if (Test-Path -LiteralPath $noticesSource) {
    Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $targetModDir "THIRD_PARTY_NOTICES.md") -Force
}
$iconsLicenseSource = Join-Path $projDir "Assets\Icons\LICENSE"
if (Test-Path -LiteralPath $iconsLicenseSource) {
    Copy-Item -LiteralPath $iconsLicenseSource -Destination (Join-Path $targetModDir "LUCIDE_LICENSE") -Force
}
Write-Host "KITN.dll, Debug-Symbole und mod.toml erfolgreich kopiert." -ForegroundColor Green

# 3. In manifest.toml eintragen
$manifestPath = "$env:USERPROFILE\Documents\My Games\Kitten Space Agency\manifest.toml"
Write-Host "[3/4] Registriere Mod in manifest.toml ..." -ForegroundColor Yellow

if (Test-Path $manifestPath) {
    $manifestContent = Get-Content $manifestPath -Raw
    if ($manifestContent -notmatch 'id\s*=\s*"KITN"') {
        $entry = "`n[[mods]]`nid = `"KITN`"`nenabled = true`n"
        Add-Content -Path $manifestPath -Value $entry
        Write-Host "Mod zu manifest.toml hinzugefuegt." -ForegroundColor Green
    } else {
        Write-Host "Mod ist bereits in manifest.toml registriert." -ForegroundColor Gray
    }
} else {
    $entry = "[[mods]]`nid = `"Core`"`nenabled = true`n`n[[mods]]`nid = `"KITN`"`nenabled = true`n"
    Set-Content -Path $manifestPath -Value $entry
    Write-Host "Neue manifest.toml erstellt." -ForegroundColor Green
}

# 4. StarMap Loader pruefen
Write-Host "[4/4] Pruefe StarMap Loader in $KSAPath ..." -ForegroundColor Yellow
$starMapExe = Join-Path $KSAPath "StarMap.exe"
if (-not (Test-Path $starMapExe)) {
    Write-Host "StarMap.exe nicht gefunden. Lade StarMap Loader herunter..." -ForegroundColor Yellow
    $url = "https://github.com/StarMapLoader/StarMap/releases/download/0.5.4/StarMap-0.5.4.exe"
    Invoke-WebRequest -Uri $url -OutFile $starMapExe
    Write-Host "StarMap.exe heruntergeladen." -ForegroundColor Green
}

$starMapConfig = Join-Path $KSAPath "StarMapConfig.json"
if (-not (Test-Path $starMapConfig)) {
    $cfgJson = @{ GameLocation = $KSAPath } | ConvertTo-Json
    Set-Content -Path $starMapConfig -Value $cfgJson
    Write-Host "StarMapConfig.json erstellt." -ForegroundColor Green
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Installation erfolgreich!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Starte Kitten Space Agency ueber: $starMapExe" -ForegroundColor White
