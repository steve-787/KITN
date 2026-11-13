using System;

namespace KITN
{
    /// <summary>Two-body orbit and finite-burn calculations, independent of game frames.</summary>
    internal static class OrbitInsertion
    {
        internal readonly struct Elements
        {
            public readonly double ApoapsisRadius, PeriapsisRadius;
            public bool Bound => double.IsFinite(ApoapsisRadius) && ApoapsisRadius > 0.0;
            public Elements(double ap, double pe) { ApoapsisRadius = ap; PeriapsisRadius = pe; }
        }

        public static Elements CalculateOrbit(double mu, double radius, double radialSpeed, double tangentialSpeed)
        {
            if (!(mu > 0.0) || !(radius > 0.0) || !double.IsFinite(radialSpeed) || !double.IsFinite(tangentialSpeed))
                return new Elements(double.NaN, double.NaN);
            double h = radius * tangentialSpeed;
            double energy = 0.5 * (radialSpeed * radialSpeed + tangentialSpeed * tangentialSpeed) - mu / radius;
            double e = Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * energy * h * h / (mu * mu)));
            double pe = h * h / (mu * (1.0 + e));
            return new Elements(energy < 0.0 ? -mu / (2.0 * energy) * (1.0 + e) : double.PositiveInfinity, pe);
        }

        public static double TargetTangentialSpeed(double mu, double radius, double targetPeRadius, double targetApRadius)
        {
            double semiMajor = 0.5 * (targetPeRadius + targetApRadius);
            return mu > 0.0 && radius > 0.0 && semiMajor > 0.0
                ? Math.Sqrt(Math.Max(0.0, mu * (2.0 / radius - 1.0 / semiMajor))) : double.NaN;
        }

        public static double ApoapsisTangentialSpeed(double mu, double radius, double tangentialSpeed, double apRadius)
            => apRadius > 0.0 && double.IsFinite(apRadius) ? radius * tangentialSpeed / apRadius : double.NaN;

        // The rocket can be above the requested apoapsis while the orbit is still unsafe.
        // First close periapsis at the current altitude; lowering the excess apoapsis is a later burn.
        public static double CaptureTangentialSpeed(double mu, double radius, double targetPeRadius, double targetApRadius)
            => TargetTangentialSpeed(mu, radius, targetPeRadius, Math.Max(radius, targetApRadius));

        public static double BurnDuration(double deltaV, double mass, double thrust, double massFlow)
        {
            if (!double.IsFinite(deltaV) || deltaV < 0.0 || !(mass > 0.0) || !(thrust > 0.0)) return double.NaN;
            if (massFlow > 1e-6 && double.IsFinite(massFlow))
                return mass / massFlow * (1.0 - Math.Exp(-deltaV * massFlow / thrust));
            return deltaV * mass / thrust;
        }

        public static double BurnLead(double duration)
            => double.IsFinite(duration) ? Math.Max(2.0, duration * 0.5 + 2.0) : double.NaN;

        public static bool ReadyToBurn(double altitude, double timeToAp, double duration, double radialSpeed)
            => altitude >= 100000.0 && double.IsFinite(timeToAp) && double.IsFinite(duration)
                && duration >= 0.0 && (radialSpeed <= 0.0 || timeToAp <= BurnLead(duration));

        public static bool ReadyToBurn(double altitude, double timeToAp, double duration, double radialSpeed,
            double periapsisAltitude, double atmosphereHeight)
        {
            // A rebuilt rocket can close a safe low orbit before reaching the target height.
            // Descending there means AP is on the next revolution, not that capture must fire now.
            double safeAltitude = Math.Max(100000.0, atmosphereHeight + 5000.0);
            bool safeCoast = double.IsFinite(periapsisAltitude) && periapsisAltitude >= safeAltitude;
            return altitude >= safeAltitude && double.IsFinite(timeToAp) && double.IsFinite(duration)
                && duration >= 0.0 && (timeToAp <= BurnLead(duration) || !safeCoast && radialSpeed <= 0.0);
        }

        public static double OrbitTolerance(double targetAltitude) => Math.Max(2000.0, targetAltitude * 0.01);

        public static bool OrbitAchieved(double ap, double pe, double targetAp, double targetPe)
            => double.IsFinite(ap) && double.IsFinite(pe)
                && Math.Abs(ap - targetAp) <= OrbitTolerance(targetAp)
                && Math.Abs(pe - targetPe) <= OrbitTolerance(targetPe);

        public static double ApoapsisCeiling(double targetAltitude)
            => targetAltitude + Math.Max(10000.0, targetAltitude * 0.05);

        // Cancel radial motion and support a finite burn against gravity. An unsafe orbit must
        // never be pushed down merely because the current altitude exceeds the requested orbit.
        public static double RadialAcceleration(double mu, double radius, double tangentialSpeed,
            double radialSpeed, double targetRadius, double responseSeconds)
        {
            double heightError = targetRadius - radius;
            double desiredRadialSpeed = heightError > 500.0 ? Math.Clamp(heightError / 30.0, 0.0, 100.0) : 0.0;
            double support = mu / (radius * radius) - tangentialSpeed * tangentialSpeed / radius;
            double remainingSpeed = Math.Sqrt(mu / radius) - tangentialSpeed;
            double response = remainingSpeed < 200.0 ? 5.0 : Math.Max(5.0, responseSeconds);
            return support + (desiredRadialSpeed - radialSpeed) / response;
        }

        internal readonly struct Command
        {
            public readonly double TargetSpeed, RadialAcceleration, TangentialAcceleration, PitchDeg, Throttle, DeltaV;
            public Command(double speed, double radial, double tangent, double maxAcceleration, double deltaV)
            {
                TargetSpeed = speed;
                RadialAcceleration = radial;
                TangentialAcceleration = tangent;
                PitchDeg = Math.Atan2(radial, tangent) * 180.0 / Math.PI;
                Throttle = maxAcceleration > 0.0
                    ? Math.Clamp(Math.Sqrt(radial * radial + tangent * tangent) / maxAcceleration, 0.0, 1.0) : 0.0;
                DeltaV = deltaV;
            }
        }

        public static Command Capture(double mu, double radius, double radialSpeed, double tangentialSpeed,
            double targetPeRadius, double targetApRadius, double maxAcceleration)
        {
            double speed = CaptureTangentialSpeed(mu, radius, targetPeRadius, targetApRadius);
            double radial = RadialAcceleration(mu, radius, tangentialSpeed, radialSpeed, targetPeRadius, 20.0);
            double tangent = Math.Clamp((speed - tangentialSpeed) / 6.0,
                -Math.Max(0.1, maxAcceleration), Math.Max(0.1, maxAcceleration));
            // Close to the target, suppress tiny radial support demands that would turn an
            // otherwise nearly finished prograde burn through 90 degrees.
            if (Math.Abs(radialSpeed) < 0.5 && radius >= targetPeRadius - 500.0) radial = 0.0;
            return new Command(speed, radial, tangent, maxAcceleration,
                Math.Sqrt((speed - tangentialSpeed) * (speed - tangentialSpeed) + radialSpeed * radialSpeed));
        }

        public static Command Trim(double mu, double radius, double radialSpeed, double tangentialSpeed,
            double targetPeRadius, double targetApRadius, double maxAcceleration)
        {
            double speed = TargetTangentialSpeed(mu, radius, targetPeRadius, targetApRadius);
            double tangent = (speed - tangentialSpeed) / 3.0;
            double radial = mu / (radius * radius) - tangentialSpeed * tangentialSpeed / radius - radialSpeed / 5.0;
            if (Math.Abs(radialSpeed) < 0.5) radial = 0.0;
            return new Command(speed, radial, tangent, maxAcceleration,
                Math.Sqrt((speed - tangentialSpeed) * (speed - tangentialSpeed) + radialSpeed * radialSpeed));
        }

        public static bool ReadyToTrim(double timeToApsis, double burnDuration, double distanceFromApsis,
            double radialSpeed, bool atPeriapsis)
            => double.IsFinite(burnDuration) && burnDuration >= 0.0
                && (double.IsFinite(timeToApsis) && timeToApsis <= BurnLead(burnDuration)
                    || distanceFromApsis <= 250.0 && (atPeriapsis ? radialSpeed >= 0.0 : radialSpeed <= 0.0));

        public static double ThrottleForDeltaV(double deltaV, double maxAcceleration, double responseSeconds)
            => double.IsFinite(deltaV) && maxAcceleration > 0.0
                ? Math.Clamp(deltaV / (maxAcceleration * Math.Max(1.0, responseSeconds)), 0.0, 1.0) : 0.0;

        public static double AscentThrottle(double mu, double bodyRadius, double radius, double radialSpeed,
            double tangentialSpeed, double targetApAltitude, double maxAcceleration, double actualPitchDeg,
            double simulationStep, out double predictedApAltitude)
        {
            predictedApAltitude = double.NaN;
            if (!(maxAcceleration > 0.0)) return 1.0; // Allow ignition/staging while performance is unavailable.
            if (!double.IsFinite(actualPitchDeg) || !(radius > 0.0) || !(mu > 0.0)) return 0.0;
            double horizon = Math.Max(1.5, Math.Min(3.0, simulationStep * 2.0 + 1.0));
            double pitch = actualPitchDeg * Math.PI / 180.0;
            double Predict(double throttle)
            {
                double vr = radialSpeed + maxAcceleration * throttle * Math.Sin(pitch) * horizon;
                double vt = tangentialSpeed + maxAcceleration * throttle * Math.Cos(pitch) * horizon;
                return CalculateOrbit(mu, radius, vr, vt).ApoapsisRadius - bodyRadius;
            }
            double fullAp = Predict(1.0);
            if (fullAp <= targetApAltitude) { predictedApAltitude = fullAp; return 1.0; }
            double coastAp = Predict(0.0);
            if (!(coastAp < targetApAltitude)) { predictedApAltitude = coastAp; return 0.0; }
            double low = 0.0, high = 1.0;
            for (int i = 0; i < 18; i++)
            {
                double mid = (low + high) * 0.5;
                if (Predict(mid) <= targetApAltitude) low = mid; else high = mid;
            }
            predictedApAltitude = Predict(low);
            return low;
        }

        public static double GuardInsertionThrottle(double mu, double bodyRadius, double radius,
            double radialSpeed, double tangentialSpeed, double targetApAltitude, double maxAcceleration,
            double actualPitchDeg, double requestedThrottle, double simulationStep, out double predictedApAltitude)
        {
            double ceiling = ApoapsisCeiling(targetApAltitude);
            double horizon = Math.Max(1.5, Math.Min(3.0, simulationStep * 2.0 + 1.0));
            return GuardImpulse(mu, bodyRadius, radius, radialSpeed, tangentialSpeed, ceiling,
                maxAcceleration, actualPitchDeg, requestedThrottle, horizon, out predictedApAltitude);
        }

        // Radial corrections can LOWER apoapsis. Its response to a burn is not monotonic,
        // especially after passing AP, so the ascent-only binary search cannot be reused.
        public static double GuardImpulse(double mu, double bodyRadius, double radius, double radialSpeed,
            double tangentialSpeed, double ceiling, double maxAcceleration, double actualPitchDeg,
            double requestedThrottle, double duration, out double predictedApAltitude)
        {
            double coastAp = CalculateOrbit(mu, radius, radialSpeed, tangentialSpeed).ApoapsisRadius - bodyRadius;
            predictedApAltitude = coastAp;
            if (!(maxAcceleration > 0.0) || !double.IsFinite(actualPitchDeg) || !double.IsFinite(requestedThrottle)
                || !double.IsFinite(duration) || duration <= 0.0) return 0.0;
            requestedThrottle = Math.Clamp(requestedThrottle, 0.0, 1.0);
            double pitch = actualPitchDeg * Math.PI / 180.0;
            double Predict(double throttle) => CalculateOrbit(mu, radius,
                radialSpeed + maxAcceleration * throttle * Math.Sin(pitch) * duration,
                tangentialSpeed + maxAcceleration * throttle * Math.Cos(pitch) * duration).ApoapsisRadius - bodyRadius;
            bool Safe(double ap) => double.IsFinite(ap) && (coastAp <= ceiling ? ap <= ceiling
                : ap < coastAp - 0.001 || ap <= coastAp + 0.001 && Math.Cos(pitch) < -0.01);
            double fullAp = Predict(requestedThrottle);
            if (Safe(fullAp)) { predictedApAltitude = fullAp; return requestedThrottle; }
            double low = 0.0, high = requestedThrottle;
            // Find a safe interval even when both zero thrust and full requested thrust
            // are above the ceiling, but a smaller outward burn arrests the descent.
            const int samples = 32;
            for (int i = samples - 1; i >= 1; i--)
            {
                double candidate = requestedThrottle * i / samples;
                if (Safe(Predict(candidate))) { low = candidate; high = requestedThrottle * (i + 1) / samples; break; }
            }
            if (low == 0.0)
            {
                for (double candidate = requestedThrottle / 64.0; candidate > requestedThrottle / 1048576.0; candidate *= 0.5)
                    if (Safe(Predict(candidate))) { low = candidate; high = candidate * 2.0; break; }
            }
            if (low == 0.0 && !(coastAp < ceiling)) return 0.0;
            for (int i = 0; i < 18; i++)
            {
                double mid = (low + high) * 0.5;
                if (Safe(Predict(mid))) low = mid; else high = mid;
            }
            predictedApAltitude = Predict(low);
            return low;
        }
    }
}
