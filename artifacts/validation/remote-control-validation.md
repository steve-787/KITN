# Remote control and physics warp validation

Validated against the local Kitten Space Agency assemblies on 2026-11-15.

- Release build: zero warnings and errors.
- Diagnostics: all 112 checks passed, including settings migration/persistence, suspended countdown timing, physics-warp budgets, and restoring the saved per-vessel mission clock.
- Native remote-control checks: player switching selects the new vessel while preserving the old session; remote selection restores that vessel's state; uncontrollable craft are excluded; prolonged loss of own or target physics range suspends countdown, ascent, and docking without clearing the docking preview; return restores flight-computer modes and begins with neutral throttle/translation commands; paused operations can be explicitly cancelled; disabling remote control stops background automation; save reversion clears stale sessions.
- Native physics-warp checks: healthy frames retain 4× warp, slow frames and physics falling behind reduce warp, and disabling the setting restores the 1× limit.
- Native UI: Remote Control renders in English and German. Existing header navigation, dragging, minimizing, docking settings, engine assist, docking connection, and world-overlay projection/culling checks passed.
- Coordinated slider changes: included after their separate Release build and native EN/DE click, persistence, locking, and visual checks; combined native docking settings checks also passed.

Reproduce the main new checks:

```powershell
dotnet build KITN/KITN.csproj -c Release
dotnet run --project tests/KITN.Diagnostics.Tests.csproj -c Release
dotnet run --project tests/NativeUi/KITN.NativeUi.Tests.csproj -c Release -- artifacts/ui/remote-control --remote-control-checks
```

These checks use native assemblies and constructed vessel fixtures. Real-game switching, range recovery, and physics throughput still need the planned in-game validation after installation. No game installation was changed during this work.
