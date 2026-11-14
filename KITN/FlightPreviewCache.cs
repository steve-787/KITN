using System;

namespace KITN
{
    internal static class FlightPreviewCache
    {
        public static bool Equivalent(RocketFlightModel previous, RocketFlightModel current, bool force)
        {
            if (previous == null || current == null || previous.ErrorKey != current.ErrorKey
                || previous.HasFlightComputer != current.HasFlightComputer || previous.HasSteering != current.HasSteering
                || previous.StableOrbit != current.StableOrbit || previous.Phases.Length != current.Phases.Length) return false;
            double tolerance = force ? 0 : 0.001;
            bool Same(double a, double b, double floor = 0.01)
                => a == b || (double.IsFinite(a) && double.IsFinite(b)
                    && Math.Abs(a - b) <= Math.Max(force ? floor / 10 : floor, Math.Abs(a) * tolerance));
            if (!Same(previous.Mu, current.Mu) || !Same(previous.BodyRadius, current.BodyRadius)
                || !Same(previous.MinimumThrottle, current.MinimumThrottle, 0.0001)
                || !Same(previous.AtmosphericThrustRatio, current.AtmosphericThrustRatio, 0.001)
                || !Same(previous.AttitudeAccelerationDegS2, current.AttitudeAccelerationDegS2, 0.01)) return false;
            foreach (var field in new[] { (previous.AtmosphereHeight, current.AtmosphereHeight),
                (previous.SeaLevelDensity, current.SeaLevelDensity), (previous.DragArea, current.DragArea) })
                if (!Same(field.Item1, field.Item2)) return false;
            for (int i = 0; i < previous.Phases.Length; i++)
            {
                var a = previous.Phases[i]; var b = current.Phases[i];
                if (a.StartsStage != b.StartsStage || !Same(a.Thrust, b.Thrust, 1)
                    || !Same(a.MassFlow, b.MassFlow, 0.001) || !Same(a.StartMass, b.StartMass, 1)
                    || !Same(a.FullThrottleSeconds, b.FullThrottleSeconds, 0.1)) return false;
            }
            if (current.StableOrbit && !force)
                return Same(previous.OrbitPeriapsis - previous.BodyRadius, current.OrbitPeriapsis - current.BodyRadius, 500)
                    && Same(previous.OrbitApoapsis - previous.BodyRadius, current.OrbitApoapsis - current.BodyRadius, 500)
                    && Same(previous.OrbitInclination, current.OrbitInclination, 0.0001);
            return Same(previous.Altitude, current.Altitude, 20)
                && Same(previous.RadialSpeed, current.RadialSpeed, 0.1)
                && Same(previous.TangentialSpeed, current.TangentialSpeed, 0.1)
                && Same(previous.RotationSpeed, current.RotationSpeed, 0.1);
        }
    }
}
