# KITN

Kitten Intelligent Trajectory. A mod for Kitten Space Agency that flies your rocket into orbit.

Pick a target orbit and press start. The mod handles throttle, pitch and staging through the ascent and the circularization burn. It can also bring a booster back from orbit and set it down on a launch pad.

The windows use the game's own fonts and widgets, so they look like part of Kitten Space Agency.

## Status

Active development, and it is likely buggy. The interface will change without warning. Save your game before you fly.

There are no prebuilt binaries here. Build from source, `main` always has the newest code.

## Requirements

- Windows
- Kitten Space Agency, written against version 2026.11.10.5801
- The StarMap mod loader, the installer downloads it
- The .NET 9 SDK

## Build

```powershell
dotnet build .\KITN\KITN.csproj -c Release
```

The output is `KITN\bin\Release\net9.0\KITN.dll`.

The build copies references out of the game folder, so `KITN\KITN.csproj` has to point at a real install. It defaults to `K:\Games\Kitten Space Agency`, change `KSAPath` if yours is elsewhere. A wrong path fails with missing assembly errors.

## Install

```powershell
.\install.ps1
```

The script builds the project, copies `KITN.dll`, `KITN.pdb` and `mod.toml` into `Documents\My Games\Kitten Space Agency\mods\KITN\`, registers the mod in `manifest.toml`, and downloads `StarMap.exe` into the game folder if it is not there yet.

By hand, copy the three files into the mods folder and add this to `manifest.toml`:

```toml
[[mods]]
id = "KITN"
enabled = true
```

## Running

Start the game through `StarMap.exe`, not the normal shortcut. The KITN window opens when a flight loads or a vehicle sits on a launch pad. It has tabs for the route, the flight plan, the automation, the diagnostics and the landing.

Settings, presets and window positions are saved next to the mod DLL in the mods folder, as `flight_presets.json` and `ui_layout.json`.

Open **Settings** in the main toolbar to choose the instrument theme. **Retro** uses mechanical drums for altitude, mission time, `T`, the sign and `M`; **Modern** uses simple numeric readouts. The visible instruments update as soon as you select a theme. Theme preferences save automatically in `ui_layout.json` along with window positions.

The **Auto-hide instrument frame** toggle below the theme hides the title, border, background and status until you hover over the instrument window. Its drag and resize controls reappear on hover. The readouts keep their positions during this transition. Drag the corner to resize down to 144 × 108 pixels at the default interface scale; both readouts shrink with the window.

Hold **Alt** while dragging either KITN window to show center guides and snap to the screen center or the other window's center and matching edges. Release Alt to move freely.

## Logs

The mod writes `kitn.log`, `kitn_telemetry.csv` and `kitn_api_dump.txt` into the mods folder. Logging and telemetry are switched in the diagnostics tab.

```powershell
Get-Content -LiteralPath "$env:USERPROFILE\Documents\My Games\Kitten Space Agency\mods\KITN\kitn.log" -Tail 40 -Wait
```

## Tests

```powershell
dotnet run --project .\tests\KITN.Diagnostics.Tests.csproj -c Release
```

They run without the game and without extra packages. The tests use their own simplified flight model, so they do not replace a test flight in the real physics.

Native UI checks require the game assemblies and .NET 10 runtime. After building the mod in Release, run `dotnet run --project .\tests\NativeUi\KITN.NativeUi.Tests.csproj -c Release`. They exercise all pages, the theme dropdown, countdown cancellation and dragging, and save rendered UI images under `artifacts/ui/redesign`.

## Layout

- `KITN/` mod sources, entry point is `KITNMain.cs`
- `tests/` diagnostics tests
- `install.ps1` installer and updater

## Worth knowing

- The route preview and the landing path are estimates. The drag and the final approach numbers need tuning against real flights.
- The rocket needs a flight computer and steering. Without them, or without a route that reaches the target orbit, the launch stays blocked and the window tells you why.
- A full return from Earth orbit has not been flown through in the real game physics yet.
