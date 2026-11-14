# Releasing KITN

`version.txt` is the version source. The build puts that version and the Git commit into the DLL's assembly information; Settings reads them from the installed DLL.

## Build references

The GitHub workflow runs on `ubuntu-latest` with the .NET 9 SDK. It compiles KITN using 20 reference DLLs rather than a game installation. These DLLs contain type definitions and signatures with executable method bodies removed by JetBrains Refasmer 2.0.3. No game assets or native libraries are needed for compilation.

The reference archive is stored in the private repository `steve-787/KITN-build-references`. `build/ksa-references.json` pins its tag, SHA-256 checksum and game build. The release workflow fetches it using a dedicated read-only SSH deploy key in the Actions secret `KSA_REFERENCES_SSH_KEY`. The Actions variable `KSA_REFERENCES_REPOSITORY` names the private repository. Only the public StarMap API package is downloaded from NuGet. References stay in temporary directories and are removed at the end of the job; they are never cached or uploaded as release assets.

To generate or update the archive from an installed game, use PowerShell 7.4 or newer, the .NET 9 SDK, and an authenticated GitHub CLI account with permission to manage the reference repository and this repository's secrets:

```powershell
pwsh -File .\tools\prepare-ksa-references.ps1 -KSAPath "K:\Games\Kitten Space Agency"
pwsh -File .\tools\publish-ksa-references.ps1
```

The first script restores Refasmer, reads the required DLL names from `KITN.csproj`, strips their method bodies, and writes the ZIP to the ignored `.local/` directory. The second creates the private repository if needed, publishes the archive under an immutable tag, generates a read-only deploy key, and sets the Actions secret and variable. It sends the key on stdin. The key is kept locally in `.local/ksa-references-deploy-key`, which Git ignores. Commit `build/ksa-references.json` together with any source changes that need the new API.

For another repository, pass `-ReferenceRepository owner/private-reference-repo -ModRepository owner/mod-repo` to the publishing script. Contributors building from a game installation need no access to the CI references or deploy key.

Reference generation happens locally when the game API changes. A release job only fetches the archive, runs the game-independent tests, builds the DLL, and publishes its package. The workflow has no automatic push or scheduled trigger, and it uploads directly to Releases without storing an Actions artifact. Standard hosted runners are free for public repositories.

## Publish

Push the source and workflow to the default branch. Open **Actions → Release KITN → Run workflow**, select that branch, and choose a bump:

| Choice | Example |
| --- | --- |
| `patch` | `0.1.0` → `0.1.1` |
| `minor` | `0.1.7` → `0.2.0` |
| `major` | `0.9.8` → `1.0.0` |
| `current` | Publish the version in `version.txt` unchanged; use this for the first `0.1.0` release. |

The workflow commits a bump locally, tests and packages that exact commit, then pushes the version commit and annotated tag atomically. Builds that fail leave the branch unchanged. If the default branch changes during the build, publication stops so the workflow cannot overwrite other work. Releases run one at a time.

The upload is assembled as a draft and published after both assets are attached:

- `KITN-<version>.zip`
- `KITN-<version>.zip.sha256`

The DLL, tag and release all refer to the same version commit. Generated release notes include the commit and installation instructions. A published version cannot be overwritten. If a failure leaves a tag or draft after the push, run a new workflow with `current` to finish it while the default branch still points at that tag.

Enable **Build and verify without publishing** for a dry run that tests the hosted build without pushing a version or creating a release.

To build the same package locally:

```powershell
pwsh -File .\tools\package-release.ps1 -KSAPath "K:\Games\Kitten Space Agency"
```

The output is under `artifacts/releases/`. Packaging verifies the DLL version, embedded commit, dependency metadata, StarMap descriptor and exact ZIP contents.

## Borea

The release ZIP uses the content index's standard layout: exactly one top-level `KITN/` folder with `mod.toml` and the mod's payload. Entry paths use forward slashes on every platform. The descriptor declares `[StarMap] EntryAssembly = "KITN"`; StarMap and the game's libraries come from the installed loader and game.

Each release contains one ZIP, which the index's GitHub release watcher can select without ambiguity. Tags use ordinary semantic versions such as `v0.1.0`. The watcher computes its own archive checksum and install size; the additional `.sha256` asset supports manual downloads.

When KITN is ready to be listed, use the [content index listing form](https://ksamodding.github.io/content-index/) to create `listings/KITN.toml`. Set the release repository to `steve-787/KITN`, the loader to StarMap with minimum `0.5.4`, and the tested game build to the value in `build/ksa-references.json`. Use `KITN` as the id and `GPL-3.0-only` as the repository's license. The normal archive layout needs no install-root override. Metadata such as authors, compatibility and loader bounds belongs in the listing, rather than in the mod descriptor.

Settings and logs already use the installed DLL's directory, so they follow Borea's individual instance folders. Test a flight in a Borea instance before submitting the listing.

References: [listing rules](https://github.com/KSAModding/content-index/blob/main/CONTRIBUTING.md), [archive and version format](https://github.com/KSAModding/content-manager-design/blob/main/rfcs/0031-content-metadata-format.md), [instance paths](https://github.com/KSAModding/Borea/blob/main/docs/mod-authors.md).
