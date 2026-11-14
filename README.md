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

Start the game through `StarMap.exe`, not the normal shortcut. KITN is visible only
in a loaded flight with a controlled rocket, and stays hidden in the main menu
and vehicle builder. Opening the main window shows a launcher with tiles for
Flight, Flight Plan, Automation, Rendezvous, Landing, Maneuvers and Diagnostics.
Inside a mode, use the compact icon row to switch pages or return Home. The active
mode expands to show its name; icons also have hover tooltips. The settings gear
stays on the right. If you close KITN, the small Home icon at the lower left
reopens the launcher during flight.

Settings, presets and window positions are saved next to the mod DLL in the mods folder, as `flight_presets.json` and `ui_layout.json`.

Open the **settings gear → Widgets** to enable the mission time/altitude widget
and choose its theme. **Retro** uses mechanical drums for altitude, mission time,
`T`, the sign and `M`; **Modern** uses simple numeric readouts. Visible instruments
update immediately. Each widget's visibility, theme and transparency save in
`ui_layout.json`, along with positions and the main window's open/closed state.
Starting automation respects the saved instrument visibility.

The **Transparent until hovered** toggle below the theme hides the title, border,
background and status until you hover over the instrument window. Its drag and
resize controls reappear on hover. The readouts keep their positions during this
transition. Drag the corner to resize down to 144 × 108 pixels at the default
interface scale; both readouts shrink with the window.

Hold **Alt** while dragging either KITN window to show center guides and snap to the screen center or the other window's center and matching edges. Release Alt to move freely.

The flight preview retains its completed graph and status while refreshing in
the background. Small RCS fuel changes and orbital phase do not repeatedly
invalidate it; meaningful rocket or orbit changes still update the calculation.
Launch actions verify a fresh snapshot, and flight preview updates do not change
the transfer or rendezvous header status.

Open **Maneuvers** for transfer planning from a stable parking orbit. Select a
planet or moon, then press **Calculate** in the main toolbar. The destination
dropdown reads the loaded system, including mod bodies. Choose a cell in the
**Porkchop** plot, **Lowest Δv**, or **Earliest** with a Δv budget. Blue cells cost
less; the ring marks the minimum and the crosshair marks your selection.
Colors and contours continue across unavailable or unsafe regions without holes.
These display-only interpolations do not create routes: invalid cells remain
blocked and their tooltip identifies them as unavailable.
The Porkchop area stays above the search options, including at small window
sizes. The single **Calculate** button stays in the main toolbar. The plot area
shows readiness, search progress and the resulting Δv contours.
The route summary emphasizes arrival from now and also shows its absolute
elapsed game day/time. Travel duration remains visible. Plot hover cards include
departure, arrival, duration and cost with icons; major/minor contours are drawn
continuously. The destination picker uses the full available width.

For direct moon transfers, the default departure window covers two parking-orbit
revolutions, so individual departure opportunities are resolved. Longer windows
automatically increase the departure sampling. Interplanetary transfers keep
their longer synodic search windows.

**Lowest Δv** compares the actual departure cost, plus capture when enabled,
and refines several independent minima between raster samples within the chosen
departure and transit window. It does not prefer a more expensive bound route.
**Earliest** retains its preference for affordable bound trajectories.
In **Lowest Δv**, **Earth Oberth · lowest Δv** starts a dedicated search for
departure at the actual periapsis passages of the current Earth parking orbit.
It optimizes the transit time and nearby periapsis opportunities, including the
rocket's finite burn and the moving SOI boundary. Circular parking orbits have
equal altitude and speed throughout, so every departure phase is eligible there.
The button plans the departure from the existing orbit; it does not insert an
extra maneuver to lower periapsis or raise apogee. Native encounter checks still
apply before a node can execute. An Oberth route is not guaranteed to cost less
than the unconstrained minimum for every starting orbit and destination.
An Oberth search also prepares a normal departure-time Porkchop grid in the
background. Switching to Porkchop uses that cached grid immediately and retains
the selected periapsis route. Clicking a valid plot cell explicitly selects the
displayed normal transfer; invalid cells cannot replace the selection.
Manual Porkchop selection follows the exact clicked or dragged
cell, including shorter escape trajectories. A brief notice identifies departures
from the central SOI; native encounter verification still applies before use.
Previously rejected windows remain blocked. For flight times below the plot's
lower edge, lower the minimum transit time in **Search window** and calculate
again. Required departure from a planet for an interplanetary transfer is supported.

Enable **Gravity assists** under **Search window** to compare direct routes
with a single unpowered flyby of an eligible planet or moon. Candidates come
from the loaded system, including mod bodies sharing the transfer's central
parent. The calculation matches incoming and outgoing excess speeds, checks
the required turn against terrain and atmosphere clearance, then corrects the
complete finite-SOI trajectory and final arrival periapsis. A selected flyby is
shown in the route. KSA's native prediction must contain that encounter before
the destination before execution is released. The toggle is saved with mod
preferences. Systems with no eligible intermediate body retain direct routes.
Multiple flybys and powered assists are not included. The search finds the best
validated candidate it can within its configured window, not a proven global
minimum over every departure date and possible flyby sequence.

Before accepting a departure node, KITN checks KSA's native trajectory prediction,
including the finite engine burn. It requires an actual destination SOI encounter
with a safe periapsis and rejects impact or a return to the departure body's
periapsis before that encounter. Bound transfers remain valid when they reach
the destination before returning. Normal parking-orbit revolutions before the
departure burn do not count as a return.

For direct moon and interplanetary transfers, each search candidate is corrected for the
rocket's finite burn using a copied snapshot of the native engine phases.
KITN adjusts the burn vector to reach the transfer corridor after engine cutoff,
then corrects the approach from the destination's finite SOI boundary to obtain
the requested arrival periapsis. Candidates exceeding the departure fuel supply,
impacting during the burn, or returning before SOI entry are filtered during
the search. Ephemerides preserve each orbit's gravitational mass override.
Search logs summarize rejected candidates by cause; native rejections record
the body, patch times, transitions, apsides, ignition and cutoff.

An unsafe candidate is removed and the main toolbar offers **Search for a better
route?**. The search begins only when you press that button. It excludes the
rejected departure/transit window, adjusts unsafe arrival periapsis above terrain
or atmosphere, and compensates for a measured finite-burn altitude shortfall.
When the predicted transfer apoapsis is too low to reach the destination, or its
periapsis is unsafe, the new search applies corrected apsis limits and expands
the range of flight times. These adjustments affect the planned transfer and
arrival; the current parking orbit stays intact.

The new result is presented for review before you create or execute a node.
**Create & execute** still waits for native verification before alignment, warp
or ignition. Unrelated maneuver nodes are preserved and prevent a new route
search. The last completed plot remains visible during recalculation, and plot
geometry and header rows stay stable as search progress updates.

If the search has no remaining non-returning route, it runs a second pass for
the best available transfer with return risk. This pass relaxes the return
restriction and additional corrective apsis limits, and can reuse windows
previously rejected for returning or missing the target encounter. The result
is marked as a best-effort route. Its node remains available even when the native
prediction returns before the target or has no confirmed target SOI encounter;
an approach correction may be needed. Predicted impacts and unsafe arrival
periapsis still block the node. Cancelling either pass prevents results from
being published, and the search itself does not create or execute a node.

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

The **Landing** tab includes saved **Landing settings** for touchdown descent
speed, braking margin, approach height, response time, maximum tilt and turn
rate. **Fuel & return model** contains reserve percentage, extra delta-v,
effective drag area multiplier, drag learning and the return search window.
Warp preference is saved as well. Settings are locked during the landing;
**Reset landing settings** restores their defaults.

Return and landing continue to use the current rocket's measured mass, thrust,
usable fuel, engine minimum throttle and restart capability. Slower attitude
response and tighter tilt limits raise the planned reserve. The final descent
holds clearance while lateral motion settles and tracks the selected vertical
touchdown speed. Actual contact speed varies with the engine's minimum pulse and
attitude response. Adequate delta-v is required alongside enough thrust to brake
and support the vehicle; settings cannot make an uncontrollable or underpowered
vehicle land safely.

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

Lucide 0.468.0 icons are vendored under `KITN/Assets/Icons`. To add an icon,
include its SVG and run `python tools/build_lucide_icons.py` before building.
The generated vectors and license are embedded in the DLL. The launcher and
navigation share the mode catalog in `AppNavigation.cs`; widget preferences use
independent IDs so additional widgets can have their own visibility and style.

## Layout

- `KITN/` mod sources, entry point is `KITNMain.cs`
- `tests/` diagnostics tests
- `install.ps1` installer and updater

## Worth knowing

- The route preview and the landing path are estimates. The drag and the final approach numbers need tuning against real flights.
- The rocket needs a flight computer and steering. Without them, or without a route that reaches the target orbit, the launch stays blocked and the window tells you why.
- A full return from Earth orbit has not been flown through in the real game physics yet.
