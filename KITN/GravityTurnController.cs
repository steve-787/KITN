using System;

namespace KITN
{
    /// <summary>Continuous LEO pitch targets; native KSA attitude control supplies damping.</summary>
    internal sealed class GravityTurnController
    {
        public double CommandedPitchDeg { get; private set; } = 90.0;
        public string HoldReason { get; private set; } = "initializing";

        public void Reset(double measuredPitchDeg)
        {
            CommandedPitchDeg = double.IsFinite(measuredPitchDeg) ? Math.Clamp(measuredPitchDeg, 0.0, 90.0) : 90.0;
            HoldReason = "initializing";
        }

        public static double ProfilePitch(double altitudeM, double startM, double endM, double shape)
        {
            if (!double.IsFinite(altitudeM) || !double.IsFinite(startM) || !double.IsFinite(endM)) return 90.0;
            double progress = Math.Clamp((altitudeM - startM) / Math.Max(1.0, endM - startM), 0.0, 1.0);
            double exponent = double.IsFinite(shape) ? Math.Clamp(shape, 0.8, 2.0) : 1.1;
            return 90.0 * (1.0 - Math.Pow(progress, exponent));
        }

        public static double RateLimit(double altitudeM, double configuredRateDegS)
        {
            double baseRate = double.IsFinite(configuredRateDegS) ? Math.Clamp(configuredRateDegS, 0.1, 1.5) : 1.0;
            double ramp = double.IsFinite(altitudeM) ? Math.Clamp((altitudeM - 10000.0) / 30000.0, 0.0, 1.0) : 0.0;
            return Math.Min(4.0, baseRate * (1.0 + 2.0 * ramp));
        }

        public static double LimitPitchForApoapsis(double profilePitchDeg, double altitudeM,
            double apoapsisM, double targetApoapsisM)
        {
            if (altitudeM < 20000.0 || !double.IsFinite(apoapsisM) || targetApoapsisM <= 0.0)
                return profilePitchDeg;
            // Build horizontal speed while approaching the ceiling instead of holding a radial climb.
            double fraction = Math.Clamp(apoapsisM / targetApoapsisM, 0.0, 1.0);
            double energyPitch = 90.0 * Math.Clamp((1.0 - fraction) / 0.65, 0.0, 1.0);
            return Math.Min(profilePitchDeg, energyPitch);
        }

        public static bool ReadyToTurn(double altitudeM, double startM, double surfaceSpeedMps,
            double minimumSpeedMps, double verticalSpeedMps, double measuredPitchDeg)
        {
            return double.IsFinite(altitudeM) && double.IsFinite(startM) && altitudeM >= startM
                && double.IsFinite(surfaceSpeedMps) && surfaceSpeedMps >= Math.Max(0.0, minimumSpeedMps)
                && double.IsFinite(verticalSpeedMps) && verticalSpeedMps > 0.0
                && double.IsFinite(measuredPitchDeg) && measuredPitchDeg >= 85.0;
        }

        public static double LimitPitchInAtmosphere(double profilePitchDeg, double flightPathPitchDeg,
            double surfaceSpeedMps, double dynamicPressurePa)
        {
            // Do not command more than three degrees below the flight path under aerodynamic load.
            return double.IsFinite(flightPathPitchDeg) && surfaceSpeedMps >= 100.0 && dynamicPressurePa >= 5000.0
                ? Math.Clamp(Math.Max(profilePitchDeg, flightPathPitchDeg - 3.0), 0.0, 90.0)
                : profilePitchDeg;
        }

        public double Update(double targetPitchDeg, double measuredPitchDeg, double pointingErrorDeg,
            double angularSpeedDegS, double maxRateDegS, double simulationDeltaSeconds)
        {
            HoldReason = "none";
            if (!double.IsFinite(simulationDeltaSeconds) || simulationDeltaSeconds <= 0.0)
                HoldReason = "simulation_not_advancing";
            else if (!double.IsFinite(measuredPitchDeg) || !double.IsFinite(pointingErrorDeg)
                || !double.IsFinite(angularSpeedDegS) || !double.IsFinite(targetPitchDeg) || !double.IsFinite(maxRateDegS))
                HoldReason = "invalid_attitude_measurement";
            else if (angularSpeedDegS > Math.Max(2.0, maxRateDegS * 4.0))
                HoldReason = "waiting_for_rotation_to_settle";
            else if (pointingErrorDeg > 3.0 || Math.Abs(measuredPitchDeg - CommandedPitchDeg) > 3.0)
                HoldReason = "waiting_for_vehicle_alignment";

            if (HoldReason != "none") return CommandedPitchDeg;
            double step = Math.Clamp(maxRateDegS, 0.1, 4.0) * Math.Min(simulationDeltaSeconds, 0.25);
            double target = Math.Clamp(targetPitchDeg, 0.0, 90.0);
            CommandedPitchDeg += Math.Clamp(target - CommandedPitchDeg, -step, step);
            return CommandedPitchDeg;
        }
    }
}
