using System;

namespace KITN
{
    /// <summary>Conservative limits for the native inertia-aware impulse/coast/brake controller.</summary>
    internal static class RcsSteering
    {
        public const double DeadbandDeg = 0.6;
        public const double CorrectionAngleDeg = 5;

        public static bool EnvironmentPermitted(bool allowed, string bodyId, string templateId,
            double radius, double atmosphereRadius)
        {
            if (!allowed) return false;
            bool earth = string.Equals(bodyId, "Earth", StringComparison.OrdinalIgnoreCase)
                || string.Equals(templateId, "Earth", StringComparison.OrdinalIgnoreCase);
            // Use the body's actual atmosphere boundary, even outside the physics bubble.
            // Unknown Earth geometry must not enable jets inside the restricted region.
            return !earth || double.IsFinite(radius) && double.IsFinite(atmosphereRadius)
                && atmosphereRadius > 0 && radius > atmosphereRadius;
        }

        public static bool Permitted(bool allowed, bool transfer, double errorDeg)
            => allowed && double.IsFinite(errorDeg) && errorDeg >= 0
                && (transfer || errorDeg <= CorrectionAngleDeg);

        public static double RateLimit(double requestedRadS, double errorDeg, double authorityRadS2)
        {
            const double radians = Math.PI / 180;
            if (!double.IsFinite(requestedRadS) || requestedRadS <= 0) return 0;
            if (!double.IsFinite(errorDeg) || errorDeg < 0) return 0;
            double cap = (errorDeg > CorrectionAngleDeg ? 1.5 : 0.5) * radians;
            if (double.IsFinite(authorityRadS2) && authorityRadS2 > 0)
            {
                // Authority is torque / moment of inertia, supplied by KSA for the current vehicle.
                // Reserve half of the remaining angle for braking; heavy craft turn more slowly.
                double brakingAngle = Math.Max(DeadbandDeg * 0.5, errorDeg - DeadbandDeg) * radians;
                cap = Math.Min(cap, Math.Sqrt(authorityRadS2 * brakingAngle));
            }
            else cap = Math.Min(cap, 0.5 * radians);
            return Math.Min(requestedRadS, cap);
        }
    }
}
