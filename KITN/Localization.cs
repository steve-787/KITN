using System;
using System.Collections.Generic;
using System.Globalization;

namespace KITN
{
    /// <summary>Game-independent fallback catalog. Game language packs can override every stable key.</summary>
    internal static class L10n
    {
        internal static readonly Dictionary<string, (string English, string German)> Catalog =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal)
            {
                ["window.autopilot"] = ("KITN", "KITN"),
                ["window.mission_time"] = ("MISSION TIME", "MISSIONSZEIT"),
                ["window.instruments"] = ("KITN · FLIGHT INSTRUMENTS", "KITN · FLUGINSTRUMENTE"),
                ["instrument.altitude"] = ("ALTITUDE / METERS", "HÖHE / METER"),
                ["settings.open"] = ("Settings", "Einstellungen"),
                ["settings.open_hint"] = ("General mod settings. Flight plans and automation remain in their own tabs.", "Allgemeine Mod-Einstellungen. Flugplan und Automatik bleiben in ihren eigenen Reitern."),
                ["settings.back"] = ("Back to flight", "Zurück zum Flug"),
                ["settings.title"] = ("MOD SETTINGS", "MOD-EINSTELLUNGEN"),
                ["settings.theme"] = ("INSTRUMENT THEME", "INSTRUMENTENDESIGN"),
                ["settings.appearance"] = ("APPEARANCE", "DARSTELLUNG"),
                ["settings.auto_hide_frame"] = ("Auto-hide instrument frame", "Instrumentenrahmen ausblenden"),
                ["settings.auto_hide_frame_hint"] = ("Show only the instruments until you hover over their window. Hover to reveal its background, title and controls; drag the title or resize the corner.", "Zeigt nur die Instrumente, bis der Mauszeiger über dem Fenster liegt. Dann erscheinen Hintergrund, Titel und Bedienelemente. Am Titel verschieben oder an der Ecke verkleinern."),
                ["settings.retro"] = ("Retro", "Retro"),
                ["settings.modern"] = ("Modern", "Modern"),
                ["settings.retro_hint"] = ("Mechanical rolling digits for mission time and altitude.", "Mechanische Rollenziffern für Missionszeit und Höhe."),
                ["settings.modern_hint"] = ("Clean, simple readouts for mission time and altitude.", "Klare, schlichte Anzeigen für Missionszeit und Höhe."),
                ["settings.alignment_hint"] = ("Hold Alt while dragging either window to show center guides and snap into alignment.", "Beim Verschieben eines Fensters Alt halten, um Mittellinien und Einrasten zu aktivieren."),
                ["settings.saved"] = ("Saved automatically", "Automatisch gespeichert"),
                ["settings.saving"] = ("Saving…", "Wird gespeichert…"),
                ["settings.load_failed"] = ("Settings could not be read. Your file is preserved; reload it to enable saving.", "Einstellungen konnten nicht gelesen werden. Die Datei bleibt erhalten; zum Speichern erneut laden."),
                ["settings.save_failed"] = ("Could not save settings. Changes will be retried automatically.", "Einstellungen konnten nicht gespeichert werden. Wird automatisch erneut versucht."),
                ["settings.retry"] = ("Reload settings", "Einstellungen neu laden"),
                ["tab.flight"] = ("FLIGHT", "FLUG"),
                ["tab.plan"] = ("FLIGHT PLAN", "FLUGPLAN"),
                ["tab.automation"] = ("AUTOMATION", "AUTOMATIK"),
                ["tab.diagnostics"] = ("DIAGNOSTICS", "DIAGNOSE"),
                ["readout.target_orbit"] = ("TARGET ORBIT", "ZIELORBIT"),
                ["readout.phase"] = ("PHASE", "PHASE"),
                ["clock.countdown"] = ("Countdown", "Countdown"),
                ["clock.active"] = ("Countdown active", "Countdown aktiv"),
                ["clock.ready"] = ("Ready to launch", "Startbereit"),
                ["clock.cancelled"] = ("Countdown cancelled", "Countdown abgebrochen"),
                ["clock.stopped"] = ("Flight aborted", "Flug abgebrochen"),
                ["action.cancel_countdown"] = ("Cancel countdown", "Countdown abbrechen"),
                ["action.abort"] = ("Abort flight", "Flug abbrechen"),
                ["action.countdown"] = ("Start countdown", "Countdown starten"),
                ["action.countdown_hint"] = ("Starts a {0} second countdown. At zero, the autopilot takes control.", "Startet den Countdown mit {0} Sekunden. Bei null übernimmt der Autopilot."),
                ["action.timer"] = ("T display", "T-Anzeige"),
                ["action.timer_hint"] = ("Opens the separate mission clock. T- before launch, T+ after launch.", "Öffnet die separate Missionsuhr. Vor dem Start T-, nach dem Start T+."),
                ["action.reset"] = ("Reset", "Zurücksetzen"),
                ["action.reset_hint"] = ("Resets the mission clock and flight trace.", "Setzt Missionsuhr und Flugspur zurück."),
                ["action.fit"] = ("Show all", "Alles anzeigen"),
                ["action.warp"] = ("Skip to burn window", "Zum Brennfenster vorspulen"),
                ["route.live_scale"] = ("Auto scale", "Live-Skalierung"),
                ["route.live_scale_hint"] = ("Disable to zoom with the mouse wheel and pan the graph.", "Ausschalten, um im Graphen mit dem Mausrad zu zoomen und die Ansicht zu verschieben."),
                ["route.hint"] = ("Red: preview from the flight plan and rocket performance. White: measured flight. The preview simplifies flight physics.", "Rot: Vorschau aus Flugplan und Raketenleistung. Weiß: gemessener Flug. Die Vorschau vereinfacht die Flugphysik."),
                ["route.trace_starts"] = ("The flight trace begins at launch.", "Die Flugspur beginnt beim Start."),
                ["route.frozen_plan"] = ("Flight plan recorded at this flight's launch.", "Flugplan vom Start dieses Flugs."),
                ["route.distance_axis"] = ("Distance from launch (km)", "Strecke ab Start (km)"),
                ["route.height_axis"] = ("Altitude (km)", "Flughöhe (km)"),
                ["route.target"] = ("Target altitude (PE)", "Zielhöhe (PE)"),
                ["route.planned"] = ("Planned route (preview)", "Geplante Route (Vorschau)"),
                ["route.actual"] = ("Actual flight path", "Tatsächliche Flugbahn"),
                ["route.rocket"] = ("Rocket", "Rakete"),
                ["plan.locked"] = ("Flight plan locked during countdown and flight.", "Flugplan während Countdown und Flug gesperrt."),
                ["preset.section"] = ("FLIGHT PRESETS", "FLUGVORLAGEN"),
                ["preset.description"] = ("Keep your flight curve and launch settings for the next flight.", "Speichere deine Flugkurve und Starteinstellungen für den nächsten Flug."),
                ["preset.list"] = ("Preset", "Vorlage"),
                ["preset.default"] = ("Default LEO", "Standard LEO"),
                ["preset.custom"] = ("Custom flight plan", "Eigener Flugplan"),
                ["preset.load_hint"] = ("Select a preset to load its curve, target orbit, countdown and automation. Loading is locked during countdown and flight.", "Lädt Kurve, Zielorbit, Countdown und Automatik der gewählten Vorlage. Während Countdown und Flug ist das Laden gesperrt."),
                ["preset.name"] = ("Preset name", "Name der Vorlage"),
                ["preset.name_hint"] = ("Choose a name with 1–80 characters. Enter also saves. Use the same name to update a preset.", "Wähle einen Namen mit 1–80 Zeichen. Enter speichert ebenfalls. Derselbe Name aktualisiert eine Vorlage."),
                ["preset.save"] = ("Save preset", "Vorlage speichern"),
                ["preset.update"] = ("Update preset", "Vorlage aktualisieren"),
                ["preset.save_hint"] = ("Saves the current flight curve, orbit and launch settings. Presets stay available after restarting the game.", "Speichert die aktuelle Flugkurve, den Orbit und die Starteinstellungen. Vorlagen bleiben nach einem Spielneustart verfügbar."),
                ["preset.update_hint"] = ("Updates the preset with this name. The previous preset file is kept as a backup.", "Aktualisiert die Vorlage mit diesem Namen. Die vorherige Vorlagendatei bleibt als Sicherung erhalten."),
                ["preset.saved"] = ("Saved: {0}", "Gespeichert: {0}"),
                ["preset.loaded"] = ("Loaded: {0}", "Geladen: {0}"),
                ["preset.save_failed"] = ("Could not save the preset. Details are in the autopilot log.", "Vorlage konnte nicht gespeichert werden. Details stehen im Autopilot-Log."),
                ["preset.load_failed"] = ("Saved presets could not be read. The file is preserved; saving is disabled until it can be loaded.", "Gespeicherte Vorlagen konnten nicht gelesen werden. Die Datei bleibt erhalten; Speichern ist bis zum erfolgreichen Laden gesperrt."),
                ["preset.retry"] = ("Reload presets", "Vorlagen erneut laden"),
                ["plan.launch"] = ("LAUNCH SEQUENCE", "STARTSEQUENZ"),
                ["plan.launch_hint"] = ("At zero, the autopilot launches. The mission clock switches from T- to T+.", "Bei null startet der Autopilot. Die Missionsuhr wechselt dann von T- auf T+."),
                ["plan.countdown"] = ("Countdown (s)", "Countdown (s)"),
                ["plan.countdown_hint"] = ("1 to 3,600 seconds of game time. Pausing the game also pauses the mission clock.", "1 bis 3.600 Sekunden Spielzeit. Bei einer Spielpause steht die Missionsuhr ebenfalls still."),
                ["plan.countdown_invalid"] = ("Set the countdown to 1–3,600 seconds.", "Countdown auf 1–3.600 Sekunden einstellen."),
                ["plan.orbit_hint"] = ("Default: LEO, 200 × 200 km.", "Standard: LEO, 200 × 200 km."),
                ["plan.leo"] = ("LEO 200 × 200 km", "LEO 200 × 200 km"),
                ["plan.apoapsis"] = ("Apoapsis (km)", "Apoapsis (km)"),
                ["plan.apoapsis_hint"] = ("Highest orbital point. At least as high as periapsis.", "Höchster Bahnpunkt. Mindestens so hoch wie die Periapsis."),
                ["plan.periapsis"] = ("Periapsis (km)", "Periapsis (km)"),
                ["plan.periapsis_hint"] = ("Lowest orbital point. At least 100 km.", "Tiefster Bahnpunkt. Mindestens 100 km."),
                ["plan.orbit_invalid"] = ("Set apoapsis ≥ periapsis ≥ 100 km.", "Apoapsis ≥ Periapsis ≥ 100 km einstellen."),
                ["plan.turn"] = ("GRAVITY TURN", "GRAVITY TURN"),
                ["plan.turn_hint"] = ("After climbing vertically, the rocket gradually tilts towards the horizon.", "Nach dem senkrechten Aufstieg neigt sich die Rakete schrittweise zum Horizont."),
                ["plan.turn_start"] = ("Turn start (m)", "Beginn der Kurve (m)"),
                ["plan.turn_start_hint"] = ("Altitude at which the rocket may start turning once it has enough speed.", "Höhe, ab der die Rakete mit ausreichendem Tempo kippen darf."),
                ["plan.turn_end"] = ("Turn end (m)", "Ende der Kurve (m)"),
                ["plan.turn_end_hint"] = ("The profile aims for horizontal flight above this altitude.", "Ab dieser Höhe sieht das Profil eine horizontale Ausrichtung vor."),
                ["plan.min_speed"] = ("Minimum speed (m/s)", "Mindesttempo (m/s)"),
                ["plan.min_speed_hint"] = ("Prevents turning too early at low speed.", "Verhindert zu frühes Kippen bei geringer Geschwindigkeit."),
                ["plan.heading"] = ("Launch heading (°)", "Startrichtung (°)"),
                ["plan.heading_hint"] = ("0° north, 90° east, 180° south, 270° west.", "0° Norden, 90° Osten, 180° Süden, 270° Westen."),
                ["plan.turn_invalid"] = ("Check the turn: end above start, altitudes and minimum speed ≥ 0, heading 0–360°.", "Kurve prüfen: Ende über Beginn, Höhen und Mindesttempo ≥ 0, Richtung 0–360°."),
                ["plan.shape"] = ("Turn shape", "Kurvenform"),
                ["plan.shape_hint"] = ("Higher values move the turn to higher altitudes.", "Höhere Werte verlagern die Kurve nach oben."),
                ["plan.pitch_rate"] = ("Initial pitch rate (°/s)", "Kipprate unten (°/s)"),
                ["plan.pitch_rate_hint"] = ("Limits how quickly the rocket tilts during the lower ascent.", "Begrenzt die Neigungsgeschwindigkeit im unteren Aufstieg."),
                ["automation.locked"] = ("Automation locked during countdown and flight.", "Automatik während Countdown und Flug gesperrt."),
                ["automation.stages"] = ("ROCKET STAGES", "RAKETENSTUFEN"),
                ["automation.stages_hint"] = ("Drops empty side boosters and changes stage when the active stage runs out of propellant.", "Wirft leere Seitenbooster ab und wechselt die Stufe, wenn der Treibstoff der aktiven Stufe endet."),
                ["automation.auto_stage"] = ("Automatic staging", "Stufen automatisch wechseln"),
                ["automation.hot_stage"] = ("Hot staging", "Hot Staging"),
                ["automation.hot_stage_hint"] = ("Ignites the next stage shortly before the previous one runs out.", "Zündet die nächste Stufe kurz vor dem Ende der vorherigen."),
                ["automation.fuel_threshold"] = ("Propellant left at staging (%)", "Resttreibstoff beim Wechsel (%)"),
                ["automation.warp"] = ("TIME WARP", "ZEITRAFFER"),
                ["automation.warp_hint"] = ("Shortens the coast until just before the burn window.", "Verkürzt den Freiflug bis kurz vor das Brennfenster."),
                ["automation.skip"] = ("Skip to orbit insertion", "Zum Einschuss vorspulen"),
                ["clock.altitude_unavailable"] = ("Altitude instrument unavailable", "Höheninstrument nicht verfügbar"),
                ["diagnostics.hint"] = ("Additional physics readings for troubleshooting.", "Zusätzliche Physikwerte für die Fehlersuche."),
                ["diagnostics.vertical_speed"] = ("CLIMB RATE", "STEIGRATE"),
                ["diagnostics.pressure"] = ("DYNAMIC PRESSURE", "DYNAMISCHER DRUCK"),
                ["diagnostics.acceleration"] = ("ACCELERATION", "BESCHLEUNIGUNG"),
                ["diagnostics.pitch"] = ("PITCH ACTUAL / TARGET", "NEIGUNG IST / SOLL"),
                ["diagnostics.trace"] = ("FLIGHT TRACE", "FLUGSPUR"),
                ["diagnostics.points"] = ("{0:N0} samples", "{0:N0} Messpunkte"),
                ["diagnostics.logs"] = ("FLIGHT LOGS", "FLUGLOGS"),
                ["diagnostics.logs_hint"] = ("Record position, physics and control commands.", "Position, Physik und Steuerbefehle protokollieren."),
                ["diagnostics.telemetry"] = ("Record telemetry", "Telemetrie protokollieren"),
                ["diagnostics.interval"] = ("Log interval (s)", "Log-Intervall (s)"),
                ["diagnostics.csv"] = ("Also save telemetry as CSV", "Telemetrie zusätzlich als CSV"),
                ["state.idle"] = ("Ready", "Bereit"),
                ["state.vertical"] = ("Vertical ascent", "Senkrechter Aufstieg"),
                ["state.turn"] = ("Gravity turn", "Gravity Turn"),
                ["state.coast"] = ("Coasting to burn window", "Freiflug zum Brennfenster"),
                ["state.insertion"] = ("Orbit insertion", "Orbit einschießen"),
                ["state.orbit"] = ("Target orbit reached / autopilot off", "Zielorbit erreicht / Autopilot aus"),
                ["state.aborted"] = ("Aborted", "Abgebrochen"),
                ["vehicle.none"] = ("No rocket selected", "Keine Rakete ausgewählt"),
                ["display.unavailable"] = ("Flight display temporarily unavailable.", "Fluganzeige vorübergehend nicht verfügbar."),
                ["display.body_changed"] = ("Reference body changed; the previous flight trace has been preserved.", "Referenzkörper gewechselt; die bisherige Flugspur bleibt gespeichert."),
                ["preview.select_vehicle"] = ("Select a rocket to preview the route.", "Wähle eine Rakete für die Routenvorschau."),
                ["planning.pending"] = ("Calculating a flight route for this rocket…", "Flugroute für diese Rakete wird berechnet…"),
                ["planning.phase_pending"] = ("Planning flight", "Flugplanung läuft"),
                ["planning.phase_failed"] = ("Flight planning error", "Fehler bei der Flugplanung"),
                ["planning.ready"] = ("Rocket-adapted route: turn {0:F1}–{1:F1} km; estimated fuel reserve {2:N0} m/s.", "An Rakete angepasste Route: Turn {0:F1}–{1:F1} km; geschätzte Treibstoffreserve {2:N0} m/s."),
                ["planning.invalid_settings"] = ("Flight planning error: check target orbit, turn heights, pitch rate and countdown.", "Fehler bei der Flugplanung: Zielorbit, Turn-Höhen, Kipprate und Countdown prüfen."),
                ["planning.no_computer"] = ("Flight planning error: this rocket has no flight computer. Add a control module.", "Fehler bei der Flugplanung: Rakete hat keinen Flight Computer. Ein Steuerungsmodul hinzufügen."),
                ["planning.no_steering"] = ("Flight planning error: no gimballed engines or usable attitude RCS. Add steering for pitch and yaw.", "Fehler bei der Flugplanung: Keine schwenkbaren Triebwerke oder nutzbare RCS-Steuerung. Steuerung für Neigung und Richtung hinzufügen."),
                ["planning.no_engines"] = ("Flight planning error: no usable burn stages. Check engines, propellant and the staging sequence.", "Fehler bei der Flugplanung: Keine nutzbaren Brennstufen. Triebwerke, Treibstoff und Stufenfolge prüfen."),
                ["planning.no_physics"] = ("Flight planning error: the body's gravity or the rocket's position cannot be read.", "Fehler bei der Flugplanung: Gravitation oder Raketenposition können nicht gelesen werden."),
                ["planning.invalid_performance"] = ("Flight planning error: a burn stage has invalid thrust, fuel flow or remaining mass.", "Fehler bei der Flugplanung: Eine Brennstufe hat ungültigen Schub, Treibstofffluss oder Restmasse."),
                ["planning.low_twr"] = ("Flight planning error: launch thrust is too low (thrust/weight {0:F2}; more than 1.02 required). Increase thrust or reduce mass.", "Fehler bei der Flugplanung: Startschub zu gering (Schub/Gewicht {0:F2}; mehr als 1,02 erforderlich). Mehr Schub oder weniger Masse."),
                ["planning.low_fuel"] = ("Flight planning error: fuel provides {0:N0} m/s, below the minimum {1:N0} m/s before flight losses. Add propellant or reduce mass.", "Fehler bei der Flugplanung: Treibstoff liefert {0:N0} m/s; mindestens {1:N0} m/s sind schon ohne Flugverluste nötig. Mehr Treibstoff oder weniger Masse."),
                ["planning.target_in_atmosphere"] = ("Flight planning error: target periapsis {0:F1} km is below the safe atmospheric limit {1:F1} km.", "Fehler bei der Flugplanung: Ziel-Periapsis {0:F1} km liegt unter der sicheren Atmosphärengrenze {1:F1} km."),
                ["planning.too_many_phases"] = ("Flight planning error: more than 127 burn phases; simplify the staging sequence.", "Fehler bei der Flugplanung: Mehr als 127 Brennphasen; Stufenfolge vereinfachen."),
                ["planning.manual_stage"] = ("Flight planning error: automatic staging is off and the first stage cannot reach the orbit. Enable automatic staging.", "Fehler bei der Flugplanung: Automatischer Stufenwechsel ist aus; die erste Stufe erreicht den Orbit nicht. Auto-Staging einschalten."),
                ["planning.fuel_exhausted"] = ("Flight planning error: fuel is exhausted before the target (best AP {0:F1} / PE {1:F1} km). Add fuel, reduce mass or improve thrust.", "Fehler bei der Flugplanung: Treibstoff endet vor dem Ziel (beste AP {0:F1} / PE {1:F1} km). Mehr Treibstoff, weniger Masse oder besseren Schub verwenden."),
                ["planning.no_route"] = ("Flight planning error: no stable route to the target was found (best AP {0:F1} / PE {1:F1} km). Check thrust, mass and steering.", "Fehler bei der Flugplanung: Keine stabile Route zum Ziel gefunden (beste AP {0:F1} / PE {1:F1} km). Schub, Masse und Steuerung prüfen."),
                ["planning.calculation_failed"] = ("Flight planning error: rocket performance could not be calculated. Details are in the autopilot log.", "Fehler bei der Flugplanung: Raketenleistung konnte nicht berechnet werden. Details stehen im Autopilot-Log."),
                ["planning.start_blocked"] = ("Launch blocked: {0}", "Start gesperrt: {0}"),
                ["preview.no_stages"] = ("No burn stages available for the preview.", "Keine Brennstufen für die Vorschau verfügbar."),
                ["preview.orbit_invalid"] = ("Check the target orbit: apoapsis ≥ periapsis ≥ 100 km.", "Zielorbit prüfen: Apoapsis ≥ Periapsis ≥ 100 km."),
                ["preview.insufficient_performance"] = ("Stage performance is insufficient for a preview.", "Stufenleistung reicht für eine Vorschau nicht aus."),
                ["preview.ends_early"] = ("Preview ends before reaching the target orbit.", "Vorschau endet vor dem Zielorbit."),
                ["preview.complete"] = ("Route preview to the target orbit.", "Routenvorschau bis zum Zielorbit."),
                ["preview.unstable"] = ("This preview does not reach a stable target orbit.", "Diese Vorschau erreicht keinen stabilen Zielorbit."),
                ["preview.fuel_exhausted"] = ("Preview: propellant runs out before the target orbit.", "Vorschau: Treibstoff endet vor dem Zielorbit."),
                ["preview.manual_stage"] = ("Preview stops at the first stage; automatic staging is disabled.", "Vorschau endet an der ersten Stufe; automatischer Stufenwechsel ist aus."),
                ["preview.unavailable"] = ("Route preview unavailable; the actual flight trace continues recording.", "Routenvorschau nicht verfügbar; die echte Flugspur wird weiter aufgezeichnet."),
                ["preview.no_performance"] = ("No stage performance available for the preview.", "Keine Stufenleistung für die Vorschau verfügbar."),
                ["status.ready"] = ("Ready to launch", "Bereit zum Start"),
                ["status.launched"] = ("Launch sequence activated — vertical ascent", "Startsequenz aktiviert — vertikaler Aufstieg"),
                ["status.aborted"] = ("Ascent aborted", "Aufstieg abgebrochen"),
                ["status.manual_warp"] = ("Manual skip to apoapsis started…", "Manueller Zeitsprung zum Apoapsis gestartet…"),
                ["status.invalid_orbit"] = ("Invalid target orbit: apoapsis ≥ periapsis ≥ 100 km required", "Zielorbit ungültig: Apoapsis ≥ Periapsis ≥ 100 km erforderlich"),
                ["status.turn_started"] = ("Gentle gravity turn started", "Sanfter Gravity Turn eingeleitet"),
                ["status.vertical"] = ("Vertical ascent: {0:F1} / {1:F1} km | {2:F0} / {3:F0} m/s", "Vertikaler Aufstieg: {0:F1} / {1:F1} km | {2:F0} / {3:F0} m/s"),
                ["status.no_computer"] = ("Gravity turn aborted: no flight computer", "Gravity Turn abgebrochen: kein Flugcomputer"),
                ["status.ap_reached"] = ("Target apoapsis reached — coasting to apoapsis", "Ziel-Apoapsis erreicht — Freiflug zum Apoapsis"),
                ["status.turn"] = ("Gravity turn — target: {0:F1}° | actual: {1:F1}° | AP: {2:F1} km", "Gravity Turn — Soll: {0:F1}° | Ist: {1:F1}° | AP: {2:F1} km"),
                ["status.insertion_ready"] = ("Orbit insertion prepared — checking alignment", "Orbit-Einschuss vorbereitet — Ausrichtung prüfen"),
                ["status.no_thrust"] = ("Orbit insertion unavailable: no usable thrust", "Orbit-Einschuss nicht möglich: kein verfügbarer Schub"),
                ["status.coast"] = ("Coasting — apoapsis T-{0:F0}s | burn {1:F0}s | alignment {2:F1}°", "Freiflug — Apoapsis T-{0:F0}s | Burn {1:F0}s | Ausrichtung {2:F1}°"),
                ["status.reentry"] = ("Insertion aborted: re-entry without reaching the target orbit", "Einschuss abgebrochen: Wiedereintritt ohne Zielorbit"),
                ["status.attitude_failed"] = ("Insertion aborted: rocket is not following the attitude target", "Einschuss abgebrochen: Rakete folgt dem Lageziel nicht"),
                ["status.aligning"] = ("Insertion: aligning ({0:F1}°)", "Einschuss: Ausrichten ({0:F1}°)"),
                ["status.insertion_failed"] = ("Insertion aborted: target orbit is not getting closer", "Einschuss abgebrochen: Zielorbit nähert sich nicht"),
                ["status.insertion"] = ("Orbit insertion — Δv {0:F0} m/s | AP {1:F1} / PE {2:F1} km", "Orbit-Einschuss — Δv {0:F0} m/s | AP {1:F1} / PE {2:F1} km"),
                ["status.trim_wait"] = ("Periapsis secured — trimming at {0} in {1:F0}s", "Periapsis gesichert — Feinkorrektur am {0} in {1:F0}s"),
                ["apsis.apoapsis"] = ("apoapsis", "Apoapsis"),
                ["apsis.periapsis"] = ("periapsis", "Periapsis"),
                ["status.trim_failed"] = ("Trim stopped: target orbit is not getting closer", "Feinkorrektur beendet: Zielorbit nähert sich nicht"),
                ["status.trim"] = ("Orbit trim — AP {0:F1} / PE {1:F1} km", "Orbit-Feinkorrektur — AP {0:F1} / PE {1:F1} km"),
                ["status.orbit"] = ("Orbit reached! AP {0:F1} / PE {1:F1} km", "Orbit erreicht! AP {0:F1} / PE {1:F1} km"),
                ["status.no_hook"] = ("Control unavailable: the physics hook could not be registered", "Steuerung nicht verfügbar: Physik-Hook konnte nicht eingerichtet werden"),
                ["status.stage"] = ("Automatic staging at T+{0:F1}s", "Automatischer Stufenwechsel bei T+{0:F1}s"),
                ["status.hot_stage"] = ("Hot staging at T+{0:F1}s", "Hot Staging bei T+{0:F1}s"),
                ["status.booster_drop"] = ("Empty side booster separation requested", "Abwurf eines leeren Seitenboosters ausgelöst"),
                ["tab.landing"] = ("LANDING", "LANDUNG"),
                ["landing.section"] = ("BOOSTER RETURN", "BOOSTER-RÜCKKEHR"),
                ["landing.description"] = ("Return the currently controlled vehicle from Earth orbit to a launch pad. Select the booster in the game, choose its destination, then start landing.", "Bringt das aktuell gesteuerte Fahrzeug aus dem Erdorbit zu einer Startrampe zurück. Wähle den Booster im Spiel, dann seinen Landeplatz und starte die Landung."),
                ["landing.target"] = ("LANDING SITE", "LANDEPLATZ"),
                ["landing.coordinates"] = ("Coordinates", "Koordinaten"),
                ["landing.available_dv"] = ("Available delta-v", "Verfügbares Delta-v"),
                ["landing.required_dv"] = ("Return + landing reserve", "Rückkehr + Landereserve"),
                ["landing.auto_warp"] = ("Warp to return window", "Zeitraffer zum Rückflugfenster"),
                ["landing.auto_warp_hint"] = ("Waits for a reachable return window and stops warp before the burn. Entry and landing use normal simulation speed.", "Wartet auf ein erreichbares Rückflugfenster und beendet den Zeitraffer vor dem Burn. Wiedereintritt und Landung laufen mit normaler Spielgeschwindigkeit."),
                ["landing.start"] = ("Start landing", "Landung starten"),
                ["landing.abort"] = ("Abort landing", "Landung abbrechen"),
                ["landing.start_hint"] = ("Plans a targeted deorbit burn, entry corrections and powered touchdown. Requires usable reserve fuel, restartable main engines and sufficient landing thrust.", "Plant einen gezielten Deorbit-Burn, Kurskorrekturen und die angetriebene Landung. Benötigt nutzbaren Reservetreibstoff, erneut zündbare Haupttriebwerke und ausreichenden Landeschub."),
                ["landing.status_hint"] = ("Return phases, fuel budget, predicted miss, braking height and touchdown confirmation are recorded in LANDING logs and the telemetry CSV.", "Rückflugphasen, Treibstoffbudget, vorausberechnete Abweichung, Bremshöhe und bestätigter Bodenkontakt stehen im LANDING-Log und in der Telemetrie-CSV."),
                ["landing.ready"] = ("Choose a launch pad to begin return planning.", "Wähle eine Startrampe für die Rückflugplanung."),
                ["landing.require_vehicle"] = ("Select the vehicle you want to land in the game.", "Wähle das Fahrzeug im Spiel aus, das landen soll."),
                ["landing.require_earth"] = ("Booster return is available around Earth.", "Die Booster-Rückkehr ist um die Erde verfügbar."),
                ["landing.require_orbit"] = ("Start the return from a stable Earth orbit above the atmosphere.", "Starte die Rückkehr aus einem stabilen Erdorbit oberhalb der Atmosphäre."),
                ["landing.require_control"] = ("The selected vehicle needs a control module and a flight computer.", "Das ausgewählte Fahrzeug benötigt ein Kontrollmodul und einen Flugcomputer."),
                ["landing.require_manual_burn"] = ("Stop the game's current automatic burn before starting landing.", "Beende den aktuellen automatischen Burn des Spiels vor dem Landestart."),
                ["landing.require_pad"] = ("No launch pads are registered for this Earth.", "Für diese Erde sind keine Startrampen registriert."),
                ["landing.require_engines"] = ("Activate a stage with restartable liquid main engines.", "Aktiviere eine Stufe mit erneut zündbaren Flüssig-Haupttriebwerken."),
                ["landing.require_fuel"] = ("The active engines need usable propellant reserved for return and landing.", "Die aktiven Triebwerke benötigen nutzbaren Reservetreibstoff für Rückkehr und Landung."),
                ["landing.require_thrust"] = ("The available thrust cannot support this vehicle against Earth's gravity.", "Der verfügbare Schub kann dieses Fahrzeug nicht gegen die Erdgravitation halten."),
                ["landing.require_idle"] = ("Wait until ascent or the countdown has finished.", "Warte, bis der Aufstieg oder der Countdown beendet ist."),
                ["landing.planning"] = ("Planning the return to the selected launch pad.", "Rückflug zur ausgewählten Startrampe wird geplant."),
                ["landing.planning_progress"] = ("Searching return windows: {0:F0}%", "Rückflugfenster werden geprüft: {0:F0}%"),
                ["landing.waiting"] = ("Return window found; waiting for the deorbit burn.", "Rückflugfenster gefunden; wartet auf den Deorbit-Burn."),
                ["landing.waiting_time"] = ("Return to {0} · burn {1}", "Rückflug nach {0} · Burn {1}"),
                ["landing.deorbit"] = ("Targeted deorbit burn in progress.", "Gezielter Deorbit-Burn läuft."),
                ["landing.entry"] = ("Entering the atmosphere toward the selected launch pad.", "Wiedereintritt in Richtung der ausgewählten Startrampe."),
                ["landing.entry_target"] = ("Entry toward {0} · predicted miss {1:F1} km", "Wiedereintritt nach {0} · vorausberechnete Abweichung {1:F1} km"),
                ["landing.descent"] = ("Braking and guiding the final descent.", "Bremst und steuert den letzten Landeanflug."),
                ["landing.descent_target"] = ("Landing at {0} · distance {1:F0} m · clearance {2:F1} m", "Landung auf {0} · Abstand {1:F0} m · Bodenfreiheit {2:F1} m"),
                ["landing.landed"] = ("Touchdown confirmed at the selected pad. Autopilot off; mission time continues.", "Landung auf dem gewählten Platz bestätigt. Autopilot aus; die Missionszeit läuft weiter."),
                ["landing.cancelled"] = ("Landing aborted; control released. Mission time continues.", "Landung abgebrochen; Steuerung freigegeben. Die Missionszeit läuft weiter."),
                ["landing.vehicle_changed"] = ("Return stopped because the vehicle or parent body changed.", "Rückflug beendet, weil das Fahrzeug oder der Zentralkörper gewechselt wurde."),
                ["landing.time_reverted"] = ("Return stopped after a simulation-time revert.", "Rückflug nach einem Rücksprung der Spielzeit beendet."),
                ["landing.fuel_exhausted"] = ("Landing stopped: the active engines ran out of usable propellant.", "Landung beendet: Die aktiven Triebwerke haben keinen nutzbaren Treibstoff mehr."),
                ["landing.no_window"] = ("No return window fits the available fuel and landing reserve. Choose another pad or reserve more propellant for the next flight.", "Kein Rückflugfenster passt zum Treibstoffbudget und der Landereserve. Wähle einen anderen Platz oder reserviere beim nächsten Flug mehr Treibstoff."),
                ["landing.attitude_failed"] = ("Return stopped: the vehicle could not align for the burn.", "Rückflug beendet: Das Fahrzeug konnte sich nicht für den Burn ausrichten."),
                ["landing.failed"] = ("Return stopped because guidance could not reach its target. Details are in the LANDING log.", "Rückflug beendet, weil die Regelung ihr Ziel nicht erreichen konnte. Details stehen im LANDING-Log."),
                ["landing.wait_physics"] = ("Waiting for the vehicle's physics simulation at normal speed.", "Wartet bei normaler Geschwindigkeit auf die Physiksimulation des Fahrzeugs."),
                ["landing.off_target"] = ("Vehicle landed outside the selected pad; control released.", "Fahrzeug außerhalb des gewählten Platzes gelandet; Steuerung freigegeben."),
                ["landing.phase.idle"] = ("Return ready", "Rückkehr bereit"),
                ["landing.phase.planning"] = ("Planning return", "Rückflugplanung"),
                ["landing.phase.waiting"] = ("Waiting for return window", "Wartet auf Rückflugfenster"),
                ["landing.phase.deorbit"] = ("Deorbit burn", "Deorbit-Burn"),
                ["landing.phase.entry"] = ("Atmospheric entry", "Wiedereintritt"),
                ["landing.phase.descent"] = ("Powered landing", "Angetriebene Landung"),
                ["landing.phase.landed"] = ("Landed / autopilot off", "Gelandet / Autopilot aus"),
                ["landing.phase.aborted"] = ("Return aborted", "Rückflug abgebrochen")
            };

        public static string Locale { get; private set; } = "en-US";
        public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
        internal static Func<string, string> ExternalTranslation;

        public static void SetLocale(string locale)
        {
            try { Culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Replace('_', '-')); }
            catch (CultureNotFoundException) { Culture = CultureInfo.GetCultureInfo("en-US"); }
            if (Culture.Equals(CultureInfo.InvariantCulture)) Culture = CultureInfo.GetCultureInfo("en-US");
            Locale = Culture.Name;
        }

        public static string Get(string key)
        {
            if (!Catalog.TryGetValue(key, out var entry)) return key;
            string translated = ExternalTranslation?.Invoke(key);
            if (!string.IsNullOrWhiteSpace(translated)) return translated;
            return Culture.TwoLetterISOLanguageName == "de" ? entry.German : entry.English;
        }

        public static string Format(string key, params object[] values)
        {
            string template = Get(key);
            try { return string.Format(Culture, template, values); }
            catch (FormatException)
            {
                // An incomplete language pack must not interrupt the flight controller.
                return Catalog.TryGetValue(key, out var entry) ? string.Format(Culture, entry.English, values) : key;
            }
        }

        public static string Id(string key) => Get(key) + "###" + key;
    }

    /// <summary>Retains the translation key so cached status/preview messages follow a language change.</summary>
    internal sealed class LocalizedMessage
    {
        private readonly string _key, _literal;
        private readonly object[] _values;
        public LocalizedMessage(string key, params object[] values) { _key = key; _values = values; }
        private LocalizedMessage(string literal, bool isLiteral) { _literal = literal; }
        public static LocalizedMessage Literal(string text) => new LocalizedMessage(text, true);
        public override string ToString() => _key == null ? _literal : _values.Length == 0 ? L10n.Get(_key) : L10n.Format(_key, _values);
    }
}
