using System;
using System.Collections.Generic;
using KSA;

namespace KITN
{
    /// <summary>Automation belongs to a craft, independently of the player's camera and selection.</summary>
    internal sealed class VesselControlSession : IDisposable
    {
        public Vehicle Vehicle { get; }
        public LaunchProfile Profile { get; } = new LaunchProfile();
        public AscentGuidance Guidance { get; }
        public FlightDisplay Flight { get; } = new FlightDisplay();
        public MissionClock Clock { get; } = new MissionClock();
        public LandingController Landing { get; } = new LandingController();
        public ManeuverPlanner Planner { get; } = new ManeuverPlanner();
        public RcsController Rcs { get; } = new RcsController();
        public RendezvousController Rendezvous { get; } = new RendezvousController();
        public IParentBody CountdownParent;
        public string PendingAction;
        public string SuspensionReason { get; private set; }
        public bool IsSuspended => SuspensionReason != null;
        public bool IsActive => Guidance.IsActive || Clock.IsCountingDown || Landing.IsActive
            || Planner.IsExecuting || Rendezvous.IsActive;
        private FlightComputer _pausedComputer;
        private FlightComputerBurnMode _burnMode;
        private FlightComputerAttitudeMode _attitudeMode;
        private FlightComputerRCSMode _rcsMode;
        private double _pausedAt;
        private static readonly System.Reflection.FieldInfo Inputs = typeof(Vehicle).GetField(
            "_manualControlInputs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public VesselControlSession(Vehicle vehicle, UiLayoutStore settings = null, LaunchProfile template = null)
        {
            Vehicle = vehicle;
            RestoreMissionClock(vehicle);
            if (template != null)
            {
                var plan = FlightPlanSettings.Capture(template);
                if (plan.IsValid) plan.Apply(Profile);
            }
            Guidance = new AscentGuidance(Profile, new StagingController(Profile));
            if (settings != null)
            {
                Landing.Settings = settings.Landing.Copy(); Landing.AutoWarp = Landing.Settings.AutoWarp;
                Rendezvous.RendezvousOptions = settings.Rendezvous.Copy();
                Rendezvous.DockingOptions = settings.Docking.Copy();
                Planner.GravityAssists = settings.GravityAssists;
                Planner.HohmannOptions = settings.Hohmann.Copy();
            }
        }

        /// <summary>The game saves the launch time per vehicle; adopting it keeps T+ across a save load.</summary>
        private void RestoreMissionClock(Vehicle vehicle)
        {
            if (vehicle?.HasLaunched != true) return;
            double launchTime = (double)vehicle.LaunchGameTime.Nanoseconds / UniverseTime.NANOSECONDS_PER_SECOND;
            if (Clock.RestoreFlight(launchTime, Universe.GetElapsedSeconds()))
                Diag.Info("MISSION_CLOCK", $"event=restored launch_s={launchTime:F3} elapsed_s={Clock.Seconds:F1} vehicle=\"{vehicle.Id}\"");
        }

        public string OperationStatus => Clock.IsCountingDown ? L10n.Get("clock.countdown")
            : Rendezvous.IsActive ? Rendezvous.IsAutoDocking ? Rendezvous.DockingCurrentStep : RendezvousPage.PhaseName(Rendezvous.Phase)
            : Landing.IsActive ? Landing.Phase
            : Planner.IsExecuting ? L10n.Get("remote.maneuver")
            : Guidance.IsActive ? AutopilotWindow.StateName(Guidance.CurrentState)
            : Planner.IsSearching || Planner.IsChecking ? L10n.Get("activity.transfer")
            : Rendezvous.Phase != DockingPhase.Idle ? RendezvousPage.PhaseName(Rendezvous.Phase)
            : Landing.State != LandingState.Idle ? Landing.Phase
            : AutopilotWindow.StateName(Guidance.CurrentState);

        public void Suspend(string reason, double now)
        {
            if (!IsSuspended)
            {
                _pausedAt = now;
                if (IsActive && Vehicle?.FlightComputer != null)
                {
                    _pausedComputer = Vehicle.FlightComputer;
                    _burnMode = _pausedComputer.BurnMode; _attitudeMode = _pausedComputer.AttitudeMode;
                    _rcsMode = _pausedComputer.RCSMode;
                }
            }
            SuspensionReason = reason;
            if (_pausedComputer != null)
            {
                _pausedComputer.BurnMode = FlightComputerBurnMode.Manual;
                _pausedComputer.AttitudeMode = FlightComputerAttitudeMode.Manual;
                _pausedComputer.RCSMode = FlightComputerRCSMode.Disabled;
                Inputs.SetValue(Vehicle, default(ManualControlInputs));
            }
        }

        public void Resume(double now)
        {
            if (!IsSuspended) return;
            Clock.ResumeCountdown(now - _pausedAt);
            if (_pausedComputer != null)
            {
                _pausedComputer.BurnMode = _burnMode; _pausedComputer.AttitudeMode = _attitudeMode;
                _pausedComputer.RCSMode = _rcsMode;
                // The next guidance step must compute fresh throttle and translation commands.
                Inputs.SetValue(Vehicle, default(ManualControlInputs));
                _pausedComputer = null;
            }
            Guidance.ResumeAfterPhysicsPause();
            Landing.ResumeAfterPhysicsPause(now, now - _pausedAt);
            Rendezvous.ResumeAfterPhysicsPause(now);
            Planner.ResumeAfterPhysicsPause();
            SuspensionReason = null;
        }

        public void Stop(string reason)
        {
            // Discard paused commands before releasing each controller's saved settings.
            if (_pausedComputer != null)
            {
                _pausedComputer.BurnMode = _burnMode; _pausedComputer.AttitudeMode = _attitudeMode;
                _pausedComputer.RCSMode = _rcsMode;
            }
            _pausedComputer = null; SuspensionReason = null; PendingAction = null;
            Clock.CancelCountdown();
            if (Guidance.IsActive) Guidance.Abort(Vehicle, reason);
            if (Landing.IsActive) Landing.Abort(Vehicle, reason);
            Planner.HandleAction("transfer_stop", Vehicle, false);
            Rendezvous.Stop(Rendezvous.IsDockedToTarget);
            Rcs.Restore();
        }

        public bool HandleCancellation()
        {
            if (PendingAction != "remote_cancel" && !(IsSuspended && (PendingAction == "abort" || PendingAction == "reset"
                || PendingAction == "landing_abort" || PendingAction == "rendezvous_stop" || PendingAction == "transfer_stop"
                || PendingAction == "cancel_countdown"))) return false;
            Stop("user_abort"); return true;
        }

        public void Dispose()
        {
            Stop("session_ended"); Clock.Stop(); Flight.CancelPlanning(); Planner.Dispose();
        }
    }

    internal sealed class RemoteControlFleet : IDisposable
    {
        private readonly Dictionary<Vehicle, VesselControlSession> _sessions = new Dictionary<Vehicle, VesselControlSession>();
        private readonly List<Vehicle> _available = new List<Vehicle>();
        private readonly UiLayoutStore _settings;
        private readonly VesselControlSession _empty;
        private Vehicle _player;
        private object _system;
        private double _lastTime = double.NaN;
        private bool _observed;
        public VesselControlSession Selected { get; private set; }
        public IEnumerable<VesselControlSession> Sessions => _sessions.Values;
        public IReadOnlyList<Vehicle> Available => _available;
        public bool AllowRemote { get; private set; } = true;

        public RemoteControlFleet(UiLayoutStore settings)
        {
            _settings = settings; Selected = _empty = new VesselControlSession(null, settings);
        }

        // Bubble leaders identify simulation groups, not control permissions or a distance limit.
        public static bool SimulationAvailable(Vehicle vessel) => vessel?.HasPhysicsBubble == true;

        public static bool Controllable(Vehicle vessel) => vessel?.Parent != null && vessel.Parts != null
            && vessel.FlightComputer != null && vessel.IsControllable;

        public VesselControlSession Find(Vehicle vessel) => vessel != null && _sessions.TryGetValue(vessel, out var session) ? session : null;
        public bool IsAvailable(Vehicle vessel) => _available.Contains(vessel);

        private VesselControlSession Get(Vehicle vessel)
        {
            if (vessel == null) return _empty;
            if (!_sessions.TryGetValue(vessel, out var session))
            {
                session = new VesselControlSession(vessel, _settings, Selected.Profile);
                _sessions.Add(vessel, session);
            }
            return session;
        }

        public void Observe(Vehicle player, IEnumerable<Vehicle> vessels, bool allowRemote, object system, double now)
        {
            if (_observed && (!ReferenceEquals(system, _system) || now < _lastTime - 0.01)) Clear();
            _observed = true; _system = system; _lastTime = now;
            var present = new HashSet<Vehicle>();
            _available.Clear();
            foreach (var vessel in vessels)
            {
                present.Add(vessel);
                if (Controllable(vessel) && SimulationAvailable(vessel)) _available.Add(vessel);
            }
            if (player != null) present.Add(player);
            foreach (var vessel in new List<Vehicle>(_sessions.Keys))
                if (!present.Contains(vessel)) { _sessions[vessel].Dispose(); _sessions.Remove(vessel); }
            _available.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
            AllowRemote = allowRemote;
            if (!ReferenceEquals(player, _player) || !present.Contains(Selected.Vehicle)) Selected = Get(player);
            _player = player;
            if (!AllowRemote)
            {
                foreach (var session in Sessions)
                    if (!ReferenceEquals(session.Vehicle, player)) session.Stop("remote_control_disabled");
                Selected = Get(player);
            }
            foreach (var session in Sessions)
            {
                string reason = !Controllable(session.Vehicle) ? "remote.control_unavailable"
                    : !ReferenceEquals(session.Vehicle, player) && !SimulationAvailable(session.Vehicle) ? "remote.out_of_range"
                    : session.Rendezvous.RequiresTargetPhysics && (!SimulationAvailable(session.Vehicle)
                        || !SimulationAvailable(session.Rendezvous.Target)) ? "remote.target_out_of_range" : null;
                if (reason != null) session.Suspend(reason, now); else session.Resume(now);
            }
        }

        public bool Select(Vehicle vessel)
        {
            if (!AllowRemote || !_available.Contains(vessel)) return false;
            Selected = Get(vessel); return true;
        }

        public void Clear()
        {
            foreach (var session in Sessions) session.Dispose();
            _sessions.Clear(); _available.Clear(); Selected = _empty; _player = null;
        }

        public void Dispose() { Clear(); _empty.Dispose(); }
    }
}
