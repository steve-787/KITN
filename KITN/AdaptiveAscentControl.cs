using System;

namespace KITN
{
    internal static class AdaptiveAscentControl
    {
        public static double Throttle(double mu, double bodyRadius, double radius, double radialSpeed,
            double tangentialSpeed, double targetApAltitude, double maxAcceleration, double pitchDeg,
            double maxPitchRateDegS, double surfaceSpeed, double density, double dynamicPressure,
            double atmosphereHeight, double simulationStep, out double predictedApAltitude, out string reason)
        {
            double throttle = OrbitInsertion.AscentThrottle(mu, bodyRadius, radius, radialSpeed, tangentialSpeed,
                targetApAltitude, maxAcceleration, pitchDeg, simulationStep, out predictedApAltitude);
            reason = throttle < 0.999 ? "predicted_ascent_apoapsis" : "ascent_full_thrust";
            if (!(maxAcceleration > 0) || !(radius > 0) || !(mu > 0) || !double.IsFinite(pitchDeg)) return throttle;
            double gravity = mu / (radius * radius);
            double support = gravity - tangentialSpeed * tangentialSpeed / radius;
            double sinPitch = Math.Sin(pitchDeg * Math.PI / 180);
            double altitude = radius - bodyRadius;
            // Preserve radial energy until attitude can turn the thrust sideways. This works with
            // measured pitch, so slow steering and sudden staging acceleration get the same guard.
            if (altitude > 1000 && pitchDeg > 35 && sinPitch > 0.1 && radialSpeed > 0)
            {
                double apRadius = bodyRadius + targetApAltitude;
                double budgetSquared = 2 * mu * (1 / radius - 1 / apRadius)
                    + tangentialSpeed * tangentialSpeed * (radius * radius / (apRadius * apRadius) - 1);
                double turnSeconds = Math.Max(20, (pitchDeg - 10) / Math.Max(0.1, maxPitchRateDegS) + 15);
                double goal = Math.Max(150, Math.Min(0.5 * Math.Sqrt(Math.Max(0, budgetSquared)),
                    0.65 * Math.Max(0, apRadius - radius) / turnSeconds));
                double limit = Math.Clamp((support + (goal - radialSpeed) / 5) / (maxAcceleration * sinPitch), 0, 1);
                if (limit < throttle) { throttle = limit; reason = "gravity_turn_radial_speed"; }
            }
            // Predict the pressure trend, including the fall in density while climbing. This
            // reduces throttle before max-Q, without slowing a low-thrust rocket below lift-off.
            if (density > 0 && atmosphereHeight > 0 && surfaceSpeed > 50 && radialSpeed > 0)
            {
                const double maxPressure = 30000;
                double allowedSpeed = Math.Sqrt(2 * maxPressure / density);
                double densityRelief = allowedSpeed * radialSpeed / (2 * Math.Max(1000, atmosphereHeight / 14));
                double dragSupport = dynamicPressure / 5300;
                double radialFraction = Math.Clamp(radialSpeed / surfaceSpeed, 0, 1);
                double thrustAlongPath = sinPitch * radialFraction
                    + Math.Cos(pitchDeg * Math.PI / 180) * Math.Sqrt(Math.Max(0, 1 - radialFraction * radialFraction));
                double limit = Math.Clamp((support * radialFraction + dragSupport + densityRelief + (allowedSpeed - surfaceSpeed) / 5)
                    / (maxAcceleration * Math.Max(0.1, thrustAlongPath)), 0, 1);
                if (limit < throttle) { throttle = limit; reason = "ascent_dynamic_pressure"; }
            }
            // Telemetry must describe the reduced command, rather than the rejected full-thrust prediction.
            double horizon = Math.Max(1.5, Math.Min(3.0, simulationStep * 2 + 1));
            predictedApAltitude = OrbitInsertion.CalculateOrbit(mu, radius,
                radialSpeed + maxAcceleration * throttle * sinPitch * horizon,
                tangentialSpeed + maxAcceleration * throttle * Math.Cos(pitchDeg * Math.PI / 180) * horizon)
                .ApoapsisRadius - bodyRadius;
            return throttle;
        }

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
