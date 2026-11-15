using System;
using System.Threading;

namespace KITN
{
    internal sealed class RocketFlightModel
    {
        public RouteBurnPhase[] Phases = Array.Empty<RouteBurnPhase>();
        public double Mu, BodyRadius, Altitude, TangentialSpeed, RotationSpeed;
        public double RadialSpeed = 0;
        public double SeaLevelDensity, AtmosphereHeight, DragArea, MinimumThrottle = 0.1;
        public double AtmosphericThrustRatio = 1, AttitudeAccelerationDegS2 = 5;
        public bool HasFlightComputer = true, HasSteering = true;
        public string Fingerprint = null, ErrorKey = null;
        public bool StableOrbit;
        public double OrbitPeriapsis, OrbitApoapsis, OrbitInclination;
    }

    internal sealed class AdaptiveFlightPlan
    {
        public FlightPlanSettings RequestedSettings, Settings;
        public RoutePrediction Route = new RoutePrediction();
        public bool Success;
        public string MessageKey = "planning.pending";
        public object[] MessageArgs = Array.Empty<object>();
        public string Message => L10n.Format(MessageKey, MessageArgs);
        public double LiftoffTwr, AvailableDeltaV, MinimumDeltaV;
        public int Candidates;
        public string Fingerprint;
        public Exception Error = null;
    }

    /// <summary>Math-only search over immutable rocket snapshots; safe to run outside game threads.</summary>
    internal static class AdaptiveFlightPlanner
    {
        public static AdaptiveFlightPlan Calculate(FlightPlanSettings requested, RocketFlightModel model,
            CancellationToken cancellation = default)
        {
            var plan = new AdaptiveFlightPlan { RequestedSettings = requested, Fingerprint = model.Fingerprint };
            AdaptiveFlightPlan Fail(string key, params object[] args)
            { plan.MessageKey = key; plan.MessageArgs = args; return plan; }
            if (model.ErrorKey != null) return Fail(model.ErrorKey);
            if (!requested.IsValid) return Fail("planning.invalid_settings");
            if (!model.HasFlightComputer) return Fail("planning.no_computer");
            if (!model.HasSteering) return Fail("planning.no_steering");
            if (model.Phases.Length == 0) return Fail("planning.no_engines");
            double radius = model.BodyRadius + model.Altitude;
            if (!(model.Mu > 0) || !(model.BodyRadius > 0) || !double.IsFinite(radius)
                || !double.IsFinite(model.RadialSpeed) || !double.IsFinite(model.TangentialSpeed))
                return Fail("planning.no_physics");
            plan.LiftoffTwr = model.Phases[0].Thrust * model.AtmosphericThrustRatio
                / model.Phases[0].StartMass / (model.Mu / (radius * radius));
            foreach (RouteBurnPhase phase in model.Phases)
            {
                double endMass = phase.StartMass - phase.MassFlow * phase.FullThrottleSeconds;
                if (!double.IsFinite(phase.Thrust) || phase.Thrust <= 0 || !double.IsFinite(phase.MassFlow)
                    || phase.MassFlow <= 0 || !double.IsFinite(phase.StartMass) || phase.StartMass <= 0
                    || !double.IsFinite(phase.FullThrottleSeconds) || phase.FullThrottleSeconds <= 0 || endMass <= 0)
                    return Fail("planning.invalid_performance");
                plan.AvailableDeltaV += phase.Thrust / phase.MassFlow * Math.Log(phase.StartMass / endMass);
            }
            if (model.Altitude < 1000 && model.RadialSpeed < 5 && plan.LiftoffTwr <= 1.02)
                return Fail("planning.low_twr", plan.LiftoffTwr);
            double targetRadius = model.BodyRadius + requested.TargetPeriapsisKm * 1000;
            plan.MinimumDeltaV = Math.Max(0, Math.Sqrt(model.Mu / targetRadius) - Math.Abs(model.TangentialSpeed));
            if (plan.AvailableDeltaV < plan.MinimumDeltaV)
                return Fail("planning.low_fuel", plan.AvailableDeltaV, plan.MinimumDeltaV);
            double safeAltitude = Math.Max(100000, model.AtmosphereHeight + 5000);
            if (requested.TargetPeriapsisKm * 1000 < safeAltitude)
                return Fail("planning.target_in_atmosphere", requested.TargetPeriapsisKm, safeAltitude / 1000);

            RoutePrediction best = null;
            double bestScore = double.NegativeInfinity;
            FlightPlanSettings bestSettings = null;
            // Include earlier turns for rapid staged acceleration as well as longer turns
            // needed by low-thrust cores. Throttle feedback keeps both within the radial budget.
            double[] ends = { requested.GravityTurnEndM, requested.GravityTurnEndM * 0.5, requested.GravityTurnEndM * 0.75,
                requested.GravityTurnEndM * 1.3, requested.GravityTurnEndM * 1.65,
                requested.GravityTurnEndM * 2.1, requested.GravityTurnEndM * 2.6 };
            double[] shapes = { requested.GravityTurnShape, 0.8, 0.9, 1.4, 1.8 };
            foreach (double end in ends)
                foreach (double shape in shapes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var profile = new LaunchProfile(); requested.Apply(profile);
                    profile.GravityTurnEndM = (float)Math.Clamp(end, requested.GravityTurnStartM + 5000,
                        Math.Max(requested.GravityTurnStartM + 5000, requested.TargetPeriapsisKm * 900));
                    profile.GravityTurnShape = (float)shape;
                    RoutePrediction route = RoutePrediction.Calculate(profile, model.Phases, model.Mu, model.BodyRadius,
                        model.Altitude, model.RadialSpeed, model.TangentialSpeed, model.RotationSpeed,
                        model.SeaLevelDensity, model.AtmosphereHeight, model.DragArea, model.MinimumThrottle,
                        true, model.AtmosphericThrustRatio, model.AttitudeAccelerationDegS2, cancellation);
                    plan.Candidates++;
                    double score = route.ReachedOrbit ? 100000 + route.RemainingDeltaV - route.FlightSeconds * 0.02
                        - Math.Abs(profile.GravityTurnEndM - requested.GravityTurnEndM) / 1000
                        - Math.Abs(profile.GravityTurnShape - requested.GravityTurnShape) * 10
                        : -Math.Abs(route.FinalApoapsisKm - requested.TargetApoapsisKm)
                          - Math.Abs(route.FinalPeriapsisKm - requested.TargetPeriapsisKm);
                    if (score > bestScore) { bestScore = score; best = route; bestSettings = FlightPlanSettings.Capture(profile); }
                }
            if (best?.ReachedOrbit != true)
            {
                string key = best?.MessageKey == "preview.manual_stage" ? "planning.manual_stage"
                    : best?.MessageKey == "preview.fuel_exhausted" ? "planning.fuel_exhausted" : "planning.no_route";
                return Fail(key, best?.FinalApoapsisKm ?? 0, best?.FinalPeriapsisKm ?? 0);
            }
            plan.Success = true; plan.Settings = bestSettings; plan.Route = best;
            plan.MessageKey = "planning.ready";
            plan.MessageArgs = new object[] { bestSettings.GravityTurnStartM / 1000, bestSettings.GravityTurnEndM / 1000, best.RemainingDeltaV };
            return plan;
        }
    }
}
