#requires -Version 7.4
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$ReferenceRepository = 'steve-787/KITN-build-references',
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$ModRepository = 'steve-787/KITN'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$archivePath = Join-Path $root '.local/ksa-references.zip'
$metadata = Get-Content -LiteralPath (Join-Path $root 'build/ksa-references.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $metadata.sha256) {
    throw 'Generate the reference archive before publishing it.'
}
$private = gh repo view $ReferenceRepository --json isPrivate --jq .isPrivate 2>$null
if ($LASTEXITCODE -ne 0) {
    gh repo create $ReferenceRepository --private --add-readme --description 'Compile-only KSA references for KITN release builds.'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the private reference repository.' }
    $private = 'true'
}
if ($private -ne 'true') { throw 'The build-reference repository must be private.' }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('KITN-reference-publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $checkout = Join-Path $scratch 'checkout'
    git -c credential.helper= -c 'credential.helper=!gh auth git-credential' clone --depth 1 "https://github.com/$ReferenceRepository.git" $checkout
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check out the reference repository.' }
    git -C $checkout config user.name 'KITN build references'
    git -C $checkout config user.email '41898282+github-actions[bot]@users.noreply.github.com'
    $tag = $metadata.ref
    $remoteTag = git -C $checkout ls-remote origin "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing reference tags.' }
    if (-not $remoteTag) {
        Copy-Item -LiteralPath $archivePath -Destination (Join-Path $checkout 'ksa-references.zip')
        git -C $checkout add -- ksa-references.zip
        git -C $checkout commit -m "KSA $($metadata.gameVersion) compile-only references"
        if ($LASTEXITCODE -ne 0) { throw 'Cannot commit the reference archive.' }
        git -C $checkout tag $tag
        if ($LASTEXITCODE -ne 0) { throw 'Cannot tag the reference archive.' }
        git -C $checkout -c credential.helper= -c 'credential.helper=!gh auth git-credential' push --atomic origin HEAD "refs/tags/$tag"
        if ($LASTEXITCODE -ne 0) { throw 'Cannot publish the reference archive.' }
    }

    $keyPath = Join-Path $root '.local/ksa-references-deploy-key'
    if (-not (Test-Path -LiteralPath $keyPath)) {
        ssh-keygen -t ed25519 -N '' -f $keyPath -C 'KITN Actions build references' -q
        if ($LASTEXITCODE -ne 0) { throw 'Cannot generate the reference deploy key.' }
    }
    $publicKey = [IO.File]::ReadAllText(($keyPath + '.pub')).Trim()
    $keysJson = gh api "repos/$ReferenceRepository/keys"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect reference deploy keys.' }
    $keys = $keysJson | ConvertFrom-Json
    if (-not @($keys | Where-Object { $_.key -eq (($publicKey -split ' ')[0..1] -join ' ') }).Count) {
        $body = @{ title = 'KITN release references'; key = $publicKey; read_only = $true }
        $bodyPath = Join-Path $scratch 'deploy-key.json'
        [IO.File]::WriteAllText($bodyPath, ($body | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        gh api "repos/$ReferenceRepository/keys" --method POST --input $bodyPath --jq .id
        if ($LASTEXITCODE -ne 0) { throw 'Cannot register the read-only reference deploy key.' }
    }
    Get-Content -LiteralPath $keyPath -Raw | gh secret set KSA_REFERENCES_SSH_KEY --repo $ModRepository
    if ($LASTEXITCODE -ne 0) { throw 'Cannot set the reference deploy key Actions secret.' }
    gh variable set KSA_REFERENCES_REPOSITORY --body $ReferenceRepository --repo $ModRepository
    if ($LASTEXITCODE -ne 0) { throw 'Cannot set the reference repository Actions variable.' }
    Write-Host "Configured private references at https://github.com/$ReferenceRepository/tree/$tag and the read-only Actions deploy key."
}
finally {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $scratchPath = [IO.Path]::GetFullPath($scratch)
    if (-not $scratchPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($scratchPath) -notlike 'KITN-reference-publish-*') { throw 'Unexpected reference checkout cleanup path.' }
    Remove-Item -LiteralPath $scratchPath -Recurse -Force
}
