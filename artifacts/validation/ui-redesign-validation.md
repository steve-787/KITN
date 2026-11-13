# KITN UI redesign validation

- Release build: zero warnings and errors.
- Native ImGui checks: nine groups passed. These exercise both themes, every existing page, the general Settings page, dropdown keyboard selection and persistence, minimum sizes, countdown cancellation in both windows and Settings, German labels, no-vehicle fallback, free dragging, Alt snapping and guide visibility.
- Game-independent diagnostics: 65 passed. One existing check fails because the local live telemetry file has no usable recorded ascent/coast samples (`RecordedFlight`, `tests/Program.cs`). The test and flight controllers were not changed by this UI work.
- Native UI images are under `artifacts/ui/redesign`. These render actual ImGui geometry with the game's fonts using fixture telemetry; they are not screenshots of a running flight.
- Retro uses a shared drum face with separate attached white drums for `T`, the sign, and `M`. Modern uses matching plain readouts. Settings has a dropdown and category-based layout without a duplicate instrument preview.
- Themes share the existing debounced, atomic `ui_layout.json` persistence. Original files without a theme remain compatible. Invalid theme files are preserved and saving stays disabled until reloaded.
- Installation replaces DLL, PDB and README only, with backups and hash checks. It does not change saved positions, presets or live flight logs.

Release DLL SHA256: `689F7CAF4056D39E594BC453DF448B4E6DDFB21BF5D486611350A8A2037234FE`.
