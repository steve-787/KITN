# Instrument frame, resizing and alignment

- Release build: zero warnings and errors.
- Game-independent diagnostics: all 66 checks pass, including migration and persistence of the auto-hide preference.
- Native UI: all ten verification groups pass. Mouse resizing shrinks the instrument window below its old minimum; both themes fit at 144 × 108 pixels, with working countdown cancellation.
- Auto-hide removes the native panel, border, title, resize grip, separator and status while retaining instrument geometry. Hover restores the frame without changing window bounds. Leaving hides it again; disabling the toggle restores the frame continuously.
- Alt snapping is applied before ImGui begins the window. During active drags of both windows, native panel vertex bounds match the saved outer window bounds on every tested frame. This catches the former problem where only the content moved after decoration had been drawn.
- Screenshots use actual ImGui geometry and game fonts with fixture telemetry. Small-window and hover images are under `artifacts/ui/redesign`.
- Installation backs up and atomically replaces DLL, PDB and README. Saved layouts and presets are preserved.

Release DLL SHA256: `6484C40733140B781B61A36D8A6B6BC36D6FD5EF3D25CFC0416C6D459B5DA80A`.
