using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed partial class RendezvousController
    {
        public readonly List<Vehicle> Targets = new List<Vehicle>();
        public readonly List<DockingPort> OwnPorts = new List<DockingPort>(), TargetPorts = new List<DockingPort>();
        public Vehicle Target { get; private set; }
        public DockingPort OwnPort { get; private set; }
        public DockingPort TargetPort { get; private set; }
        public DockingPhase Phase { get; private set; }
        public string Message { get; private set; }
        public string UnavailableReason { get; private set; } = "rendezvous.require_vehicle";
        public bool IsActive => _activeVehicle != null;
        public bool IsDockedToTarget => OwnPort?.Docked == true && ReferenceEquals(OwnPort.DockedToPart, TargetPort?.Parent.FullPart);
        public bool CanMatch { get; private set; }
        public bool CanDock { get; private set; }
        public bool CanRendezvous { get; private set; }
        public string RendezvousUnavailableReason { get; private set; } = "rendezvous.require_vehicle";
        public double Distance { get; private set; } = double.NaN;
        public double RelativeSpeed { get; private set; } = double.NaN;
        public double PointingError { get; private set; } = double.NaN;
        public double LateralError { get; private set; } = double.NaN;
        public string TargetName => Target?.Id ?? "—";
        public RendezvousSettings RendezvousOptions { get; set; } = new RendezvousSettings();
        public DockingSettings DockingOptions { get; set; } = new DockingSettings();
        public string LastDockingRecoveryText => _lastDockingRecovery?.ToString();
        private Vehicle _vehicle, _activeVehicle;
        private bool _matching;
        private double _lastTime = double.NaN, _settledSince = double.NaN;
        private float _savedRate, _savedDeadband;
        private FlightComputerRollMode _savedRoll;
        private FlightComputerManualThrustMode _savedThrust;
        private FlightComputerRCSMode _savedRcs;
        private FlightComputerAttitudeMode _savedAttitudeMode;
        private FlightComputerAttitudeTrackTarget _savedTrack;
        private VehicleReferenceFrame _savedFrame;
        private double3 _savedCustom;
        private FlightComputer _savedComputer;
        private double3 _relativePosition, _relativeVelocity;
        private double3 _inertialRelativeVelocity;
        private doubleQuat _targetPort2Cci = doubleQuat.Identity;
        private double _acceleration, _standOff;
        private double _dockingSafeDistance, _dockingRollAngle;
        private bool _routingAround;
        private bool _dockingEntryCleared;
        private bool _dockingRotationCoasting;
        private double _minimumPulseSeconds;
        private readonly RcsPulseControl _translationPulses = new RcsPulseControl();
        private readonly DockingRecoveryMonitor _dockingRecovery = new DockingRecoveryMonitor();
        private bool _dockingRetrying;
        private LocalizedMessage _lastDockingRecovery;
        private double _dockingBlockedSince = double.NaN;
        private float3 _rotationTorque;
        private float3 _minimumDeltaV;
        private static readonly FieldInfo ManualInputs = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

        public void SelectTarget(Vehicle target)
        {
            if (IsActive || ReferenceEquals(target, Target)) return;
            Target = target; TargetPort = null; Message = null; Phase = DockingPhase.Idle; _lastDockingRecovery = null;
        }
        public void SelectOwnPort(DockingPort port) { if (!IsActive && OwnPorts.Contains(port)) OwnPort = port; }
        public void SelectTargetPort(DockingPort port) { if (!IsActive && TargetPorts.Contains(port)) TargetPort = port; }
        public static string PortName(DockingPort port) => port == null ? "—" : port.Parent.FullPart.DisplayName;

        public void Refresh(Vehicle vehicle, bool allowRcs)
        {
            if (IsActive && IsDockedToTarget)
                Stop(true);
            if (!ReferenceEquals(vehicle, _vehicle))
            {
                if (IsActive) StopWithReason("rendezvous.target_changed");
                _vehicle = vehicle; OwnPort = null; TargetPort = null; Target = vehicle?.Target as Vehicle;
            }
            Targets.Clear(); OwnPorts.Clear(); TargetPorts.Clear();
            CanMatch = CanDock = CanRendezvous = false; Distance = RelativeSpeed = PointingError = LateralError = double.NaN;
            RendezvousUnavailableReason = "rendezvous.require_vehicle";
            UnavailableReason = "rendezvous.require_vehicle";
            if (vehicle?.Parts == null || vehicle.FlightComputer == null || !vehicle.IsControllable) return;
            if (Universe.CurrentSystem != null)
                foreach (var candidate in Universe.CurrentSystem.All.OfType<Vehicle>())
                    if (!ReferenceEquals(candidate, vehicle) && ReferenceEquals(candidate.Parent, vehicle.Parent)) Targets.Add(candidate);
            Targets.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
            if (!Targets.Contains(Target)) { if (IsActive) StopWithReason("dock.target_missing"); Target = Targets.Count > 0 ? Targets[0] : null; TargetPort = null; }
            foreach (var port in vehicle.Parts.Modules.Get<DockingPort>()) if (!port.Docked) OwnPorts.Add(port);
            if (!OwnPorts.Contains(OwnPort)) { if (IsActive && !_matching) StopWithReason("dock.own_port_missing"); OwnPort = OwnPorts.Count > 0 ? OwnPorts[0] : null; }
            if (Target == null) { RendezvousUnavailableReason = UnavailableReason = "rendezvous.require_target"; return; }
            if (Target.Parts != null)
                foreach (var port in Target.Parts.Modules.Get<DockingPort>()) if (!port.Docked) TargetPorts.Add(port);
            if (!TargetPorts.Contains(TargetPort)) { if (IsActive && !_matching) StopWithReason("dock.target_port_missing"); TargetPort = TargetPorts.Count > 0 ? TargetPorts[0] : null; }
            ReadMotion(vehicle);
            RendezvousUnavailableReason = RendezvousProblem(vehicle, allowRcs);
            CanRendezvous = RendezvousUnavailableReason == null;
            if (!allowRcs) { UnavailableReason = "rendezvous.rcs_disabled"; return; }
            if (vehicle.FlightComputer.BurnMode == FlightComputerBurnMode.Auto || vehicle.BurnPlan.BurnCount > 0)
            { UnavailableReason = "rendezvous.other_nodes"; return; }
            if (Distance > DockingMath.MaximumRange) { UnavailableReason = "rendezvous.too_far"; return; }
            if (!vehicle.HasPhysicsBubble || !Target.HasPhysicsBubble) { UnavailableReason = "rendezvous.require_physics"; return; }
            if (!ReadAuthority(vehicle)) { UnavailableReason = "rendezvous.require_rcs"; return; }
            if (!_rendezvousRunning)
            {
                double offsets = (OwnPort?.Connector.PositionVehicleAsmb - vehicle.CenterOfMassAsmb)?.Length() ?? 0;
                offsets += (TargetPort?.Connector.PositionVehicleAsmb - Target.CenterOfMassAsmb)?.Length() ?? 0;
                _dockingSafeDistance = vehicle.MeanRadius + Target.MeanRadius + offsets + DockingOptions.SafetyMarginM;
                _standOff = Math.Max(_dockingSafeDistance + 5, DockingOptions.OverrideStartDistance ? DockingOptions.StartDistanceM : 30);
            }
            double clearance = OwnPort != null && TargetPort != null && LateralError < DockingOptions.CorridorRadiusM && PointingError < DockingOptions.AlignmentToleranceDeg
                ? 0.1 : Math.Max(2, _standOff * 0.5);
            if (!DockingMath.HasBrakingRoom(Distance, RelativeSpeed, _acceleration, clearance) && !IsActive)
            { UnavailableReason = "rendezvous.too_fast"; return; }
            CanMatch = true;
            if (OwnPort == null || TargetPort == null) { UnavailableReason = "rendezvous.require_ports"; return; }
            if (!OwnPort.Connector.CanConnect(TargetPort.Connector)) { UnavailableReason = "rendezvous.incompatible_ports"; return; }
            if (_rotationTorque.X <= 0) { UnavailableReason = "docking.require_roll_rcs"; return; }
            CanDock = true; UnavailableReason = null;
        }

        private void ReadMotion(Vehicle vehicle)
        {
            double3 ownOffset = double3.Zero, targetOffset = double3.Zero;
            doubleQuat ownBody = vehicle.GetBody2Cci(), targetBody = Target.GetBody2Cci();
            _targetPort2Cci = targetBody;
            bool usePorts = IsActive && !_matching;
            if (OwnPort != null && usePorts) ownOffset = (OwnPort.Connector.PositionVehicleAsmb - vehicle.CenterOfMassAsmb).Transform(ownBody);
            if (TargetPort != null)
            {
                if (usePorts) targetOffset = (TargetPort.Connector.PositionVehicleAsmb - Target.CenterOfMassAsmb).Transform(targetBody);
                _targetPort2Cci = doubleQuat.Concatenate(TargetPort.Connector.Asmb2VehicleAsmb, targetBody);
            }
            double3 ownPosition = vehicle.GetPositionCci() + ownOffset, targetPosition = Target.GetPositionCci() + targetOffset;
            double3 ownVelocity = vehicle.GetVelocityCci() + double3.Cross(vehicle.GetBodyRates().Transform(ownBody), ownOffset);
            double3 targetRates = Target.GetBodyRates().Transform(targetBody);
            double3 targetVelocity = Target.GetVelocityCci() + double3.Cross(targetRates, targetOffset);
            double3 displacement = ownPosition - targetPosition;
            _relativePosition = displacement.Transform(_targetPort2Cci.Inverse());
            _relativeVelocity = (ownVelocity - targetVelocity - double3.Cross(targetRates, displacement)).Transform(_targetPort2Cci.Inverse());
            _inertialRelativeVelocity = ownVelocity - targetVelocity;
            Distance = displacement.Length(); RelativeSpeed = (ownVelocity - targetVelocity).Length();
            LateralError = Math.Sqrt(_relativePosition.Y * _relativePosition.Y + _relativePosition.Z * _relativePosition.Z);
            if (OwnPort != null && TargetPort != null)
            {
                doubleQuat current = doubleQuat.Concatenate(OwnPort.Connector.Asmb2VehicleAsmb, ownBody);
                PointingError = FlightAttitude.Angle(FlightAttitude.Forward(current), -FlightAttitude.Forward(_targetPort2Cci));
            }
        }

        private bool ReadAuthority(Vehicle vehicle)
        {
            if (!vehicle.Parts.States.TryGetTypeList<ThrusterController, ThrusterControllerState, ThrusterControllerGlobalState, EmptyStruct>(out var states)) return false;
            var authority = states.GlobalState.Authority;
            _rotationTorque = authority.TotalTorque;
            float mass = vehicle.TotalMass;
            if (!float.IsFinite(mass) || mass <= 0) return false;
            _minimumDeltaV = authority.MinLinearImpulse / mass;
            _minimumPulseSeconds = 0;
            foreach (var thruster in vehicle.Parts.Modules.Get<ThrusterController>())
                if (thruster.IsActive && float.IsFinite(thruster.MinimumPulseTime))
                    _minimumPulseSeconds = Math.Max(_minimumPulseSeconds, thruster.MinimumPulseTime);
            var force = authority.TotalForce / mass;
            _acceleration = Math.Min(force.X, Math.Min(force.Y, force.Z));
            // Pulse mode emits one minimum impulse per 0.15 s. Reserve half its acceleration for braking.
            _acceleration = Math.Min(_acceleration, Math.Min(_minimumDeltaV.X, Math.Min(_minimumDeltaV.Y, _minimumDeltaV.Z)) / 0.2) * 0.5;
            return double.IsFinite(_acceleration) && _acceleration > 1e-7
                && authority.TotalTorque.Y > 0 && authority.TotalTorque.Z > 0;
        }

        public void HandleAction(string action, Vehicle vehicle, bool locked, bool allowRcs)
        {
            if (action == "rendezvous_stop") { Stop(); return; }
            if (action == "rendezvous_start")
            {
                if (locked || IsActive || !ReferenceEquals(vehicle, _vehicle)) return;
                Refresh(vehicle, allowRcs);
                if (!CanRendezvous)
                {
                    Message = RendezvousUnavailableReason;
                    Diag.Warn("RENDEZVOUS", $"event=start_blocked reason={Message} detail=\"{MessageText}\"");
                    return;
                }
                try { StartRendezvous(vehicle); }
                catch (Exception ex) { Diag.Error("RENDEZVOUS", ex, "Starting rendezvous failed"); FailRendezvous("rendezvous.failed_detail", ex.Message); }
                return;
            }
            if (action != "rendezvous_dock" && action != "rendezvous_match") return;
            if (locked || IsActive || !ReferenceEquals(vehicle, _vehicle)) return;
            Refresh(vehicle, allowRcs);
            bool match = action == "rendezvous_match";
            if (!(match ? CanMatch : CanDock)) { Message = UnavailableReason; return; }
            _activeVehicle = vehicle; _matching = match; _lastTime = Universe.GetElapsedSeconds(); _settledSince = double.NaN;
            _dockCommandSentAt = double.NaN;
            ClearDockingTelemetry();
            _translationPulses.Reset(); _dockingEntryCleared = _dockingRotationCoasting = false;
            ResetEngineAssist();
            var fc = _savedComputer = vehicle.FlightComputer;
            _savedRate = fc.RateLimit; _savedDeadband = fc.AngleDeadband; _savedRoll = fc.RollMode;
            _savedThrust = fc.ManualThrustMode; _savedAttitudeMode = fc.AttitudeMode;
            _savedRcs = fc.RCSMode;
            _savedTrack = fc.AttitudeTrackTarget; _savedFrame = fc.AttitudeFrame; _savedCustom = fc.CustomAttitudeTarget;
            Universe.AutoWarpStop(true); if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
            Universe.SetTarget(vehicle, Target, TargetPort?.Parent.FullPart);
            fc.SetManualThrustMode(FlightComputerManualThrustMode.Pulse);
            if (match) FlightAttitude.StopTracking(fc);
            else
            {
                _dockingRecovery.Reset(); _dockingRetrying = false; _lastDockingRecovery = null; _dockingBlockedSince = double.NaN;
                ReadMotion(vehicle);
                _routingAround = _relativePosition.X < -0.5;
                _dockingRollAngle = DockingOptions.ForceRoll ? DockingOptions.RollAngleDeg * FlightAttitude.DEG_TO_RAD
                    : DockingRollOffset(doubleQuat.Concatenate(OwnPort.Connector.Asmb2VehicleAsmb, vehicle.GetBody2Cci()), _targetPort2Cci);
                ApplyDockingAttitude(vehicle);
            }
            Phase = match ? DockingPhase.Matching : DockingPhase.Aligning; Message = null;
            SetInputs(vehicle, ThrusterMapFlags.None);
            Diag.Info("RENDEZVOUS", $"event=start vehicle=\"{vehicle.Id}\" target=\"{Target.Id}\" docking={!match} distance_m={Distance:F2}");
        }

        public void Update(Vehicle vehicle, double now, bool allowRcs)
        {
            if (!IsActive) return;
            if (_rendezvousRunning) { UpdateRendezvous(vehicle, now, allowRcs); return; }
            if (OwnPort?.Docked == true) { if (IsDockedToTarget) Stop(true); else StopWithReason("dock.own_port_missing"); return; }
            if (!ReferenceEquals(vehicle, _activeVehicle) || !ReferenceEquals(vehicle.Parent, Target?.Parent))
            { StopWithReason("rendezvous.target_changed"); return; }
            if (!allowRcs) { StopWithReason("rendezvous.rcs_disabled"); return; }
            if (now < _lastTime) { StopWithReason("dock.time_reverted"); return; }
            if (WaitForDockingConnection(vehicle, now)) return;
            if (!CanMatch || (!_matching && !CanDock))
            {
                string reason = UnavailableReason ?? "rendezvous.require_vehicle";
                bool transient = reason == "rendezvous.require_physics" || reason == "rendezvous.require_rcs" || reason == "docking.require_roll_rcs";
                if (!transient || _matching) { StopWithReason(reason); return; }
                if (!double.IsFinite(_dockingBlockedSince)) _dockingBlockedSince = now;
                SetInputs(vehicle, ThrusterMapFlags.None);
                SetDockingMessage(new LocalizedMessage("dock.waiting_control", new LocalizedMessage(reason)));
                DockingPreview.Holding(Vector(_relativePosition));
                if (now - _dockingBlockedSince >= 5) StopWithReason(reason);
                return;
            }
            _dockingBlockedSince = double.NaN;
            if (now <= _lastTime) return;
            double simulationStep = now - _lastTime;
            _lastTime = now;
            Universe.AutoWarpStop(true); if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
            var fc = vehicle.FlightComputer;
            double3 desiredLocal = double3.Zero;
            double engineThrottle = 0;
            LandingVector translationWaypoint = default;
            if (!_matching)
            {
                doubleQuat desiredControl = ApplyDockingAttitude(vehicle);
                doubleQuat currentControl = FlightAttitude.ControlToCci(vehicle);
                // Pitch/yaw alone cannot detect a reversed roll or a misaligned side-mounted port.
                double fullError = 2 * Math.Acos(Math.Clamp(Math.Abs(doubleQuat.Dot(doubleQuat.Normalize(currentControl),
                    doubleQuat.Normalize(desiredControl))), 0, 1)) * FlightAttitude.RAD_TO_DEG;
                double angularRate = (vehicle.GetBodyRates().Transform(vehicle.GetBody2Cci()) - Target.GetBodyRates().Transform(Target.GetBody2Cci())).Length() * FlightAttitude.RAD_TO_DEG;
                if (_routingAround && _relativePosition.X >= _standOff - 0.5) _routingAround = false;
                // KSA's switch lines include a minimum impulse and a ten-second turn-around
                // window. Respect that attainable precision instead of waiting forever.
                var windows = DockingNativeWindows(fc);
                // Native minimum-duration jets can turn a tiny correction into the next
                // opposite correction. Coast inside a bounded attitude window; re-enable
                // native tracking immediately for drift or rates that need braking.
                double quietRate = Math.Max(DockingOptions.MaximumAngularRateDegS, Math.Min(1, windows.Rate));
                double quietAngle = DockingOptions.AlignmentToleranceDeg * (_dockingRotationCoasting ? 0.85 : 0.6);
                _dockingRotationCoasting = double.IsFinite(fullError) && double.IsFinite(angularRate)
                    && angularRate <= quietRate && fullError + angularRate * 0.5 <= quietAngle;
                if (_dockingRotationCoasting) fc.AttitudeMode = FlightComputerAttitudeMode.Manual;
                if (_dockingRetrying && _relativePosition.X >= _standOff - 0.5 && Math.Abs(_relativeVelocity.X) < 0.1)
                { _dockingRetrying = false; _dockingRecovery.Reset(); }
                var command = DockingMath.Approach(Vector(_relativePosition), PointingError, angularRate,
                    _acceleration, _standOff, DockingOptions, _routingAround, _dockingSafeDistance, fullError,
                    Phase == DockingPhase.Approaching || Phase == DockingPhase.Docking || Phase == DockingPhase.Correcting,
                    windows.Angle, windows.Rate, Vector(_relativeVelocity), _dockingRetrying, _dockingEntryCleared);
                if (command.Issue == DockingIssue.InvalidMotion) { StopWithReason("dock.invalid_motion"); return; }
                double patience = command.Issue == DockingIssue.Lateral
                    ? 30 + 4 * Math.Sqrt(command.CorrectionError * DockingOptions.BrakingMargin / _acceleration) + 2 * _relativeVelocity.Length() / _acceleration
                    : 30 + 2 * command.CorrectionError / Math.Max(0.1, Math.Min(0.5, DockingOptions.MaximumTurnRateDegS));
                if (!_dockingRetrying && (command.Issue == DockingIssue.UnsafePath
                    || _dockingRecovery.Observe(now, command.Issue, command.CorrectionError, patience)))
                {
                    BeginDockingRetry(command, now, fullError);
                    command = DockingMath.Approach(Vector(_relativePosition), PointingError, angularRate,
                        _acceleration, _standOff, DockingOptions, _routingAround, _dockingSafeDistance, fullError,
                        false, windows.Angle, windows.Rate, Vector(_relativeVelocity), true, false);
                }
                _dockingEntryCleared = command.EntryCleared;
                translationWaypoint = command.Waypoint;
                desiredLocal = Native(command.DesiredVelocity); Phase = command.Phase;
                UpdateDockingTelemetry(now, command, fullError, angularRate, windows.Angle, windows.Rate);
                if (TryRequestDocking(vehicle, now, command)) return;
                engineThrottle = UpdateEngineAssist(vehicle, now, simulationStep, command, fullError, angularRate);
                DockingFeedback(command, fullError, angularRate);
                Diag.Every("docking_guidance", 2, () => $"[DOCKING] phase={Phase} issue={command.Issue} distance_m={Distance:F2} lateral_m={LateralError:F3} lateral_velocity_mps={Math.Sqrt(_relativeVelocity.Y * _relativeVelocity.Y + _relativeVelocity.Z * _relativeVelocity.Z):F3} port_angle_deg={PointingError:F2} orientation_deg={fullError:F2} angular_rate_deg_s={angularRate:F3} native_angle_window_deg={windows.Angle:F2} native_rate_window_deg_s={windows.Rate:F3} closing_speed_mps={-desiredLocal.X:F3} entry_cleared={_dockingEntryCleared} rotation_coasting={_dockingRotationCoasting} minimum_pulse_s={_minimumPulseSeconds:F3} minimum_dv_mps=({_minimumDeltaV.X:F4},{_minimumDeltaV.Y:F4},{_minimumDeltaV.Z:F4})");
            }
            else if (RelativeSpeed < 0.02)
            {
                if (!double.IsFinite(_settledSince)) _settledSince = now;
                if (now - _settledSince >= 1) { Stop(); Phase = DockingPhase.Matched; Message = "rendezvous.matched"; return; }
            }
            else _settledSince = double.NaN;
            // Translate in the current flight-computer frame, including rotation of the target's port frame.
            double3 deltaV = (_matching ? -_inertialRelativeVelocity : (desiredLocal - _relativeVelocity).Transform(_targetPort2Cci))
                .Transform(FlightAttitude.ControlToCci(vehicle).Inverse());
            if (_matching) SetInputs(vehicle, TranslationFlags(deltaV, _minimumDeltaV));
            else
            {
                var frame = FlightAttitude.ControlToCci(vehicle).Inverse();
                var actual = _relativeVelocity.Transform(_targetPort2Cci).Transform(frame);
                var requested = desiredLocal.Transform(_targetPort2Cci).Transform(frame);
                // The closing axis follows a speed profile, not a proportional pull toward
                // a port tens of metres away. Keep that distance out of fine lateral control
                // when the craft's control frame is slightly tilted or has a side-mounted port.
                var portOffset = translationWaypoint.Length > 0.001 ? Native(translationWaypoint) - _relativePosition : -_relativePosition;
                var trackingAxes = default(LandingVector);
                if (_dockingEntryCleared && translationWaypoint.Length < 0.001)
                {
                    portOffset.X = 0;
                    var axis = double3.UnitX.Transform(_targetPort2Cci).Transform(frame);
                    trackingAxes = new LandingVector(Math.Abs(axis.X) > 0.8 ? 1 : 0,
                        Math.Abs(axis.Y) > 0.8 ? 1 : 0, Math.Abs(axis.Z) > 0.8 ? 1 : 0);
                }
                var offset = portOffset.Transform(_targetPort2Cci).Transform(frame);
                var signs = _translationPulses.Update(now, fc.LastThrusterPulseTime.Seconds(), _minimumPulseSeconds,
                    Vector(requested), Vector(actual), Vector(offset), Vector(new double3(_minimumDeltaV.X, _minimumDeltaV.Y, _minimumDeltaV.Z)), trackingAxes);
                signs = SuppressEngineDirection(signs, vehicle);
                SetInputs(vehicle, TranslationFlags(Native(signs), float3.Zero), engineThrottle);
            }
        }

        internal static ThrusterMapFlags TranslationFlags(double3 deltaV, float3 minimumDeltaV)
        {
            ThrusterMapFlags flags = ThrusterMapFlags.None;
            int x = DockingMath.TranslationSign(deltaV.X, minimumDeltaV.X), y = DockingMath.TranslationSign(deltaV.Y, minimumDeltaV.Y), z = DockingMath.TranslationSign(deltaV.Z, minimumDeltaV.Z);
            if (x != 0) flags |= x > 0 ? ThrusterMapFlags.TranslateForward : ThrusterMapFlags.TranslateBackward;
            if (y != 0) flags |= y > 0 ? ThrusterMapFlags.TranslateRight : ThrusterMapFlags.TranslateLeft;
            if (z != 0) flags |= z > 0 ? ThrusterMapFlags.TranslateDown : ThrusterMapFlags.TranslateUp;
            return flags;
        }

        private void SetDockingMessage(LocalizedMessage message)
        { _rendezvousMessageDetail = message; Message = message.Key; }

        private void DockingFeedback(DockingCommand command, double orientation, double rate)
        {
            if (_dockingRetrying) { SetDockingMessage(_lastDockingRecovery); return; }
            string key = command.Issue == DockingIssue.Lateral ? "dock.correcting_lateral"
                : command.Issue == DockingIssue.Pointing ? "dock.correcting_pointing"
                : command.Issue == DockingIssue.Orientation ? "dock.correcting_roll"
                : command.Issue == DockingIssue.AngularRate ? "dock.correcting_rotation"
                : command.Issue == DockingIssue.BehindTarget ? "dock.routing_reason" : "dock.approaching_info";
            double value = command.Issue == DockingIssue.Lateral ? Math.Sqrt(_relativePosition.Y * _relativePosition.Y + _relativePosition.Z * _relativePosition.Z)
                : command.Issue == DockingIssue.Pointing ? PointingError : command.Issue == DockingIssue.Orientation ? orientation : rate;
            SetDockingMessage(new LocalizedMessage(key, value));
        }

        private void BeginDockingRetry(DockingCommand command, double now, double orientation)
        {
            double elapsed = _dockingRecovery.SecondsWithoutProgress(now);
            string key = command.Issue == DockingIssue.Lateral ? "dock.retry_lateral"
                : command.Issue == DockingIssue.Pointing ? "dock.retry_pointing"
                : command.Issue == DockingIssue.Orientation ? "dock.retry_roll"
                : command.Issue == DockingIssue.AngularRate ? "dock.retry_rotation" : "dock.retry_unsafe";
            _lastDockingRecovery = command.Issue == DockingIssue.UnsafePath
                ? new LocalizedMessage(key, Distance, Math.Sqrt(_relativePosition.Y * _relativePosition.Y + _relativePosition.Z * _relativePosition.Z), orientation)
                : new LocalizedMessage(key, command.CorrectionError, elapsed);
            _dockingRetrying = true;
            SetDockingMessage(_lastDockingRecovery);
            Diag.Warn("DOCKING", $"event=retry issue={command.Issue} error={command.CorrectionError:F3} no_progress_s={elapsed:F1} reason=\"{MessageText}\"");
        }

        private void StopWithReason(string reason)
        {
            if (!IsActive) return;
            bool docking = !_matching;
            Stop();
            SetDockingMessage(new LocalizedMessage(docking ? "dock.stopped_reason" : "rendezvous.failed_detail", new LocalizedMessage(reason)));
            Diag.Warn("RENDEZVOUS", $"event=stopped reason={reason} detail=\"{MessageText}\"");
        }

        public void StopWithFailure(string detail)
        {
            if (!IsActive) return;
            Stop(); SetDockingMessage(new LocalizedMessage("rendezvous.failed_detail", detail));
        }

        internal static doubleQuat DockingControlTarget(doubleQuat ownPort2Body, doubleQuat targetPort2Cci, doubleQuat control2Body)
        {
            var desiredPort = doubleQuat.Concatenate(FlightComputerAttitudeTrackTargetEx.YAW_FLIP, targetPort2Cci);
            var desiredBody = doubleQuat.Concatenate(ownPort2Body.Inverse(), desiredPort);
            return doubleQuat.Concatenate(control2Body, desiredBody);
        }

        private doubleQuat ApplyDockingAttitude(Vehicle vehicle)
        {
            var fc = vehicle.FlightComputer;
            // Custom tracking uses Helm once the main engines stop. Do not reuse the previous
            // maneuver's TNM frame when converting either the attitude or translation command.
            fc.Ctrl2Asmb = fc.Helm2Asmb;
            var rolledTarget = doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, _dockingRollAngle), _targetPort2Cci);
            var desired = DockingControlTarget(OwnPort.Connector.Asmb2VehicleAsmb, rolledTarget, fc.Ctrl2Asmb);
            fc.RCSMode = FlightComputerRCSMode.Enabled;
            fc.RateLimit = (float)(DockingOptions.MaximumTurnRateDegS * FlightAttitude.DEG_TO_RAD);
            fc.AngleDeadband = (float)(Math.Clamp(DockingOptions.AlignmentToleranceDeg / 2, 0.25, 1) * FlightAttitude.DEG_TO_RAD);
            // Do not demand repeated corrections smaller than one hardware angular pulse.
            double rateBit = Math.Max(fc.RateBit.X, Math.Max(fc.RateBit.Y, fc.RateBit.Z));
            if (double.IsFinite(rateBit) && rateBit > 0)
                fc.AngleDeadband = (float)Math.Max(fc.AngleDeadband,
                    Math.Min(DockingOptions.AlignmentToleranceDeg * 0.75 * FlightAttitude.DEG_TO_RAD, rateBit * 2));
            fc.AttitudeFrame = VehicleReferenceFrame.EclBody; fc.RollMode = FlightComputerRollMode.Up;
            fc.CustomAttitudeTarget = FlightAttitude.InertialCustomAngles(vehicle, desired);
            fc.TrackTarget(FlightComputerAttitudeTrackTarget.Custom);
            return desired;
        }

        internal static (double Angle, double Rate) DockingNativeWindows(FlightComputer fc)
        {
            double angleSquared = 0, rateSquared = 0;
            void Axis(float turnAround, float rateBit)
            {
                if (float.IsFinite(turnAround) && turnAround >= 0)
                {
                    double window = turnAround + fc.AngleDeadband * 0.5;
                    angleSquared += window * window;
                }
                if (float.IsFinite(rateBit) && rateBit >= 0) rateSquared += rateBit * rateBit;
            }
            Axis(fc.AngleTurnaround.X, fc.RateBit.X); Axis(fc.AngleTurnaround.Y, fc.RateBit.Y); Axis(fc.AngleTurnaround.Z, fc.RateBit.Z);
            return (Math.Sqrt(angleSquared) * FlightAttitude.RAD_TO_DEG,
                Math.Sqrt(rateSquared) * 1.5 * FlightAttitude.RAD_TO_DEG);
        }

        private static double DockingRollOffset(doubleQuat currentPort, doubleQuat targetPort)
        {
            var aligned = FlightAttitude.AlignForward(currentPort, -FlightAttitude.Forward(targetPort));
            var basis = doubleQuat.Concatenate(FlightComputerAttitudeTrackTargetEx.YAW_FLIP, targetPort);
            var up = double3.UnitZ.Transform(aligned).Transform(basis.Inverse());
            // The facing port's forward axis is opposite the target's X axis.
            return Math.Atan2(up.Y, up.Z);
        }

        private static void SetInputs(Vehicle vehicle, ThrusterMapFlags flags, double engineThrottle = 0)
        {
            if (vehicle == null || ManualInputs == null) return;
            var inputs = (ManualControlInputs)ManualInputs.GetValue(vehicle);
            inputs.EngineThrottle = (float)engineThrottle; inputs.EngineOn = engineThrottle > 0.001; inputs.ThrusterCommandFlags = flags;
            ManualInputs.SetValue(vehicle, inputs);
        }

        public void Stop(bool docked = false)
        {
            if (!IsActive) return;
            CancelDockingRequest();
            ClearDockingTelemetry();
            _translationPulses.Reset(); _dockingEntryCleared = _dockingRotationCoasting = false;
            ResetEngineAssist();
            StopRendezvous();
            SetInputs(_activeVehicle, ThrusterMapFlags.None);
            if (_savedComputer != null)
            {
                RestoreSettings(_savedComputer);
                // Docking can replace both vehicles with a combined craft and copy its control state.
                Vehicle combined = docked ? OwnPort?.Parent.FullPart.Tree?.OwningVehicle : null;
                if (combined != null && !ReferenceEquals(combined, _activeVehicle))
                {
                    SetInputs(combined, ThrusterMapFlags.None);
                    if (combined.FlightComputer != null) RestoreSettings(combined.FlightComputer);
                }
            }
            _savedComputer = null; _activeVehicle = null;
            Phase = docked ? DockingPhase.Docked : DockingPhase.Stopped; Message = docked ? "rendezvous.docked" : "rendezvous.stopped";
            if (docked) _lastDockingRecovery = null;
            _rendezvousMessageDetail = null;
            Diag.Info("RENDEZVOUS", $"event={(docked ? "docked" : "stopped")}");
        }
        private void RestoreSettings(FlightComputer fc)
        {
            fc.RateLimit = _savedRate; fc.AngleDeadband = _savedDeadband; fc.RollMode = _savedRoll; fc.RCSMode = _savedRcs;
            fc.ManualThrustMode = _savedThrust; fc.AttitudeMode = _savedAttitudeMode;
            fc.AttitudeTrackTarget = _savedTrack; fc.AttitudeFrame = _savedFrame; fc.CustomAttitudeTarget = _savedCustom;
        }
        private static LandingVector Vector(double3 v) => new LandingVector(v.X, v.Y, v.Z);
        private static double3 Native(LandingVector v) => new double3(v.X, v.Y, v.Z);
    }
}
