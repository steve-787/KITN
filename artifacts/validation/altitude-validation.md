# Originale Höhenanzeige im T-Fenster

Validiert am 13.11.2026 mit KSA 2026.11.10.5801, StarMap API 0.5.4 und .NET 10.0.14.

Die Mod verwendet das originale `GaugeCanvas` mit der ID `Altitude`, dessen Draw-Callback, Ziffernbindungen, Shader und Schriftatlas. Nur das Zielrechteck liegt im T-Fenster. Ein vorübergehender Instanz-Prefix unterdrückt ausschließlich das ursprüngliche UI dieses Instruments; sein aktiver Zustand und sein HUD-Layout werden erhalten.

- Release-Build: 0 Warnungen, 0 Fehler (`altitude-release-build.txt`).
- Diagnostik und vorhandene Flugregelung: 64 bestandene Prüfungen (`altitude-diagnostics-tests.txt`).
- Native KSA-UI: 48 bestandene Prüfungen, darunter sieben Prüfungen der Höhenanzeige (`altitude-native-ui-tests.txt`).
- Native API und Lebenszyklus: fünf bestandene Prüfungen (`altitude-native-hook-tests.txt`).

Die Höhenprüfungen bestätigen den originalen Canvas und Callback, kopierte native Renderdaten mit geprüftem Strukturlayout, unveränderte Farbe und Seitenverhältnis, kleine und große Fenster, erreichbaren Countdown-Abbruch, HUD-Freigabe beim Schließen und Entladen sowie bei fehlendem Renderer oder Fahrzeug. Die Missionszeit läuft nach dem Schließen weiter.

Die UI-Prüfungen erzeugen native ImGui-Renderbefehle. Sie führen die Vulkan-Callbacks nicht auf einer GPU aus; ein Bildvergleich im laufenden Spiel wurde nicht durchgeführt. Alle Probes laden temporäre DLL-Kopien. Die installierte DLL wird nicht für `Diag.Init()` verwendet, damit echte Fluglogs erhalten bleiben.

Release-DLL und beide native Probe-Kopien haben denselben SHA256:
`3A10F1D5F9A0C4E88F2579CAB5CF30A561AD654AC3103F25828CE4EB9456689B`.

Installation: `install-altitude-release.ps1` sichert die bisherigen Mod-Dateien und ersetzt ausschließlich DLL, PDB und README atomar. Gespeicherte Vorlagen, Fensterlayouts und echte Logs werden erhalten. `altitude-installation.json` dokumentiert die geprüften Dateihashes und den Sicherungsordner. Das Spiel wird nicht geschlossen oder neu gestartet; die Änderung wird beim nächsten Spielstart geladen.
