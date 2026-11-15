# Hohmann transfer validation

Added a fourth Maneuvers mode using `HohmannTransferParameters` adapted from
the user's MechJeb checkout (`C:\Users\karst\Documents\MechJeb2`). The
MechJeb-derived Lambert, parking-orbit ejection and finite-burn solvers refine
the nominal flight time and departure-phase seeds against actual orbits.
Departure nodes retain KITN's existing KSA trajectory validation. Capture is
estimated and planned after the encounter is established.

Saved Hohmann preferences in `ui_layout.json`: departure lead (300 s), number
of synodic windows (1), flight-time tolerance (±20%), include capture cost
(enabled) and automatic warp (enabled). Arrival periapsis remains per target.
Hohmann has independent capture/warp settings and disables gravity-assist,
Oberth and best-effort fallback searches. Co-orbital and counter-rotating
orbit pairs receive an explicit unsupported-geometry message.

Passed:

- Release build: zero warnings and errors.
- Complete diagnostics suite, including three new Hohmann checks: published
  LEO–GEO impulse/time benchmarks; independent two-body propagation of inward,
  outward and retrograde phase windows; analytical seeds between grid samples;
  search bounds and cancellation; persistence, migration and corrupt-file
  preservation.
- Native `--hohmann-checks`: KSA orbital snapshots, an inclined moving Moon,
  finite-burn propagation reaching its SOI, four selectable modes, independent
  capture/warp settings, English/German controls, minimum window sizes and
  locked/default settings interaction.
- Native `--native-transfer-checks`: recorded lunar transfers, safe finite
  burns, capture altitude, Oberth, cancellation and interplanetary ejection
  and gravity-assist regressions.
- Native `--porkchop-axis-checks`: complete axis labels, localized durations
  and plot selection at minimum size.
- Native `--remote-control-checks`: vessel switching, prolonged range loss
  and recovery, docking state, cancellation, clock reversion and physics warp.
- `git diff --check`.

The default full native UI suite stops at the existing assertion
`Launcher flight tile cannot open its app page` in `LauncherNavigationChecks`.
The same current test harness reproduces that failure when run in an isolated
directory against the previously installed committed DLL from
`7e3b51cc35d170ad68b16e5ec0d382bbd4006723` (SHA256
`686B19EF00BB22AFC3F009DB0D40CA5C840B260421C1E8A988419893F79D6857`).
This failure occurs before the transfer-page tests.

UI renders are under `artifacts/ui/hohmann`. Installed-file hashes and the
backup path are recorded in `hohmann-installation.json`. No running-game
test flight was performed; restart the game to load the installed DLL.
