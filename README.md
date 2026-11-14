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

Open **Maneuvers** for transfer planning from a stable parking orbit. Select a
planet or moon, then press **Calculate** in the main toolbar. The destination
dropdown reads the loaded system, including mod bodies. Choose a cell in the
**Porkchop** plot, **Lowest Δv**, or **Earliest** with a Δv budget. Blue cells cost
less; the ring marks the minimum and the crosshair marks your selection.
The Porkchop area stays above the search options, including at small window
sizes. The single **Calculate** button stays in the main toolbar. The plot area
shows readiness, search progress and the resulting Δv contours.

For direct moon transfers, the default departure window covers two parking-orbit
revolutions, so individual departure opportunities are resolved. Longer windows
automatically increase the departure sampling. Interplanetary transfers keep
their longer synodic search windows.

Selections prefer transfers that remain bound to the shared central body. A
Porkchop click on an escape trajectory selects the nearest available bound
window. Escape transfers remain selectable when no future bound alternative
exists; required departure from a planet for an interplanetary transfer is supported.

Before accepting a departure node, KITN checks KSA's native trajectory prediction,
including the finite engine burn. It requires an actual destination SOI encounter
with a safe periapsis and rejects impact or a return to the departure body's
periapsis before that encounter. Bound transfers remain valid when they reach
the destination before returning. Normal parking-orbit revolutions before the
departure burn do not count as a return.

An unsafe candidate is removed and its departure/transit window excluded from
up to three automatic recalculations. **Create node** remains create-only;
**Create & execute** waits for verification before alignment, warp or ignition.
Unrelated maneuver nodes are preserved and prevent automatic replacement. If
no route passes verification, KITN stops and asks for different search bounds.

Expand **Search window** to change the departure delay, search horizon, transit
bounds, arrival periapsis, capture-cost inclusion and automatic warp. Times use
hours or 24-hour days. The search runs in the background and can be cancelled.
Changing the vehicle, orbit or simulation time invalidates obsolete plans.

**Create node** adds the departure impulse to KSA's native maneuver plan.
**Create & execute** first aligns using inertia-aware RCS with engines off,
then immediately warps to 60 seconds before ignition after the first successful
alignment. RCS pulse resolution is respected, so unavoidable tiny angular
corrections do not prevent warp. It checks alignment again at normal speed
before enabling the native automatic burn at ignition. Manual throttle and
translation commands are cleared throughout; a missed ignition stops the transfer.
It can be disabled in **Search window**.
**Stop maneuver** is available in the main toolbar while it is enabled. Other
nodes must be removed first; **Remove planner node** only removes KITN's node.
Transfers and ascent/landing automation cannot control the vehicle together.

Automatic staging checks the local usable engine propellant behind each
scheduled decoupler, including radial boosters and inline lower stages. An
empty, previously activated branch can separate during coast; remaining fuel,
control modules and unstarted engines keep it attached. Groups containing only
completed separations advance automatically without skipping other actions.
Tanks require a staged decoupler to be jettisoned.

**Settings → Control → Allow RCS** controls RCS use for all KITN automation and
is saved with the other mod preferences. Ascent and landing use RCS for small
corrections; transfers can also use it to turn toward a badly misaligned node.
RCS turns use a lower rotation rate and a wider resting deadband. The rate limit
uses the current torque divided by the vehicle's moment of inertia and reserves
braking distance. KSA's native impulse/coast/brake controller performs the turn,
so jets stay quiet during the coast instead of continuously pushing. Manual
flight settings are restored when KITN control ends or the vehicle changes.

Open **Settings → Open diagnostics** for the existing flight readouts, telemetry
toggle, log interval and CSV control. Diagnostics no longer occupies a flight tab.

The **Rendezvous** tab lists loaded vehicles around the same body, including
mod-created craft, and their free docking ports. **Match velocity** removes
relative velocity using RCS. **Autodock** in the toolbar aligns the selected
ports, establishes stand-off before lateral corrections, then approaches at up
to 0.08 m/s near contact. It supports side-mounted ports and moving or slowly
rotating targets; the game's physical docking connection confirms completion.

This first docking controller works from an existing nearby rendezvous within
2 km, with both craft in the active physics area, compatible ports, enough
braking room and working RCS rotation/translation. Enable **Allow RCS** in
settings and remove maneuver nodes before starting. **Stop docking** stays in
the toolbar on every page, including Settings and Diagnostics. Docking runs at
normal simulation speed, stops when RCS permission or the target is lost, and
restores the previous attitude and manual-thrust settings on completion or stop.

The calculation adapts MechJebLib's Gooding Lambert solver and parking-orbit
ejection mathematics, with finite SOI escape aiming. It supports direct transfers
to moons of the current body and transfers between bodies orbiting the same
parent. Routes crossing additional moon/planet hierarchies require separate
legs. Arrival periapsis and capture Δv use an impulsive patched-conic estimate;
the first node is the departure burn. After departure, KITN creates a circular
capture node at periapsis on the actual predicted target encounter, including
before the SOI transition. The capture node is executed using native maneuver
controls during **Create & execute**. Automation stays active during the unbound
coast, with engines and attitude control off. It warps to the target SOI,
starts capture alignment only after entering that body's SOI, then applies the
same alignment and ignition safeguards to capture. Capture requests its own
warp to one minute before ignition once aligned, including when the native
ignition estimate changes while waiting. Each burn requests warp only once.
Unsafe approaches and
existing correction nodes delay its creation. A recovered capture node can
also be started with **Execute capture**, without requiring a bound parking orbit.
Arrival limits distinguish terrain from atmosphere; airless bodies have no
atmospheric limit. The altitude instrument names its reference body and switches
to a nearby planet or moon during approach, before SOI entry. Finite engine burns can
require correction maneuvers. See [third-party notices](THIRD_PARTY_NOTICES.md).

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
