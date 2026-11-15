# KITN

Kitten Intelligent Trajectory Navigator. A mod for Kitten Space Agency that flies your rocket into orbit.

Pick a target orbit and press start. KITN handles throttle, pitch and staging through the ascent and the circularization burn. It can also bring a booster back from orbit and set it down on a launch pad.

The windows use the game's own fonts and widgets.

## Status

Active development, and it is likely buggy. The interface will change without warning. Save your game before you fly.

Prebuilt packages are on the [releases page](https://github.com/steve-787/KITN/releases). The `main` branch has the newest development code.

## Requirements

- Windows
- Kitten Space Agency, written against version 2026.11.10.5801
- The StarMap mod loader, version 0.5.4 or newer
- The .NET 10 runtime required by StarMap

## Install

Download `KITN-<version>.zip` from the [releases page](https://github.com/steve-787/KITN/releases) and extract the `KITN` folder into your game profile's `mods` folder, usually `Documents\My Games\Kitten Space Agency\mods\`. Then enable it in that profile's `manifest.toml`:

```toml
[[mods]]
id = "KITN"
enabled = true
```

Start the game through StarMap. Updating means replacing the packaged files in the same KITN folder, so keep your saved settings and presets. Each release includes a `.zip.sha256` checksum file.

To install from a source checkout instead:

```powershell
.\install.ps1
```

The script builds the project, copies the mod and licenses into the mods folder, registers KITN in `manifest.toml`, and downloads `StarMap.exe` into the game folder if it is not there yet. Pass `-KSAPath` if the game is installed elsewhere.

## Using KITN

Start the game through `StarMap.exe`, not the normal shortcut. KITN appears only in a loaded flight with a controlled rocket, and stays hidden in the main menu and the vehicle builder.

The launcher has one tile per mode:

- Flight shows the predicted trajectory and the current flight state.
- Flight Plan sets the target orbit, the gravity turn and the countdown.
- Automation handles staging and time warp.
- Rendezvous approaches another vehicle that orbits the same body.
- Docking docks with a selected port.
- Landing brings the controlled vehicle back from Earth orbit to a launch pad.
- Maneuvers plans a transfer to another moon or planet.
- Diagnostics holds the flight readouts, telemetry and log controls.

Settings, presets and window positions are saved next to the mod DLL as `flight_presets.json` and `ui_layout.json`.

In Mod Settings, **Detach windows** opens KITN and its widgets as desktop windows that can be moved outside the game. In-game and desktop positions and sizes are saved separately; switching it off restores the previous in-game layout. The toggle is disabled when the game starts with `-fixed-viewport`.

Transfer maneuvers are checked against Kitten Space Agency's own trajectory prediction before the node is created. If the prediction says the burn will not reach the target, KITN refuses it and tells you why.

## Logs

The mod writes `kitn.log`, `kitn_telemetry.csv` and `kitn_api_dump.txt` next to the mod DLL in the mods folder. Logging and telemetry are switched in the diagnostics tab.

```powershell
Get-Content -LiteralPath "$env:USERPROFILE\Documents\My Games\Kitten Space Agency\mods\KITN\kitn.log" -Tail 40 -Wait
```

## Build

Building from source also requires the .NET 9 SDK.

```powershell
dotnet build .\KITN\KITN.csproj -c Release
```

The output is `KITN\bin\Release\net9.0\KITN.dll`. The build reads assembly references from `K:\Games\Kitten Space Agency` by default. For another installation, pass `"-p:KSAPath=C:\path\to\Kitten Space Agency"`. The StarMap API comes from NuGet and is supplied by the loader at runtime.

## Tests

```powershell
dotnet run --project .\tests\KITN.Diagnostics.Tests.csproj -c Release
```

These run without the game and without extra packages. They use their own simplified flight model, so they do not replace a test flight in the real physics.

Native UI checks need the game assemblies and the .NET 10 runtime. Build the mod in Release first, then run:

```powershell
dotnet run --project .\tests\NativeUi\KITN.NativeUi.Tests.csproj -c Release
```

They exercise all pages, the theme dropdown, countdown cancellation and dragging, and save rendered UI images under `artifacts/ui/redesign`.

Lucide 0.468.0 icons are vendored under `KITN/Assets/Icons`. To add one, include its SVG and run `python tools/build_lucide_icons.py` before building. The generated vectors and license are embedded in the DLL.

## Releasing

On GitHub, open Actions, then Release KITN, then Run workflow. Pick the default branch and choose a bump: `patch`, `minor`, `major` or `current`. The workflow updates `version.txt`, tests and builds the mod, commits the version change, tags it as `v<version>`, and publishes the ZIP and checksum as a GitHub Release. `current` publishes the version in `version.txt` unchanged.

The release guide in [docs/releasing.md](docs/releasing.md) covers the build references, the packaging step and the Borea listing.

## Layout

- `KITN/` mod sources, entry point is `KITNMain.cs`
- `tests/` diagnostics tests
- `docs/` release guide
- `tools/` packaging and icon scripts
- `install.ps1` installer and updater

## Known limits

- The route preview and the landing path are estimates. The drag and the final approach numbers need tuning against real flights.
- The rocket needs a flight computer and steering. Without them, or without a route that reaches the target orbit, the launch stays blocked and the window tells you why.
- A full return from Earth orbit has not been flown through in the real game physics yet.

## License

GPL-3.0-only. See [LICENSE](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md).
