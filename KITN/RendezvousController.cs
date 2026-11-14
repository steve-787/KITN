using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class RendezvousController
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
        public double Distance { get; private set; } = double.NaN;
        public double RelativeSpeed { get; private set; } = double.NaN;
        public double PointingError { get; private set; } = double.NaN;
        public double LateralError { get; private set; } = double.NaN;
        public string TargetName => Target?.Id ?? "—";
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
        private float3 _minimumDeltaV;
        private static readonly FieldInfo ManualInputs = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

        public void SelectTarget(Vehicle target)
        {
            if (IsActive || ReferenceEquals(target, Target)) return;
            Target = target; TargetPort = null; Message = null; Phase = DockingPhase.Idle;
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
                if (IsActive) Stop();
                _vehicle = vehicle; OwnPort = null; TargetPort = null; Target = vehicle?.Target as Vehicle;
            }
            Targets.Clear(); OwnPorts.Clear(); TargetPorts.Clear();
            CanMatch = CanDock = false; Distance = RelativeSpeed = PointingError = LateralError = double.NaN;
            UnavailableReason = "rendezvous.require_vehicle";
            if (vehicle?.Parts == null || vehicle.FlightComputer == null || !vehicle.IsControllable) return;
            if (Universe.CurrentSystem != null)
                foreach (var candidate in Universe.CurrentSystem.All.OfType<Vehicle>())
                    if (!ReferenceEquals(candidate, vehicle) && ReferenceEquals(candidate.Parent, vehicle.Parent)) Targets.Add(candidate);
            Targets.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
            if (!Targets.Contains(Target)) { if (IsActive) Stop(); Target = Targets.Count > 0 ? Targets[0] : null; TargetPort = null; }
            foreach (var port in vehicle.Parts.Modules.Get<DockingPort>()) if (!port.Docked) OwnPorts.Add(port);
            if (!OwnPorts.Contains(OwnPort)) { if (IsActive && !_matching) Stop(); OwnPort = OwnPorts.Count > 0 ? OwnPorts[0] : null; }
            if (Target == null) { UnavailableReason = "rendezvous.require_target"; return; }
            if (Target.Parts != null)
                foreach (var port in Target.Parts.Modules.Get<DockingPort>()) if (!port.Docked) TargetPorts.Add(port);
            if (!TargetPorts.Contains(TargetPort)) { if (IsActive && !_matching) Stop(); TargetPort = TargetPorts.Count > 0 ? TargetPorts[0] : null; }
            ReadMotion(vehicle);
            if (!allowRcs) { UnavailableReason = "rendezvous.rcs_disabled"; return; }
            if (vehicle.FlightComputer.BurnMode == FlightComputerBurnMode.Auto || vehicle.BurnPlan.BurnCount > 0)
            { UnavailableReason = "rendezvous.other_nodes"; return; }
            if (Distance > DockingMath.MaximumRange) { UnavailableReason = "rendezvous.too_far"; return; }
            if (!vehicle.HasPhysicsBubble || !Target.HasPhysicsBubble) { UnavailableReason = "rendezvous.require_physics"; return; }
            if (!ReadAuthority(vehicle)) { UnavailableReason = "rendezvous.require_rcs"; return; }
            _standOff = Math.Max(30, vehicle.MeanRadius + Target.MeanRadius + 20);
            double clearance = OwnPort != null && TargetPort != null && LateralError < 0.3 && PointingError < 2
                ? 0.1 : Math.Max(2, _standOff * 0.5);
            if (!DockingMath.HasBrakingRoom(Distance, RelativeSpeed, _acceleration, clearance) && !IsActive)
            { UnavailableReason = "rendezvous.too_fast"; return; }
            CanMatch = true;
            if (OwnPort == null || TargetPort == null) { UnavailableReason = "rendezvous.require_ports"; return; }
            if (!OwnPort.Connector.CanConnect(TargetPort.Connector)) { UnavailableReason = "rendezvous.incompatible_ports"; return; }
            if (_relativePosition.X < -0.5) { UnavailableReason = "rendezvous.wrong_side"; return; }
            CanDock = true; UnavailableReason = null;
        }

        private void ReadMotion(Vehicle vehicle)
        {
            double3 ownOffset = double3.Zero, targetOffset = double3.Zero;
            doubleQuat ownBody = vehicle.GetBody2Cci(), targetBody = Target.GetBody2Cci();
            _targetPort2Cci = targetBody;
            bool usePorts = !IsActive || !_matching;
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
            float mass = vehicle.TotalMass;
            if (!float.IsFinite(mass) || mass <= 0) return false;
            _minimumDeltaV = authority.MinLinearImpulse / mass;
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
            if (action != "rendezvous_dock" && action != "rendezvous_match") return;
            if (locked || IsActive || !ReferenceEquals(vehicle, _vehicle)) return;
            Refresh(vehicle, allowRcs);
            bool match = action == "rendezvous_match";
            if (!(match ? CanMatch : CanDock)) { Message = UnavailableReason; return; }
            _activeVehicle = vehicle; _matching = match; _lastTime = Universe.GetElapsedSeconds(); _settledSince = double.NaN;
            var fc = _savedComputer = vehicle.FlightComputer;
            _savedRate = fc.RateLimit; _savedDeadband = fc.AngleDeadband; _savedRoll = fc.RollMode;
            _savedThrust = fc.ManualThrustMode; _savedAttitudeMode = fc.AttitudeMode;
            _savedRcs = fc.RCSMode;
            _savedTrack = fc.AttitudeTrackTarget; _savedFrame = fc.AttitudeFrame; _savedCustom = fc.CustomAttitudeTarget;
            Universe.AutoWarpStop(true); if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
            Universe.SetTarget(vehicle, Target, TargetPort?.Parent.FullPart);
            fc.SetManualThrustMode(FlightComputerManualThrustMode.Pulse);
            if (match) FlightAttitude.StopTracking(fc);
            Phase = match ? DockingPhase.Matching : DockingPhase.Aligning; Message = null;
            SetInputs(vehicle, ThrusterMapFlags.None);
            Diag.Info("RENDEZVOUS", $"event=start vehicle=\"{vehicle.Id}\" target=\"{Target.Id}\" docking={!match} distance_m={Distance:F2}");
        }

        public void Update(Vehicle vehicle, double now, bool allowRcs)
        {
            if (!IsActive) return;
            if (OwnPort?.Docked == true) { Stop(IsDockedToTarget); return; }
            if (!ReferenceEquals(vehicle, _activeVehicle) || !allowRcs || !CanMatch || (!_matching && !CanDock)
                || !ReferenceEquals(vehicle.Parent, Target?.Parent) || now < _lastTime)
            { Stop(); Message = !allowRcs ? "rendezvous.rcs_disabled" : UnavailableReason ?? "rendezvous.stopped"; return; }
            if (now <= _lastTime) return;
            _lastTime = now;
            Universe.AutoWarpStop(true); if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
            var fc = vehicle.FlightComputer;
            double3 desiredLocal = double3.Zero;
            if (!_matching)
            {
                doubleQuat desiredControl = DockingControlTarget(OwnPort.Connector.Asmb2VehicleAsmb, _targetPort2Cci, fc.Ctrl2Asmb);
                fc.AttitudeFrame = VehicleReferenceFrame.EnuBody; fc.RollMode = FlightComputerRollMode.Up;
                fc.CustomAttitudeTarget = FlightAttitude.CustomAngles(vehicle.GetPositionCci(), desiredControl);
                fc.TrackTarget(FlightComputerAttitudeTrackTarget.Custom);
                double angularRate = (vehicle.GetBodyRates().Transform(vehicle.GetBody2Cci()) - Target.GetBodyRates().Transform(Target.GetBody2Cci())).Length() * FlightAttitude.RAD_TO_DEG;
                var command = DockingMath.Approach(Vector(_relativePosition), PointingError, angularRate, _acceleration, _standOff);
                desiredLocal = Native(command.DesiredVelocity); Phase = command.Phase;
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
            SetInputs(vehicle, TranslationFlags(deltaV, _minimumDeltaV));
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

        internal static doubleQuat DockingControlTarget(doubleQuat ownPort2Body, doubleQuat targetPort2Cci, doubleQuat control2Body)
        {
            var desiredPort = doubleQuat.Concatenate(FlightComputerAttitudeTrackTargetEx.YAW_FLIP, targetPort2Cci);
            var desiredBody = doubleQuat.Concatenate(ownPort2Body.Inverse(), desiredPort);
            return doubleQuat.Concatenate(control2Body, desiredBody);
        }

        private static void SetInputs(Vehicle vehicle, ThrusterMapFlags flags)
        {
            if (vehicle == null || ManualInputs == null) return;
            var inputs = (ManualControlInputs)ManualInputs.GetValue(vehicle);
            inputs.EngineThrottle = 0; inputs.EngineOn = false; inputs.ThrusterCommandFlags = flags;
            ManualInputs.SetValue(vehicle, inputs);
        }

        public void Stop(bool docked = false)
        {
            if (!IsActive) return;
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
