# Remote-control eligibility and compact widget

Validated against KSA 2026.11.10.5801 on 2026-11-15.

The false docking pause came from requiring the controlled craft and docking target to share `Vehicle.BubbleLeader`. KSA can place nearby craft in different simulation bubbles and merge them as contact becomes imminent. The mod now checks actual simulation availability instead of bubble identity. A docking target does not need to be controllable.

KSA's `Vehicle.IsControllable` checks control modules and crew/probe-core availability; it has no distance condition. `Program.ControlledVehicle` likewise has no distance gate. `BubbleOrigin.GetRecommendedRadius()` has a minimum of 2097.152 m and can grow with coordinate precision requirements; this is a clustering threshold, not a control-distance limit. KITN's 2000 m automatic-docking approach limit is separate.

Remote Control now shows the selected vessel once, followed by compact vessel-name/Control rows. Idle statuses and the repeated selected-vessel row are omitted. Instructions and full suspension reasons are available as tooltips.

- Isolated Release build: zero warnings/errors; unrelated Hohmann work excluded.
- Native regression: independently simulated vessels with different bubble leaders remain selectable, and autodocking advances for a target 103.72 m away.
- Native regression: a target without a flight computer does not suspend docking; actual loss of target simulation still pauses and resumes without clearing the operation/path.
- English/German compact Control buttons remain clickable.
- Existing native automatic docking, connection confirmation, engine assist, settings click/persistence/locking, and world-overlay checks pass.

Reproduce the new checks with:

```powershell
dotnet build KITN/KITN.csproj -c Release
dotnet run --project tests/NativeUi/KITN.NativeUi.Tests.csproj -c Release -- artifacts/ui/remote-control --remote-control-checks
```
