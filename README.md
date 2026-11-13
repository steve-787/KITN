# KITN

KITN stands for Kitten Intelligent Trajectory. It is a mod for Kitten Space Agency that flies your rocket into orbit.

You pick a target orbit and press start. After that the mod handles the throttle, the pitch and the staging. It climbs straight up, tips over into a gravity turn, coasts to apoapsis and burns to circularize the orbit. It can also bring a booster back from orbit and set it down on a launch pad.

The mod draws its windows with the game's own fonts and widgets, so it looks like part of Kitten Space Agency.

## Status

KITN is under active development. It is very likely buggy. Expect the interface to change without warning, and expect your rocket to occasionally do something you did not ask for. Save your game before you fly.

There are no prebuilt binaries in this repository. The only way to get the newest version is to compile the mod from source. The `main` branch always holds the latest code, and it is often ahead of anything that was working yesterday.

## What you need

* Windows.
* Kitten Space Agency. The mod was written against version 2026.11.10.5801.
* The StarMap mod loader. The installer downloads it for you.
* The .NET 9 SDK, for building from source.

## Building from source

```powershell
git clone https://github.com/steve-787/KITN.git
cd KITN
dotnet build .\KITN\KITN.csproj -c Release
```

The result is `KITN\bin\Release\net9.0\KITN.dll`.

The build copies references straight out of the game folder, so `KITN\KITN.csproj` has to point at a real installation. It defaults to `K:\Games\Kitten Space Agency`. Change the `KSAPath` property if your game lives somewhere else. A build with the wrong path fails with missing assembly errors.

## Installing

Open PowerShell in the repository folder and run:

```powershell
.\install.ps1
```

The script builds the project, copies `KITN.dll`, `KITN.pdb` and `mod.toml` into `Documents\My Games\Kitten Space Agency\mods\KITN\`, registers the mod in `manifest.toml`, and downloads `StarMap.exe` into the game folder if it is not there yet.

If you prefer to do it by hand, copy the three files into the mods folder and add this to `manifest.toml`:

```toml
[[mods]]
id = "KITN"
enabled = true
```

## Starting a flight

Launch Kitten Space Agency through `StarMap.exe`, not through the normal shortcut. Load a flight or put a vehicle on a launch pad and the KITN window opens on its own.

The window has five tabs.

The flight tab shows the route. The red line is the planned route, calculated for the rocket you have selected right now. The white line is the flight the rocket actually made. You can zoom with the mouse wheel and drag the graph around once you switch off auto scale.

The flight plan tab holds the countdown, the target orbit and the gravity turn settings. It also has presets. Give your settings a name and save them, and they show up in the dropdown on the next flight.

The automation tab holds the staging options and the time warp. Automatic staging switches to the next stage when the current stage runs out of propellant. Hot staging lights the next stage before the current one is empty, at a propellant level you choose.

The diagnostics tab shows climb rate, dynamic pressure, acceleration and pitch. It also has the switches for the log files and the telemetry.

The landing tab handles the booster return. Pick a launch pad and press start landing.

## Flight phases

```mermaid
flowchart LR
    A["Ready"] --> B["Vertical climb"]
    B --> C["Gravity turn"]
    C --> D["Coast to apoapsis"]
    D --> E["Circularization burn"]
    E --> F["Target orbit reached"]
```

The vertical climb holds full throttle and straight up. The gravity turn starts at 2500 m, once the rocket has at least 125 m/s of surface speed, and eases the nose over towards the horizon. The pitch rate is limited to 1 degree per simulation second at the bottom of the ascent and grows to 3 degrees per second between 10 km and 40 km. Above the thick air a weaker stage gets extra pitch so it can still climb at a useful rate.

The coast shuts the engine off. The circularization burn starts about half a burn length plus two seconds before apoapsis, which is where the extra speed for the periapsis has to come from. It raises the periapsis first and then brakes the radial motion.

The target orbit counts as reached when both apsides sit inside the larger of 1 percent or 2 km of the target. If the periapsis is already safe and the apoapsis is too high, the mod waits for the next apoapsis and trims it there.

## Target orbit

The default is a 200 km by 200 km low Earth orbit. You can type any apoapsis and periapsis you like in kilometres, as long as the periapsis is at least 100 km and below the apoapsis.

## Staging

Automatic staging watches the stage that is currently burning. When it goes dry, the mod calls the game's own sequence switch to light the next stage.

Empty side boosters are handled separately. The mod looks only at the tanks and engines inside the branch that is about to be dropped, so a full stage next door does not keep the empty one attached. A booster has to stay empty for half a second of game time before the decoupler is triggered. Solid boosters count their unburnable residue as empty, and liquid engines count the usable mixture, so an engine that has run out of oxidizer counts as empty even when fuel is left.

Hot staging is a switch in the automation tab. Set the remaining propellant percentage and the mod lights the next stage while the current one is still running.

## Booster landing

This mode takes the vehicle you have selected in the game and brings it back from Earth orbit to a launch pad. It only works around Earth.

The pads in the list come from the game data. You get CCSFS LC-39A, VSFB LC-4, Starbase OLM A, GSC ELA-4 and Māhia LC-1A with the stock game, plus any other place the game marks as a launch pad.

To land, select the booster in the game, activate a stage with liquid main engines that can be restarted, and keep enough propellant in reserve for the trip home. The vehicle needs a control module and a flight computer. The mod searches for a return window, does a targeted deorbit burn, corrects on the way in and flies a powered descent to the pad.

The landing counts as confirmed when the vehicle has real ground contact for at least 0.75 seconds, is moving at no more than 1.5 m/s and sits on the pad. Touchdown ends the guidance. The mission clock keeps running.

Empty boosters that were dropped on a suborbital path cannot be recovered this way. A booster has to have made it into orbit on its own.

## Mission clock

Press start countdown and the mod opens a separate clock window. The countdown starts at 10 seconds by default and you can set it anywhere from 1 to 3600 seconds in the flight plan tab. The clock shows `T-HH:MM:SS` until the count reaches zero, then it switches to `T+HH:MM:SS` and keeps going after the orbit is reached.

The clock uses game time, so pausing the game pauses the clock and time warp speeds it up. Changing vehicle or leaving the planet cancels a running countdown.

Under the clock you get the game's own scrolling altitude readout, moved into the clock window. The original readout in the game is hidden while the clock window is open and comes back when you close it.

Window positions and sizes are stored in `ui_layout.json` next to the mod DLL. They are restored on the next launch. The previous version of the file is kept as `ui_layout.json.bak`.

## Presets

Presets save the whole flight plan: target orbit, gravity turn heights, minimum speed, launch heading, turn shape, pitch rate, countdown, staging and time warp. There is a built in preset called Default LEO that restores the 200 by 200 km setup.

Your presets live in `flight_presets.json` next to the mod DLL. The file is written completely before it replaces the old one, and the old one is kept as `flight_presets.json.bak`. If the file turns out to be damaged, the mod keeps it and disables saving until you reload the presets.

## Logs and telemetry

The mod writes three files into the mods folder, next to `KITN.dll`.

`kitn.log` is a readable log with timestamps, a level and a category for every line. Phase changes name the reason that caused them, staging records the reason and the sequence before and after the call, and errors include the exception type and stack trace.

`kitn_telemetry.csv` holds the same measurements as CSV, one row per sample. Numbers use a decimal point and text fields with commas are quoted.

`kitn_api_dump.txt` lists the control, position and physics interfaces that the installed game exposes.

Telemetry is on by default and records one sample every 0.5 seconds of real time. It keeps recording before a launch and after an abort. Phase changes, vehicle changes and staging add extra samples. You can switch the recording off, switch the CSV off, and set the interval between 0.1 and 5 seconds in the diagnostics tab.

The log and the CSV keep three older copies each, named `.1`, `.2` and `.3`, and they also rotate once they pass 10 MiB. You can read both files while the game is running.

To follow the log in PowerShell:

```powershell
Get-Content -LiteralPath 'C:\Users\karst\Documents\My Games\Kitten Space Agency\mods\KITN\kitn.log' -Tail 40 -Wait
```

To look at a flight afterwards:

```powershell
$flightRows = Import-Csv -LiteralPath 'C:\Users\karst\Documents\My Games\Kitten Space Agency\mods\KITN\kitn_telemetry.csv'
$flightRows | Select-Object simulation_t_s, state, barometric_altitude_m, vertical_speed_m_s, live_thrust_n, actual_pitch_deg, commanded_pitch_deg
```

Several CSV columns end in a unit suffix such as `_m` or `_m_s`. `accel_fd_cci_*` is worked out from two velocity samples and the simulation time between them, so it is an average over the sample interval. A value of `NaN` means the number was not available or did not apply. `physics_active=false` and `physics_valid=false` show that there was no physics running, which happens during time warp.

## Language

The mod uses the translation provider that comes with the game, so menus, buttons, help texts and status messages follow the game language. The Windows language does not matter.

The game version this was written against only ships English, so the mod starts in English. German is included in the mod and is used when the game runs in German. Language packs for the game can translate the mod too. The keys are named `KITN.<key>`, for example `KITN.tab.flight`. Missing entries fall back to the text built into the mod.

## Project layout

```
KITN/
├── install.ps1                       installer and updater
├── README.md
├── KITN.sln
├── KITN/
│   ├── KITN.csproj                   project file with the KSA and StarMap references
│   ├── KITNMain.cs                   mod entry point
│   ├── mod.toml                      StarMap mod descriptor
│   ├── AscentGuidance.cs             ascent states, coast and insertion
│   ├── AdaptiveFlightPlanner.cs      route search for the current rocket
│   ├── AdaptiveAscentControl.cs      turn control during the ascent
│   ├── GravityTurnController.cs      pitch profile and attitude limits
│   ├── FlightAttitude.cs             attitude maths and native KSA attitude targets
│   ├── OrbitInsertion.cs             orbit, burn and apoapsis maths
│   ├── ThrottleActuator.cs           minimum thrust pulses and cutoff
│   ├── StagingController.cs          automatic and hot staging
│   ├── BoosterMonitor.cs             booster propellant watch
│   ├── BoosterSeparation.cs          decoupler logic
│   ├── LandingController.cs          return planning and touchdown
│   ├── LandingMath.cs                return and descent maths
│   ├── LandingSupport.cs             pad and engine helpers
│   ├── LandingTelemetry.cs           landing values for the log and the CSV
│   ├── FlightDisplay.cs              route preview and launch checks
│   ├── FlightRecorder.cs             position, physics and control telemetry
│   ├── FlightTrack.cs                measured flight path
│   ├── RoutePrediction.cs            predicted route
│   ├── FlightPresets.cs              saved flight plans
│   ├── LaunchProfile.cs              settings model
│   ├── AutopilotWindow.cs            main window
│   ├── MissionTimeWindow.cs          mission clock window
│   ├── MissionClock.cs               countdown and mission time
│   ├── MissionAltitudeDisplay.cs     hosts the game's altitude readout
│   ├── PersistentWindowLayout.cs     window geometry
│   ├── UiLayoutStore.cs              ui_layout.json
│   ├── Localization.cs               fallback text catalog
│   ├── GameLocalization.cs           registration with the game
│   ├── GameUiColors.cs               colours from the game theme
│   ├── TelemetryRecord.cs            shared log and CSV format
│   ├── DiagnosticFile.cs             log files and rotation
│   ├── Diag.cs                       categories, error reporting and API dump
│   └── RocketSnapshotReader.cs       reads the current rocket state
└── tests/
    ├── KITN.Diagnostics.Tests.csproj
    └── ...                            test sources and fixtures
```

## Tests

The diagnostics tests run without the game and without any extra test packages:

```powershell
dotnet run --project .\tests\KITN.Diagnostics.Tests.csproj -c Release
```

They cover the orbit maths, the burn window, the staging decisions, the landing maths, the mission clock, the presets, the window layout and the language catalog. Some of them replay flight log states and the fixed insertion data in `tests\Fixtures\insertion-20261112.csv`.

The tests use their own simplified flight model. They do not replace a test flight in the real game physics.

## Limits worth knowing

* The route preview is an estimate. It uses mass, thrust, air pressure, finite propellant, stage changes, gravity, planet rotation and a simplified drag model. It does not simulate the full game physics, and it does not model the overlap of hot staging.
* The landing path is estimated the same way. The drag and the final approach numbers need tuning against real flights.
* A full return from Earth orbit has not been flown through in the real game physics yet. It has been tested with simulated returns and against the game's own interfaces.
* The mod needs a flight computer and steering on the rocket. Without them the launch stays blocked and the window tells you why.
* The launch is blocked when the flight planner cannot find a route that reaches the target orbit. The window shows the reason, such as too little thrust at launch, too little propellant, a missing flight computer, a missing control module, an invalid stage order or a target inside the atmosphere.
