using System;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal static class DockingEngineReader
    {
        public static bool Read(Vehicle vehicle, DockingPort port, out DockingEngineData data, out DockingEngineReason reason)
        {
            data = default; reason = DockingEngineReason.Fuel;
            if (vehicle?.Parts?.States == null || port?.Connector == null || vehicle.FlightComputer == null
                || !vehicle.Parts.States.TryGetTypeList<RocketCore, RocketCoreState, RocketCoreGlobalState, EmptyStruct>(out var states)
                || !(float.IsFinite(vehicle.TotalMass) && vehicle.TotalMass > 0)) return false;
            double minimum = 0.005, pulse = 0; int active = 0;
            foreach (var engine in vehicle.Parts.Modules.Get<EngineController>())
            {
                if (!engine.IsActive) continue;
                active++;
                if (engine.Cores?.Length is not > 0 || !float.IsFinite(engine.MinimumThrottle) || engine.MinimumThrottle < 0) return false;
                minimum = Math.Max(minimum, engine.MinimumThrottle);
                foreach (var core in engine.Cores)
                {
                    // Solid/unknown cores cannot be assumed restartable or safely cut off.
                    if (core is not PlumbedCore || core.Rocket?.Nozzles?.Length is not > 0
                        || !float.IsFinite(core.MinimumThrottle) || !float.IsFinite(core.MinimumPulseTime)
                        || core.MinimumThrottle < 0 || core.MinimumPulseTime < 0) return false;
                    ref readonly var state = ref states.GetState(core);
                    if (!state.IsPropellantAvailable) return false;
                    if (state.Throttle > 0 || state.MinThrustTimeRemaining > 0 || state.ThrustTimeRemaining > 0)
                    { reason = DockingEngineReason.Coasting; return false; }
                    minimum = Math.Max(minimum, core.MinimumThrottle); pulse = Math.Max(pulse, core.MinimumPulseTime);
                }
            }
            if (active == 0 || minimum > 1) return false;
            double3 portForward = double3.UnitX.Transform(port.Connector.Asmb2VehicleAsmb);
            var geometry = new DockingEngineGeometry(Vector(port.Connector.PositionVehicleAsmb - vehicle.CenterOfMassAsmb), Vector(portForward));
            double flow = 0;
            foreach (var engine in vehicle.Parts.Modules.Get<EngineController>())
            {
                if (!engine.IsActive) continue;
                foreach (var core in engine.Cores)
                {
                    var conditions = core.ComputeConditions((float)minimum);
                    foreach (var nozzle in core.Rocket.Nozzles)
                    {
                        if (nozzle.Parent == null) { reason = DockingEngineReason.Layout; return false; }
                        var performance = nozzle.ComputePerformance(in conditions, 0);
                        double3 location = double3.Unpack(in nozzle.LocationAsmb).Transform(nozzle.Parent.MatrixAsmb2VehicleAsmb) - vehicle.CenterOfMassAsmb;
                        double3 direction = -double3.Unpack(in nozzle.ExhaustDirectionAsmb).Transform(nozzle.Parent.Asmb2VehicleAsmb);
                        geometry.Add(Vector(location), Vector(direction), performance.GetTotalThrust());
                        flow += performance.MassFlowRate;
                    }
                }
            }
            reason = DockingEngineReason.Layout;
            if (!geometry.Valid || geometry.Count == 0 || !geometry.Force.IsFinite || geometry.Force.Length <= 0 || !(flow > 0)
                || LandingVector.Dot(geometry.Force.Unit, Vector(double3.UnitX.Transform(vehicle.FlightComputer.Helm2Asmb))) < Math.Cos(Math.PI / 180)) return false;
            // Engine ignition can select the native thrust frame. Reject an appreciable
            // frame jump rather than changing the docking attitude when thrust comes on.
            if (FlightAttitude.Angle(double3.UnitX.Transform(vehicle.FlightComputer.Helm2Asmb),
                double3.UnitX.Transform(vehicle.FlightComputer.Tnm2Asmb)) > 1) return false;
            double3 torque = Native(geometry.Torque);
            double angularAcceleration = torque.Transform(in vehicle.FlightComputer.TotalMassPropsInvBody.Inertia).Length();
            if (torque.Length() > 1e-6 && !(angularAcceleration > 0)) return false;
            // Own port +X faces the target: the target port's frame has the opposite +X.
            var facingTarget = doubleQuat.Concatenate(FlightComputerAttitudeTrackTargetEx.YAW_FLIP, port.Connector.Asmb2VehicleAsmb);
            var acceleration = Native(geometry.Force).Transform(facingTarget.Inverse()) / vehicle.TotalMass;
            if (!double.IsFinite(angularAcceleration) || !acceleration.IsFinite() || flow * Math.Max(0.3, pulse) > vehicle.TotalMass * 0.001)
            { reason = DockingEngineReason.TooStrong; return false; }
            data = new DockingEngineData(Vector(acceleration), angularAcceleration, minimum, pulse);
            return true;
        }
        private static LandingVector Vector(double3 v) => new LandingVector(v.X, v.Y, v.Z);
        private static double3 Native(LandingVector v) => new double3(v.X, v.Y, v.Z);
    }

    internal sealed partial class RendezvousController
    {
        private double _engineAssistUntil = double.NegativeInfinity;
        private double _engineAssistCheckAfter = double.NegativeInfinity;
        private bool _engineAssistSuppressClosing;
        private DockingEngineReason _engineAssistReason = DockingEngineReason.Waiting;
        public string DockingEngineStatus => L10n.Get("dock.engine." + (_engineAssistReason == DockingEngineReason.TooStrong
            ? "too_strong" : _engineAssistReason.ToString().ToLowerInvariant()));
        private void ResetEngineAssist()
        { _engineAssistUntil = _engineAssistCheckAfter = double.NegativeInfinity; _engineAssistSuppressClosing = false; _engineAssistReason = DockingEngineReason.Waiting; }

        private double UpdateEngineAssist(Vehicle vehicle, double now, double step, DockingCommand command, double orientation, double rate)
        {
            _engineAssistSuppressClosing = false;
            if (!DockingOptions.EngineAssist) return 0;
            if (command.Phase != DockingPhase.Approaching || !command.EntryCleared || command.Issue != DockingIssue.None
                || _dockingRetrying || !_relativePosition.IsFinite() || _relativePosition.X <= Math.Max(30, _dockingSafeDistance + 5))
            { _engineAssistReason = _relativePosition.X <= Math.Max(30, _dockingSafeDistance + 5) ? DockingEngineReason.Final : DockingEngineReason.Waiting; return 0; }
            if (orientation > Math.Min(1, DockingOptions.AlignmentToleranceDeg * 0.5) || rate > 0.1
                || vehicle.FlightComputer.BurnMode != FlightComputerBurnMode.Manual
                || Target.GetBodyRates().Length() * FlightAttitude.RAD_TO_DEG > 0.01)
            { _engineAssistReason = DockingEngineReason.Waiting; return 0; }
            if (now < _engineAssistUntil)
            { _engineAssistReason = DockingEngineReason.Coasting; _engineAssistSuppressClosing = true; return 0; }
            if (now < _engineAssistCheckAfter) return 0;
            _engineAssistCheckAfter = now + 1;
            try
            {
                if (!DockingEngineReader.Read(vehicle, OwnPort, out var engine, out _engineAssistReason)) return 0;
                // Resolve the measured force in the actual target-port frame, including the
                // remaining own-port pointing/roll error rather than an assumed ideal frame.
                var ownFacingTarget = doubleQuat.Concatenate(FlightComputerAttitudeTrackTargetEx.YAW_FLIP,
                    doubleQuat.Concatenate(OwnPort.Connector.Asmb2VehicleAsmb, vehicle.GetBody2Cci()));
                var actualForce = Native(engine.Acceleration).Transform(ownFacingTarget).Transform(_targetPort2Cci.Inverse());
                engine = new DockingEngineData(Vector(actualForce), engine.AngularAcceleration, engine.Throttle, engine.MinimumPulse);
                if (!DockingEngineMath.Plan(command, Vector(_relativePosition), Vector(_relativeVelocity), orientation, rate,
                    _acceleration, _dockingSafeDistance, step, DockingOptions, engine, out double deltaV, out _engineAssistReason)) return 0;
                _engineAssistUntil = now + Math.Max(3, engine.MinimumPulse + 0.5);
                _engineAssistSuppressClosing = true; _translationPulses.Reset();
                Diag.Info("DOCKING", $"event=engine_assist_push throttle={engine.Throttle:F4} worst_delta_v_mps={deltaV:F4} minimum_burn_s={engine.MinimumPulse:F3} distance_m={Distance:F2}");
                return engine.Throttle;
            }
            catch (Exception ex)
            {
                _engineAssistReason = DockingEngineReason.Fuel;
                Diag.Every("docking_engine_unavailable", 10, () => $"[DOCKING] engine_assist_fallback={ex.Message}");
                return 0;
            }
        }

        private LandingVector SuppressEngineDirection(LandingVector signs, Vehicle vehicle)
        {
            if (!_engineAssistSuppressClosing) return signs;
            var axis = (-double3.UnitX).Transform(_targetPort2Cci).Transform(FlightAttitude.ControlToCci(vehicle).Inverse());
            double Keep(double sign, double direction) => Math.Abs(direction) > 0.8 && sign * direction > 0 ? 0 : sign;
            return new LandingVector(Keep(signs.X, axis.X), Keep(signs.Y, axis.Y), Keep(signs.Z, axis.Z));
        }
    }
}
