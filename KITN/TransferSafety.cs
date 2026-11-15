using System;

namespace KITN
{
    internal static class TransferSafety
    {
        public const double AlignmentLeadSeconds = 60;

        public static bool AxisSettled(double rateDegS, double minimumPulseDegS)
        {
            // A jet's smallest impulse can exceed the fixed precision of a reaction wheel.
            double limit = double.IsFinite(minimumPulseDegS) && minimumPulseDegS > 0
                ? Math.Clamp(minimumPulseDegS + 0.05, 0.15, 1) : 0.15;
            return double.IsFinite(rateDegS) && Math.Abs(rateDegS) <= limit;
        }

        public static bool Settled(double angleDeg, double rateDegS)
            => double.IsFinite(angleDeg) && double.IsFinite(rateDegS)
                && angleDeg <= 1 && angleDeg >= 0 && rateDegS <= 0.15 && rateDegS >= 0;

        public static bool CanIgnite(double now, double ignition, bool settled, bool warping, double speed)
            => double.IsFinite(now) && double.IsFinite(ignition) && settled && !warping
                && speed > 0 && speed <= 1 && now >= ignition && now <= ignition + 5;

        public static double SurfaceRadius(double mean, double terrain)
            => Math.Max(mean, double.IsFinite(terrain) && terrain > 0 ? terrain : mean);

        public static string ArrivalError(double altitude, double mean, double terrain, double atmosphere)
        {
            if (!double.IsFinite(altitude) || !double.IsFinite(mean) || mean <= 0 || altitude < 0)
                return "transfer.invalid_periapsis";
            double radius = mean + altitude;
            if (radius <= SurfaceRadius(mean, terrain)) return "transfer.arrival_surface";
            if (double.IsFinite(atmosphere) && atmosphere > mean && radius <= atmosphere)
                return "transfer.arrival_atmosphere";
            return null;
        }

        public static double ImprovedArrivalAltitude(double requested, double mean, double terrain, double atmosphere, double predicted = double.NaN)
        {
            double limit = SurfaceRadius(mean, terrain);
            if (double.IsFinite(atmosphere)) limit = Math.Max(limit, atmosphere);
            double safeAltitude = limit - mean + 10000;
            if (!double.IsFinite(requested) || requested < 0) requested = Math.Max(100000, safeAltitude);
            // Compensate for the measured finite-burn shortfall rather than just repeating the minimum altitude.
            double correction = double.IsFinite(predicted) ? Math.Max(0, safeAltitude - predicted) : 0;
            return Math.Max(safeAltitude, requested + correction);
        }

        public static bool NearbyAltitude(double currentAltitude, double distance, double radius, double soi, bool retained)
            => double.IsFinite(distance) && double.IsFinite(radius) && radius > 0 && soi > radius
                && distance <= 2 * soi && distance > 0
                && distance - radius < currentAltitude * (retained ? 1.1 : 0.9);

        // An impulsive correction to the near edge of the destination SOI at the selected arrival.
        // This is an estimate for the predicted coast, not a measure of remaining onboard fuel.
        public static double SoiCorrectionDeltaV(double mu, LandingVector position, LandingVector velocity,
            LandingVector predictedArrival, LandingVector targetArrival, double targetSoi, double remainingSeconds)
        {
            if (!double.IsFinite(mu) || mu <= 0 || !position.IsFinite || !velocity.IsFinite
                || !predictedArrival.IsFinite || !targetArrival.IsFinite || !double.IsFinite(targetSoi) || targetSoi < 0
                || !double.IsFinite(remainingSeconds) || remainingSeconds <= 0 || position.Length < 1 || targetArrival.Length < 1)
                return double.NaN;
            var miss = predictedArrival - targetArrival;
            if (miss.Length <= targetSoi) return 0;
            var endpoint = targetArrival + miss.Unit * (targetSoi * 0.99);
            var normal = LandingVector.Cross(position, velocity);
            if (normal.Length < 1e-12) return double.NaN;
            double best = double.PositiveInfinity;
            foreach (int revolutions in new[] { 0, 1, -1, 2, -2 })
            {
                try
                {
                    var (required, _) = TransferLambert.Solve(mu, position, endpoint, remainingSeconds,
                        TransferGeometry.Prograde, revolutions, normal);
                    double correction = (required - velocity).Length;
                    if (double.IsFinite(correction)) best = Math.Min(best, correction);
                }
                catch (Exception) { /* That Lambert branch may have no feasible solution. */ }
            }
            return double.IsFinite(best) ? best : double.NaN;
        }

        // At periapsis the circular capture velocity is tangential to the measured incoming orbit.
        public static bool Capture(double mu, LandingVector position, LandingVector velocity, out LandingVector deltaV)
        {
            deltaV = default;
            double radius = position.Length;
            if (!(mu > 0 && radius > 0) || !position.IsFinite || !velocity.IsFinite) return false;
            var tangent = velocity - position.Unit * LandingVector.Dot(velocity, position.Unit);
            if (tangent.Length < 0.001) return false;
            deltaV = tangent.Unit * Math.Sqrt(mu / radius) - velocity;
            return deltaV.IsFinite && deltaV.Length > 0.01;
        }
    }
}
