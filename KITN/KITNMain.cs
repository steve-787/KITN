using System;
using System.IO;
using System.Reflection;
using StarMap.API;
using StarMap.API.Services;
using KSA;

namespace KITN
{
    [StarMapMod]
    public class KITNMain
    {
        private LaunchProfile _profile;
        private StagingController _staging;
        private AscentGuidance _guidance;
        private bool _windowOpen = true;
        private Vehicle _lastControlledVehicle;
        private bool _vehicleObserved;
        private readonly IPatchService _patches;
        private IDisposable _physicsHook;
        private double _playerTime;
        private long _physicsHookCalls;
        private string _pendingAction;
        private readonly FlightDisplay _flightDisplay = new FlightDisplay();
        private readonly AutopilotWindow _window = new AutopilotWindow();
        private readonly MissionClock _missionClock = new MissionClock();
        private readonly MissionTimeWindow _missionTimeWindow = new MissionTimeWindow();
        private MissionAltitudeDisplay _missionAltitude;
        private readonly LandingController _landing = new LandingController();
        private UiLayoutStore _uiLayout;
        private bool _missionTimeOpen;
        private IParentBody _countdownParent;

        public KITNMain(IPatchService patches)
        {
            _patches = patches;
        }

        [StarMapImmediateLoad]
        public void Init(KSA.Mod definingMod)
        {
            Diag.Init();
            GameLocalization.Refresh();
            Diag.DumpApi();
            _profile = new LaunchProfile();
            string modDirectory = Path.GetDirectoryName(typeof(KITNMain).Assembly.Location) ?? ".";
            _uiLayout = new UiLayoutStore(Path.Combine(modDirectory, "ui_layout.json"));
            if (_uiLayout.Load()) Diag.Info("UI_LAYOUT", $"event=loaded windows={_uiLayout.WindowCount}");
            else Diag.Error("UI_LAYOUT", _uiLayout.LastError, "Loading window positions failed; using the default layout and preserving the file");
            _window.Layout = new PersistentWindowLayout(_uiLayout, "autopilot");
            _missionTimeWindow.Layout = new PersistentWindowLayout(_uiLayout, "mission_time");
            _window.Settings = _missionTimeWindow.Settings = _uiLayout;
            _window.Layout.Peer = _missionTimeWindow.Layout;
            _missionTimeWindow.Layout.Peer = _window.Layout;
            try
            {
                _missionAltitude = new MissionAltitudeDisplay(_patches, () => _missionTimeOpen);
                _missionTimeWindow.Altitude = _missionAltitude;
            }
            catch (Exception ex)
            {
                Diag.Error("ALTITUDE_DISPLAY", ex, "The original altitude display cannot be hosted; the game HUD is preserved");
            }
            var presets = new FlightPresetStore(Path.Combine(modDirectory, "flight_presets.json"));
            _window.Presets = presets;
            if (presets.Load()) Diag.Info("PRESET", $"event=loaded count={presets.Presets.Count}");
            else Diag.Error("PRESET", presets.LastError, "Loading saved flight plans failed; the file is preserved");

            // Korrigiert: StagingController ist eine Klasse und muss mit 'new' instanziiert werden
            _staging = new StagingController(_profile);

            _guidance = new AscentGuidance(_profile, _staging);
            try
            {
                MethodInfo target = typeof(Universe).GetMethod(nameof(Universe.ProcessAutoWarp),
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(double) }, null);
                if (target == null) throw new MissingMethodException("Universe.ProcessAutoWarp(double)");
                // PrepareFrame has joined/applied the previous workers at this point. Commands are
                // copied into the next worker; warp stops also affect the upcoming simulation step.
#pragma warning disable PatchHelperExperimental
                _physicsHook = _patches.RegisterStaticVoidPatch(IPatchService.PatchType.Postfix, target, BeforeVehiclePhysics);
#pragma warning restore PatchHelperExperimental
                Diag.Info("CONTROL_HOOK", "registered=true phase=after_auto_warp_before_job_sim_step main_thread=true");
            }
            catch (Exception ex)
            {
                _profile.SetStatus("status.no_hook");
                Diag.Error("CONTROL_HOOK", ex, "Autopilot cannot start without the synchronized physics hook");
            }
            Diag.Info("LIFECYCLE", "Mod successfully loaded via StarMap; telemetry defaults to one sample per 0.5 seconds.");
        }

        [StarMapAllModsLoaded]
        public void OnAllLoaded()
        {
            GameLocalization.Refresh();
            Diag.Info("LIFECYCLE", "Ready for automated spaceflight.");
        }

        [StarMapAfterOnFrame]
        public void OnFrame(double currentPlayerTime, double dtPlayer)
        {
            _playerTime = currentPlayerTime;
            if (_missionClock.HasLaunched) _missionClock.Advance(Universe.GetElapsedSeconds());
        }

        private bool BeforeVehiclePhysics(object[] args)
        {
            double dtPlayer = (double)args[0];
            _physicsHookCalls++;
            if (_physicsHookCalls == 1)
                Diag.Info("CONTROL_HOOK", "event=first_guidance_callback execution=before_physics_snapshot");
            Vehicle vehicle = Program.ControlledVehicle;
            ObserveVehicle(vehicle);
            try { _landing.Refresh(vehicle); }
            catch (Exception ex)
            {
                Diag.EveryError("landing_availability", 5, "LANDING", ex, "Reading landing availability failed");
                if (_landing.IsActive) _landing.Abort(vehicle, "landing_availability_exception", "landing.failed");
            }
            string action = _pendingAction;
            if (!_guidance.IsActive && !_landing.IsActive && _guidance.CurrentState == AscentGuidance.AscentState.Idle)
                _flightDisplay.RefreshLaunchPlan(vehicle, _profile, action == "countdown");
            double simulationTime = Universe.GetElapsedSeconds();
            bool blockedCountdown = action == "cancel_countdown" || action == "abort" || action == "reset";
            if (_missionClock.IsCountingDown && (vehicle == null || !ReferenceEquals(vehicle.Parent, _countdownParent)))
            {
                _missionClock.CancelCountdown();
                Diag.Info("COUNTDOWN", "event=cancelled reason=vehicle_or_parent_changed");
            }
            if (blockedCountdown) _missionClock.CancelCountdown();
            if (_missionClock.Advance(simulationTime) && vehicle != null) action = "start";
            if (vehicle != null && _guidance != null)
            {
                try
                {
                    switch (action)
                    {
                        case "countdown":
                            if (!_guidance.IsActive && !_landing.IsActive)
                            {
                                _flightDisplay.RefreshLaunchPlan(vehicle, _profile, true);
                                if (!_flightDisplay.CanLaunch(_profile))
                                {
                                    _profile.SetStatus("planning.start_blocked", _flightDisplay.PlanningMessage(_profile));
                                    Diag.Warn("FLIGHT_PLAN", "event=start_blocked reason=flight_plan_not_ready");
                                    break;
                                }
                                _missionClock.BeginCountdown(_profile.LaunchCountdownSeconds, simulationTime);
                                _countdownParent = vehicle.Parent;
                                _missionTimeOpen = true;
                                Diag.Info("COUNTDOWN", $"event=started duration_s={_profile.LaunchCountdownSeconds} vehicle=\"{vehicle.Id}\"");
                            }
                            break;
                        case "cancel_countdown":
                            Diag.Info("COUNTDOWN", "event=cancelled reason=user_cancel");
                            break;
                        case "start":
                            if (_landing.IsActive) break;
                            _flightDisplay.RefreshLaunchPlan(vehicle, _profile, true);
                            if (!_flightDisplay.CanLaunch(_profile))
                            {
                                _missionClock.CancelCountdown();
                                _profile.SetStatus("planning.start_blocked", _flightDisplay.PlanningMessage(_profile));
                                Diag.Warn("FLIGHT_PLAN", "event=countdown_cancelled reason=rocket_or_plan_changed");
                                action = null;
                                break;
                            }
                            _landing.Reset(vehicle);
                            _guidance.Start(_flightDisplay.LaunchPlan.Settings);
                            _missionClock.MarkLaunched(simulationTime);
                            Diag.Info("COUNTDOWN", "event=launch time=T+00:00:00");
                            break;
                        case "abort": _guidance.Abort(vehicle); break;
                        case "reset": _landing.Reset(vehicle); _guidance.Reset(vehicle); _missionClock.Reset(); break;
                        case "warp": _guidance.TriggerManualWarpToApoapsis(vehicle); break;
                        case "landing_start":
                            if (!_guidance.IsActive && !_missionClock.IsCountingDown && _landing.Start(vehicle, simulationTime))
                            {
                                if (!_missionClock.HasLaunched)
                                {
                                    double launchTime = (double)vehicle.LaunchGameTime.Nanoseconds / (double)UniverseTime.NANOSECONDS_PER_SECOND;
                                    _missionClock.MarkLaunched(double.IsFinite(launchTime) && launchTime >= 0 && launchTime <= simulationTime ? launchTime : simulationTime);
                                    _missionClock.Advance(simulationTime);
                                }
                                _missionTimeOpen = true;
                            }
                            break;
                        case "landing_abort": if (_landing.IsActive) _landing.Abort(vehicle); break;
                    }
                    _pendingAction = null;
                    _landing.Update(vehicle, simulationTime, dtPlayer);
                    _guidance.LandingDebug = _landing.Telemetry(_missionClock);
                    _guidance.Update(vehicle, _playerTime + dtPlayer, dtPlayer);
                }
                catch (Exception ex)
                {
                    Diag.EveryError("guidanceFrame", 5.0, "GUIDANCE", ex,
                        $"Physics guidance failed: state={_guidance.CurrentState} hook_calls={_physicsHookCalls}");
                    if (_landing.IsActive) _landing.Abort(vehicle, "guidance_exception", "landing.failed");
                    if (_guidance.IsActive) _guidance.Abort(vehicle, "guidance_exception");
                }
            }
            else
            {
                _pendingAction = null;
            }
            if (_guidance != null)
                _flightDisplay.Update(vehicle, _profile, _guidance, action);
            return true;
        }

        private void ObserveVehicle(Vehicle vehicle)
        {
            if (!_vehicleObserved || !ReferenceEquals(vehicle, _lastControlledVehicle))
            {
                if (_vehicleObserved) _pendingAction = null;
                if (_lastControlledVehicle != null && _guidance != null && _guidance.IsActive)
                    _guidance.Abort(_lastControlledVehicle, "controlled_vehicle_changed");
                Diag.Info("VEHICLE", $"previous=\"{_lastControlledVehicle?.Id ?? "none"}\" current=\"{vehicle?.Id ?? "none"}\" state={_guidance?.CurrentState}");
                _lastControlledVehicle = vehicle;
                _vehicleObserved = true;
                _missionClock.OnVehicleChanged();
                _countdownParent = null;
            }
        }

        [StarMapAfterGui]
        public void OnGui(double dt)
        {
            if (_profile == null || _guidance == null) return;
            GameLocalization.Refresh();
            bool windowWasOpen = _windowOpen, timerWasOpen = _missionTimeOpen;
            bool timerDrawn = false;
            _window.Layout.Enabled = _windowOpen;
            _missionTimeWindow.Layout.Enabled = _missionTimeOpen;
            try
            {
                if (_windowOpen)
                {
                    string action = _window.Draw(ref _windowOpen, _profile, _guidance, _flightDisplay,
                        _missionClock, _physicsHook != null, _pendingAction != null, _landing);
                    if (action == "timer") _missionTimeOpen = !_missionTimeOpen;
                    else if (_pendingAction == null && action != null) _pendingAction = action;
                }
                if (_missionTimeOpen)
                {
                    _missionTimeWindow.Layout.Enabled = true;
                    string action = _missionTimeWindow.Draw(ref _missionTimeOpen, _missionClock,
                        _profile, _guidance.CurrentState, _pendingAction != null, _landing, _flightDisplay);
                    timerDrawn = _missionTimeWindow.IsVisible;
                    if (_pendingAction == null && action != null) _pendingAction = action;
                }
            }
            catch (Exception ex)
            {
                Diag.EveryError("autopilot_window", 5, "UI", ex, "Drawing the flight window failed");
            }
            finally
            {
                if (!_missionTimeOpen || !timerDrawn) _missionAltitude?.Release();
                _window.Layout.Enabled = _windowOpen;
                _missionTimeWindow.Layout.Enabled = _missionTimeOpen && timerDrawn;
                PersistentWindowLayout.DrawGuides(_window.Layout, _missionTimeWindow.Layout);
                SaveUiLayout((windowWasOpen && !_windowOpen) || (timerWasOpen && !_missionTimeOpen));
            }
        }

        private void SaveUiLayout(bool force = false)
        {
            if (_uiLayout == null) return;
            UiLayoutSaveResult result = _uiLayout.Flush(Environment.TickCount64, force);
            if (result == UiLayoutSaveResult.Saved) Diag.Info("UI_LAYOUT", $"event=saved windows={_uiLayout.WindowCount}");
            else if (result == UiLayoutSaveResult.Failed)
                Diag.EveryError("ui_layout_save", 5, "UI_LAYOUT", _uiLayout.LastError, "Saving the window layout failed; pending changes will be retried");
        }

        [StarMapUnload]
        public void Unload()
        {
            if (_lastControlledVehicle != null && _guidance != null && _guidance.IsActive)
                _guidance.Abort(_lastControlledVehicle, "mod_unload");
            if (_landing.IsActive) _landing.Abort(_lastControlledVehicle, "mod_unload");
            _physicsHook?.Dispose();
            _physicsHook = null;
            _missionAltitude?.Dispose();
            _missionClock.Stop();
            SaveUiLayout(true);
            _flightDisplay.CancelPlanning();
            _window.Dispose();
            Diag.Shutdown();
        }
    }
}
