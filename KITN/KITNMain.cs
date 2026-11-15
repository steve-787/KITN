using System;
using System.IO;
using System.Reflection;
using StarMap.API;
using StarMap.API.Services;
using KSA;
using Brutal.Numerics;

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
        private MinimizedWindow _minimizedWindow;
        private readonly MissionClock _missionClock = new MissionClock();
        private readonly MissionTimeWindow _missionTimeWindow = new MissionTimeWindow();
        private MissionAltitudeDisplay _missionAltitude;
        private readonly LandingController _landing = new LandingController();
        private readonly ManeuverPlanner _planner = new ManeuverPlanner();
        private readonly RcsController _rcs = new RcsController();
        private readonly RendezvousController _rendezvous = new RendezvousController();
        private readonly DockingGuidanceOverlay _dockingOverlay = new DockingGuidanceOverlay();
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
            _minimizedWindow = new MinimizedWindow(_uiLayout);
            _minimizedWindow.Peer = _missionTimeWindow.Layout;
            _window.Settings = _missionTimeWindow.Settings = _uiLayout;
            _windowOpen = _uiLayout.MainWindowOpen;
            _missionTimeOpen = _uiLayout.MissionTimeOpen;
            _window.Planner = _planner;
            _window.Rendezvous = _rendezvous;
            _window.Layout.Peer = _missionTimeWindow.Layout;
            _missionTimeWindow.Layout.Peer = _window.Layout;
            try
            {
                _missionAltitude = new MissionAltitudeDisplay(_patches, () => _missionTimeOpen && CanShowFlightUi());
                _missionTimeWindow.Altitude = _missionAltitude;
            }
            catch (Exception ex)
            {
                Diag.Error("ALTITUDE_DISPLAY", ex, "The original altitude display cannot be hosted; the game HUD is preserved");
            }
            var presets = new FlightPresetStore(Path.Combine(modDirectory, "flight_presets.json"));
            _window.Presets = presets;
            _planner.GravityAssists = _uiLayout.GravityAssists;
            _landing.Settings = _uiLayout.Landing.Copy(); _landing.AutoWarp = _landing.Settings.AutoWarp;
            _rendezvous.RendezvousOptions = _uiLayout.Rendezvous.Copy();
            _rendezvous.DockingOptions = _uiLayout.Docking.Copy();
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
            bool allowRcs = RcsController.Allowed(vehicle, _uiLayout.AllowRcs);
            try { _rendezvous.Refresh(vehicle, allowRcs); }
            catch (Exception ex)
            {
                Diag.EveryError("rendezvous_refresh", 5, "RENDEZVOUS", ex, "Reading docking targets failed");
                _rendezvous.StopWithFailure(ex.Message);
            }
            try { _landing.Refresh(vehicle); }
            catch (Exception ex)
            {
                Diag.EveryError("landing_availability", 5, "LANDING", ex, "Reading landing availability failed");
                if (_landing.IsActive) _landing.Abort(vehicle, "landing_availability_exception", "landing.failed");
            }
            string action = _pendingAction;
            if (!_guidance.IsActive && !_landing.IsActive && !_rendezvous.IsActive && _guidance.CurrentState == AscentGuidance.AscentState.Idle)
                _flightDisplay.RefreshLaunchPlan(vehicle, _profile, action == "countdown");
            double simulationTime = Universe.GetElapsedSeconds();
            try
            {
                _planner.Refresh(vehicle, simulationTime);
                if (action?.StartsWith("transfer_", StringComparison.Ordinal) == true)
                    _planner.HandleAction(action, vehicle, _guidance.IsActive || _landing.IsActive || _missionClock.IsCountingDown || _rendezvous.IsActive);
                if (action?.StartsWith("rendezvous_", StringComparison.Ordinal) == true)
                    _rendezvous.HandleAction(action, vehicle, _guidance.IsActive || _landing.IsActive || _missionClock.IsCountingDown || _planner.IsExecuting, allowRcs);
            }
            catch (Exception ex) { Diag.EveryError("transfer_refresh", 5, "TRANSFER", ex, "Reading transfer planner state failed"); }
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
                            if (!_guidance.IsActive && !_landing.IsActive && !_planner.IsExecuting && !_rendezvous.IsActive)
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
                                Diag.Info("COUNTDOWN", $"event=started duration_s={_profile.LaunchCountdownSeconds} vehicle=\"{vehicle.Id}\"");
                            }
                            break;
                        case "cancel_countdown":
                            Diag.Info("COUNTDOWN", "event=cancelled reason=user_cancel");
                            break;
                        case "start":
                            if (_landing.IsActive || _planner.IsExecuting || _rendezvous.IsActive) break;
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
                        case "abort": _planner.HandleAction("transfer_stop", vehicle, false); _guidance.Abort(vehicle); break;
                        case "reset": _planner.HandleAction("transfer_stop", vehicle, false); _rendezvous.Stop(); _landing.Reset(vehicle); _guidance.Reset(vehicle); _missionClock.Reset(); break;
                        case "warp": if (!_rendezvous.IsActive && !_planner.IsExecuting) _guidance.TriggerManualWarpToApoapsis(vehicle); break;
                        case "landing_start":
                            if (!_guidance.IsActive && !_missionClock.IsCountingDown && !_planner.IsExecuting && !_rendezvous.IsActive && _landing.Start(vehicle, simulationTime))
                            {
                                if (!_missionClock.HasLaunched)
                                {
                                    double launchTime = (double)vehicle.LaunchGameTime.Nanoseconds / (double)UniverseTime.NANOSECONDS_PER_SECOND;
                                    _missionClock.MarkLaunched(double.IsFinite(launchTime) && launchTime >= 0 && launchTime <= simulationTime ? launchTime : simulationTime);
                                    _missionClock.Advance(simulationTime);
                                }
                            }
                            break;
                        case "landing_abort": if (_landing.IsActive) _landing.Abort(vehicle); break;
                    }
                    _pendingAction = null;
                    UpdateRcs(vehicle);
                    _landing.Update(vehicle, simulationTime, dtPlayer);
                    _guidance.LandingDebug = _landing.Telemetry(_missionClock);
                    _guidance.Update(vehicle, _playerTime + dtPlayer, dtPlayer);
                    _rendezvous.Update(vehicle, simulationTime, allowRcs);
                    _planner.UpdateExecution(vehicle, simulationTime, allowRcs,
                        _guidance.IsActive || _landing.IsActive || _missionClock.IsCountingDown || _rendezvous.IsActive);
                }
                catch (Exception ex)
                {
                    Diag.EveryError("guidanceFrame", 5.0, "GUIDANCE", ex,
                        $"Physics guidance failed: state={_guidance.CurrentState} hook_calls={_physicsHookCalls}");
                    if (_landing.IsActive) _landing.Abort(vehicle, "guidance_exception", "landing.failed");
                    if (_guidance.IsActive) _guidance.Abort(vehicle, "guidance_exception");
                    _rendezvous.StopWithFailure(ex.Message);
                    _planner.HandleAction("transfer_stop", vehicle, false);
                }
                finally { UpdateRcs(vehicle); }
            }
            else
            {
                _pendingAction = null;
                _rcs.Restore();
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
                _rendezvous.Stop(_rendezvous.IsDockedToTarget);
                _rcs.Restore();
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
            if (!CanShowFlightUi())
            {
                _window.OnVisibilityChanged(false);
                _window.Layout.Enabled = _missionTimeWindow.Layout.Enabled = false;
                _minimizedWindow.Enabled = false;
                _missionAltitude?.Release();
                SaveUiLayout();
                return;
            }
            _missionTimeOpen = _uiLayout.MissionTimeOpen;
            bool windowWasOpen = _windowOpen, timerWasOpen = _missionTimeOpen;
            try { _dockingOverlay.Draw(_rendezvous); }
            catch (Exception ex) { Diag.EveryError("docking_overlay", 5, "UI", ex, "Drawing docking guidance failed"); }
            bool timerDrawn = false;
            _window.Layout.Enabled = _windowOpen;
            _minimizedWindow.Enabled = !_windowOpen;
            _missionTimeWindow.Layout.Enabled = _missionTimeOpen;
            try
            {
                if (_windowOpen)
                {
                    string action = _window.Draw(ref _windowOpen, _profile, _guidance, _flightDisplay,
                        _missionClock, _physicsHook != null, _pendingAction != null, _landing);
                    if (_pendingAction == null && action != null) _pendingAction = action;
                    if (!_windowOpen) _minimizedWindow.PlaceAt(_window.Layout.Current);
                }
                else if (_minimizedWindow.Draw(ModActivity.Read(_guidance, _flightDisplay,
                    _missionClock, _landing, _rendezvous, _planner, _pendingAction != null, _uiLayout.MissionTimeOpen)))
                {
                    _windowOpen = true;
                    UiWindowBounds bounds = _minimizedWindow.Layout.Current;
                    if (bounds != null) _window.Layout.PlaceAt(new float2(bounds.X, bounds.Y));
                }
                _missionTimeOpen = _uiLayout.MissionTimeOpen;
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
                _window.OnVisibilityChanged(_windowOpen);
                _uiLayout.SetMainWindowOpen(_windowOpen, Environment.TickCount64);
                _uiLayout.SetMissionTimeOpen(_missionTimeOpen, Environment.TickCount64);
                if (!_missionTimeOpen || !timerDrawn) _missionAltitude?.Release();
                _window.Layout.Enabled = _windowOpen;
                _minimizedWindow.Enabled = !_windowOpen;
                _missionTimeWindow.Layout.Enabled = _missionTimeOpen && timerDrawn;
                _missionTimeWindow.Layout.Peer = _windowOpen ? _window.Layout : _minimizedWindow.Layout;
                PersistentWindowLayout.DrawGuides(_windowOpen ? _window.Layout : _minimizedWindow.Layout, _missionTimeWindow.Layout);
                SaveUiLayout((windowWasOpen && !_windowOpen) || (timerWasOpen && !_missionTimeOpen));
            }
        }

        internal static bool CanShowFlightUi() => FlightUiVisibility.ShouldShow(
            Program.IsLoaded, Program.IsEditorOpen, Program.ControlledVehicle != null && Program.ControlledVehicle.Parent != null);

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
            _planner.Dispose();
            _rendezvous.Stop();
            _rcs.Restore();
            _window.Dispose();
            Diag.Shutdown();
        }

        private void UpdateRcs(Vehicle vehicle)
        {
            try
            {
                _rcs.Update(vehicle, _guidance.IsActive || _landing.IsActive || _planner.IsExecuting || _rendezvous.IsActive,
                    (_uiLayout?.AllowRcs ?? true) && !_planner.IsWarping && !_planner.IsCoasting && !_rendezvous.IsRendezvousCoasting,
                    _planner.IsExecuting || _rendezvous.IsExecutingManeuver, _rendezvous.IsActive);
            }
            catch (Exception ex)
            {
                Diag.EveryError("rcs_control", 5, "CONTROL", ex, "Applying RCS preferences failed");
                if (_guidance.IsActive) _guidance.Abort(vehicle, "rcs_control_exception");
                if (_landing.IsActive) _landing.Abort(vehicle, "rcs_control_exception", "landing.failed");
                if (_planner.IsExecuting) _planner.HandleAction("transfer_stop", vehicle, false);
                _rendezvous.StopWithFailure(ex.Message);
                _rcs.Restore();
            }
        }
    }
}
