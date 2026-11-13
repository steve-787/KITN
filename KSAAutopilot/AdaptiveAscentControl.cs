using System;

namespace KSAAutopilot
{
    internal static class AdaptiveAscentControl
    {
        public static double Pitch(double profilePitch, double mu, double bodyRadius, double radius,
            double radialSpeed, double tangentialSpeed, double targetApAltitude, double maxAcceleration,
            double dynamicPressure, double atmosphereHeight)
        {
            double altitude = radius - bodyRadius;
            if (altitude < Math.Max(40000.0, atmosphereHeight * 0.5) || dynamicPressure > 2000
                || !(maxAcceleration > 0) || !(radius > 0) || !(mu > 0)) return profilePitch;
            double apRadius = bodyRadius + targetApAltitude;
            if (radius >= apRadius - 1000) return profilePitch;
            // Keep enough radial motion to climb toward the target while a low-thrust core
            // builds horizontal speed. The AP energy budget bounds that climb.
            double radialBudgetSquared = 2 * mu * (1 / radius - 1 / apRadius)
                + tangentialSpeed * tangentialSpeed * (radius * radius / (apRadius * apRadius) - 1);
            double goal = Math.Min(500, 0.65 * Math.Sqrt(Math.Max(0, radialBudgetSquared)));
            double support = mu / (radius * radius) - tangentialSpeed * tangentialSpeed / radius;
            double radialAcceleration = Math.Max(0, support + (goal - radialSpeed) / 25);
            double supportPitch = Math.Asin(Math.Clamp(radialAcceleration / maxAcceleration, 0, 0.7)) * 180 / Math.PI;
            return Math.Max(profilePitch, supportPitch);
        }
    }
}
