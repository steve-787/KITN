using System;
using Brutal.Numerics;
using KSA;

namespace KSAAutopilot
{
    internal static class RocketSnapshotReader
    {
        public static RocketFlightModel Read(Vehicle vehicle, LaunchProfile profile, string fingerprint)
        {
            var model = new RocketFlightModel { Fingerprint = fingerprint };
            if (vehicle?.Parts == null) { model.ErrorKey = "planning.no_engines"; return model; }
            if (vehicle.Parent == null) { model.ErrorKey = "planning.no_physics"; return model; }
            var fc = vehicle.FlightComputer;
            model.HasFlightComputer = fc != null;
            model.HasSteering = fc != null && fc.RCSMode == FlightComputerRCSMode.Enabled
                && fc.RcsTorqueAuthority.Y > 0 && fc.RcsTorqueAuthority.Z > 0;
            foreach (GimbalController gimbal in vehicle.Parts.Modules.Get<GimbalController>()) model.HasSteering = true;
            if (fc != null)
            {
                double authority = Math.Min(fc.TvcTorqueAuthorityMax.Y + fc.RcsTorqueAuthority.Y,
                    fc.TvcTorqueAuthorityMax.Z + fc.RcsTorqueAuthority.Z) * FlightAttitude.RAD_TO_DEG;
                if (authority > 0) model.AttitudeAccelerationDegS2 = Math.Clamp(authority, 0.2, 15);
            }
            double pressure = vehicle.HasPhysicsBubble ? vehicle.PhysicsEnvironment.AtmosphericPressure : 0;
            // Use an isolated native performance calculator: drain simulation must not change
            // the game's live fuel, staging or cached performance pressure.
            var performance = new SequencePerformanceList(vehicle.Parts);
            Span<FiniteBurnPhase> vacuum = stackalloc FiniteBurnPhase[128];
            performance.RecomputeForFlight(0);
            int count = performance.FillStagedBurn(true, vacuum);
            bool fromActive = count > 0;
            if (count == 0) count = performance.FillStagedBurn(false, vacuum);
            if (count >= vacuum.Length) { model.ErrorKey = "planning.too_many_phases"; return model; }
            ReadOnlySpan<Sequence> sequences = vehicle.Parts.SequenceList.Sequences;
            ReadOnlySpan<SequencePerformance> native = performance.PerformanceSequences;
            int first = -1;
            for (int i = 0; i < Math.Min(sequences.Length, native.Length); i++)
                if (fromActive ? sequences[i].Number == vehicle.Parts.SequenceList.ActiveSequence
                    : !sequences[i].Activated && native[i].Thrust > 0 && native[i].MassFlowRate > 0)
                { first = i; break; }
            model.Phases = new RouteBurnPhase[count];
            int sequenceIndex = first;
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && vacuum[i].StartsStage)
                {
                    sequenceIndex++;
                    while (sequenceIndex < native.Length && native[sequenceIndex].Phases?.Count is not > 0) sequenceIndex++;
                }
                double correction = 1;
                if (sequenceIndex >= 0 && sequenceIndex < sequences.Length && sequences[sequenceIndex].Environment == PerformanceEnvironment.Atmospheric)
                {
                    double reference = ThrustAtPressure(vehicle, sequences[sequenceIndex].Number, 101325);
                    double inVacuum = ThrustAtPressure(vehicle, sequences[sequenceIndex].Number, 0);
                    if (reference > 0 && inVacuum > 0) correction = inVacuum / reference;
                }
                model.Phases[i] = new RouteBurnPhase(vacuum[i].Thrust, vacuum[i].MassFlowRate,
                    vacuum[i].StartMass, vacuum[i].MaxSeconds, vacuum[i].StartsStage);
                if (correction != 1) model.Phases[i] = new RouteBurnPhase(vacuum[i].Thrust * correction, vacuum[i].MassFlowRate,
                    vacuum[i].StartMass, vacuum[i].MaxSeconds, vacuum[i].StartsStage);
            }
            if (count > 0 && pressure > 0 && first >= 0)
            {
                // KSA's finite phases retain the sequence's editor pressure reference even
                // after RecomputeForFlight. Evaluate real nozzles to get ambient thrust.
                double inVacuum = ThrustAtPressure(vehicle, sequences[first].Number, 0);
                double local = ThrustAtPressure(vehicle, sequences[first].Number, (float)pressure);
                if (inVacuum > 0) model.AtmosphericThrustRatio = Math.Clamp(local / inVacuum, 0.1, 1);
            }
            double3 position = vehicle.GetPositionCci(), velocity = vehicle.GetVelocityCci();
            double radius = position.Length();
            double3 heading = FlightAttitude.Direction(position, 0, profile.LaunchHeadingDeg);
            model.Mu = vehicle.Parent.Mu; model.BodyRadius = vehicle.Parent.MeanRadius;
            model.Altitude = Math.Max(0, radius - model.BodyRadius);
            model.RadialSpeed = radius > 0 ? double3.Dot(position, velocity) / radius : 0;
            model.TangentialSpeed = double3.Dot(velocity, heading);
            model.RotationSpeed = double3.Dot(velocity - vehicle.GetSurfaceVelocityCci(), heading)
                * model.BodyRadius / Math.Max(1, radius);
            model.AtmosphereHeight = Math.Max(0, vehicle.Parent.GetAtmosphereRadius() - model.BodyRadius);
            double scaleHeight = Math.Max(1000, model.AtmosphereHeight / 14);
            model.SeaLevelDensity = vehicle.HasPhysicsBubble ? vehicle.PhysicsEnvironment.AtmosphericDensity
                * Math.Exp(Math.Min(10, model.Altitude / scaleHeight)) : 0;
            model.DragArea = count > 0 ? model.Phases[0].StartMass / 5300 : 0;
            model.MinimumThrottle = Math.Max(0.001, vehicle.GetMinThrottle());
            foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
                foreach (RocketCore core in engine.Cores) model.MinimumThrottle = Math.Max(model.MinimumThrottle, core.MinimumThrottle);
            return model;
        }

        private static double ThrustAtPressure(Vehicle vehicle, int sequence, float pressure)
        {
            double thrust = 0;
            foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
            {
                if (engine.Sequence != sequence && !engine.IsActive) continue;
                if (engine.Sequence > sequence) continue;
                foreach (RocketCore core in engine.Cores)
                {
                    if (core.Rocket?.Nozzles == null) continue;
                    ref readonly RocketCoreConditions conditions = ref core.DesignConditions;
                    foreach (RocketNozzle nozzle in core.Rocket.Nozzles)
                        thrust += Math.Max(0, nozzle.ComputePerformance(in conditions, pressure).GetTotalThrust());
                }
            }
            return thrust;
        }
    }
}
