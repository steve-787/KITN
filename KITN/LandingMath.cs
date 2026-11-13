using System;

namespace KITN
{
    internal readonly struct LandingVector
    {
        public readonly double X, Y, Z;
        public LandingVector(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(LengthSquared);
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
        public LandingVector Unit => Length > 1e-12 ? this / Length : default;
        public static LandingVector operator +(LandingVector a, LandingVector b) => new LandingVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static LandingVector operator -(LandingVector a, LandingVector b) => new LandingVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static LandingVector operator -(LandingVector a) => a * -1;
        public static LandingVector operator *(LandingVector a, double b) => new LandingVector(a.X * b, a.Y * b, a.Z * b);
        public static LandingVector operator /(LandingVector a, double b) => a * (1 / b);
        public static double Dot(LandingVector a, LandingVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static LandingVector Cross(LandingVector a, LandingVector b) => new LandingVector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public LandingVector Horizontal(LandingVector up) => this - up * Dot(this, up);
        public LandingVector Limited(double maximum) => Length > maximum && maximum >= 0 ? Unit * maximum : this;
        public static LandingVector Rotate(LandingVector value, LandingVector angularVelocity, double seconds)
        {
            double rate = angularVelocity.Length;
            if (rate < 1e-12) return value;
            LandingVector axis = angularVelocity / rate;
            double angle = rate * seconds, c = Math.Cos(angle), s = Math.Sin(angle);
            return value * c + Cross(axis, value) * s + axis * (Dot(axis, value) * (1 - c));
        }
    }

    internal sealed class LandingBodyModel
    {
        public double Mu, Radius, AtmosphereHeight, DragArea, Mass, Thrust;
        public LandingVector AngularVelocity;
        public double[] DensitySamples = Array.Empty<double>();
        public double DensityStep = 2000;
        public double Density(double altitude)
        {
            if (DensitySamples.Length == 0 || altitude >= AtmosphereHeight) return 0;
            double cell = Math.Clamp(altitude / DensityStep, 0, DensitySamples.Length - 1);
            int i = (int)cell, next = Math.Min(i + 1, DensitySamples.Length - 1);
            double a = DensitySamples[i], b = DensitySamples[next];
            if (a <= 0 || b <= 0) return Math.Max(0, a + (b - a) * (cell - i));
            return Math.Exp(Math.Log(a) + (Math.Log(b) - Math.Log(a)) * (cell - i));
        }
        public LandingVector Acceleration(LandingVector position, LandingVector velocity)
        {
            double r = position.Length;
            LandingVector gravity = position * (-Mu / (r * r * r));
            LandingVector relative = velocity - LandingVector.Cross(AngularVelocity, position);
            double drag = 0.5 * Density(r - Radius) * Math.Max(0, DragArea) / Math.Max(1, Mass) * relative.Length;
            return gravity - relative * drag;
        }
    }

    internal readonly struct LandingPrediction
    {
        public readonly bool Valid;
        public readonly double Seconds, Mass;
        public readonly LandingVector Position, Velocity;
        public LandingPrediction(bool valid, double seconds, LandingVector position, LandingVector velocity, double mass = 0)
        { Valid = valid; Seconds = seconds; Position = position; Velocity = velocity; Mass = mass; }
    }

    internal readonly struct LandingDescentCommand
    {
        public readonly LandingVector Acceleration;
        public readonly double DesiredVerticalSpeed, StoppingHeight, HorizontalDistance, AlignmentHeight, TimeToGo;
        public LandingDescentCommand(LandingVector acceleration, double speed, double stoppingHeight, double distance,
            double alignmentHeight, double timeToGo)
        { Acceleration = acceleration; DesiredVerticalSpeed = speed; StoppingHeight = stoppingHeight; HorizontalDistance = distance;
            AlignmentHeight = alignmentHeight; TimeToGo = timeToGo; }
    }

    /// <summary>Return targeting and powered descent in an inertial frame, independent of the game API.</summary>
    internal static class LandingMath
    {
        public const double HandoverHeight = 5000;

        public static double SurfaceDistance(LandingVector position, LandingVector target, double radius)
            => position.IsFinite && target.IsFinite && position.Length > 0 && target.Length > 0 && radius > 0
                ? radius * Math.Acos(Math.Clamp(LandingVector.Dot(position.Unit, target.Unit), -1, 1)) : double.NaN;

        public static bool Lambert(double mu, LandingVector start, LandingVector end, LandingVector orbitNormal,
            double seconds, out LandingVector velocity)
        {
            velocity = default;
            double r1 = start.Length, r2 = end.Length;
            if (!(mu > 0 && r1 > 0 && r2 > 0 && seconds > 0) || !start.IsFinite || !end.IsFinite) return false;
            double cosine = Math.Clamp(LandingVector.Dot(start, end) / (r1 * r2), -1, 1);
            double sine = Math.Sqrt(Math.Max(0, 1 - cosine * cosine));
            if (LandingVector.Dot(LandingVector.Cross(start, end), orbitNormal) < 0) sine = -sine;
            if (Math.Abs(sine) < 1e-7 || 1 - cosine < 1e-10) return false;
            double a = sine * Math.Sqrt(r1 * r2 / (1 - cosine));
            double low = -4 * Math.PI * Math.PI, high = 4 * Math.PI * Math.PI - 1e-5;
            double y = 0, c = 0;
            for (int i = 0; i < 70; i++)
            {
                double z = (low + high) / 2;
                Stumpff(z, out c, out double s);
                y = r1 + r2 + a * (z * s - 1) / Math.Sqrt(c);
                if (!(y > 0) || !double.IsFinite(y)) { low = z; continue; }
                double time = (Math.Pow(y / c, 1.5) * s + a * Math.Sqrt(y)) / Math.Sqrt(mu);
                if (time < seconds) low = z; else high = z;
            }
            double g = a * Math.Sqrt(y / mu);
            if (!double.IsFinite(g) || Math.Abs(g) < 1e-9) return false;
            velocity = (end - start * (1 - y / r1)) / g;
            return velocity.IsFinite;
        }

        private static void Stumpff(double z, out double c, out double s)
        {
            if (Math.Abs(z) < 1e-7) { c = 0.5 - z / 24 + z * z / 720; s = 1.0 / 6 - z / 120 + z * z / 5040; }
            else if (z > 0) { double root = Math.Sqrt(z); c = (1 - Math.Cos(root)) / z; s = (root - Math.Sin(root)) / (root * root * root); }
            else { double root = Math.Sqrt(-z); c = (Math.Cosh(root) - 1) / -z; s = (Math.Sinh(root) - root) / (root * root * root); }
        }

        public static void Step(LandingBodyModel body, ref LandingVector position, ref LandingVector velocity, double dt,
            LandingVector thrustAcceleration = default)
        {
            LandingVector v1 = velocity, a1 = body.Acceleration(position, velocity) + thrustAcceleration;
            LandingVector v2 = velocity + a1 * (dt / 2), a2 = body.Acceleration(position + v1 * (dt / 2), v2) + thrustAcceleration;
            LandingVector v3 = velocity + a2 * (dt / 2), a3 = body.Acceleration(position + v2 * (dt / 2), v3) + thrustAcceleration;
            LandingVector v4 = velocity + a3 * dt, a4 = body.Acceleration(position + v3 * dt, v4) + thrustAcceleration;
            position += (v1 + v2 * 2 + v3 * 2 + v4) * (dt / 6);
            velocity += (a1 + a2 * 2 + a3 * 2 + a4) * (dt / 6);
        }

        public static LandingPrediction Predict(LandingBodyModel body, LandingVector position, LandingVector velocity,
            double arrivalRadius, double maximumSeconds = 6000)
        {
            if (!position.IsFinite || !velocity.IsFinite || !(body.Mu > 0 && arrivalRadius > 0)) return default;
            for (double elapsed = 0; elapsed < maximumSeconds;)
            {
                double altitude = position.Length - body.Radius;
                double dt = altitude > body.AtmosphereHeight ? 5 : altitude > 35000 ? 2 : 0.5;
                LandingVector previousPosition = position, previousVelocity = velocity;
                double previousRadius = position.Length;
                Step(body, ref position, ref velocity, dt);
                if (!position.IsFinite || !velocity.IsFinite || velocity.Length > 100000) return default;
                double radius = position.Length;
                if (radius <= arrivalRadius && previousRadius > arrivalRadius)
                {
                    double fraction = Math.Clamp((previousRadius - arrivalRadius) / (previousRadius - radius), 0, 1);
                    return new LandingPrediction(true, elapsed + fraction * dt,
                        previousPosition + (position - previousPosition) * fraction,
                        previousVelocity + (velocity - previousVelocity) * fraction, body.Mass);
                }
                if (radius < body.Radius - 1000 || radius > body.Radius * 10) return default;
                elapsed += dt;
            }
            return default;
        }

        public static bool Target(LandingBodyModel body, LandingVector position, LandingVector seedVelocity,
            LandingVector padNow, double padAltitude, out LandingVector targetVelocity, out LandingPrediction prediction)
        {
            targetVelocity = seedVelocity;
            prediction = default;
            LandingVector up = position.Unit;
            LandingVector normal = LandingVector.Cross(position, seedVelocity).Unit;
            LandingVector axis1 = LandingVector.Cross(normal, up).Unit, axis2 = normal;
            if (axis1.LengthSquared < 0.5 || axis2.LengthSquared < 0.5) return false;
            double radius = body.Radius + padAltitude + HandoverHeight;
            LandingVector Error(LandingPrediction p) =>
                (p.Position - LandingVector.Rotate(padNow, body.AngularVelocity, p.Seconds).Unit * radius);
            for (int iteration = 0; iteration < 10; iteration++)
            {
                prediction = Predict(body, position, targetVelocity, radius);
                if (!prediction.Valid) return false;
                LandingVector error = Error(prediction);
                if (error.Length < 750) return true;
                LandingVector endUp = prediction.Position.Unit;
                LandingVector along = LandingVector.Cross(normal, endUp).Unit, across = LandingVector.Cross(endUp, along).Unit;
                var p1 = Predict(body, position, targetVelocity + axis1, radius);
                var p2 = Predict(body, position, targetVelocity + axis2, radius);
                if (!p1.Valid || !p2.Valid) return false;
                LandingVector d1 = Error(p1) - error, d2 = Error(p2) - error;
                double a = LandingVector.Dot(d1, along), b = LandingVector.Dot(d2, along);
                double c = LandingVector.Dot(d1, across), d = LandingVector.Dot(d2, across);
                double determinant = a * d - b * c;
                if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-6) return false;
                double e1 = LandingVector.Dot(error, along), e2 = LandingVector.Dot(error, across);
                LandingVector correction = (axis1 * ((b * e2 - d * e1) / determinant)
                    + axis2 * ((c * e1 - a * e2) / determinant)).Limited(100);
                bool improved = false;
                for (double fraction = 1; fraction >= 0.03125; fraction /= 2)
                {
                    LandingVector next = targetVelocity + correction * fraction;
                    var p = Predict(body, position, next, radius);
                    if (p.Valid && Error(p).Length < error.Length)
                    { targetVelocity = next; improved = true; break; }
                }
                if (!improved) return false;
            }
            return prediction.Valid && Error(prediction).Length < 2500;
        }

        public static bool TargetReturn(LandingBodyModel body, LandingVector position, LandingVector orbitalVelocity,
            LandingVector seedVelocity, LandingVector padNow, double padAltitude, double exhaustVelocity,
            out LandingVector targetVelocity, out LandingPrediction prediction)
        {
            targetVelocity = seedVelocity; prediction = default;
            if (!(body.Mass > 0 && exhaustVelocity > 0)) return false;
            var entry = new LandingBodyModel { Mu = body.Mu, Radius = body.Radius, AtmosphereHeight = body.AtmosphereHeight,
                DragArea = body.DragArea, AngularVelocity = body.AngularVelocity, DensitySamples = body.DensitySamples,
                DensityStep = body.DensityStep, Thrust = body.Thrust };
            // A large deorbit burn changes the ballistic coefficient. Target the mass
            // after that burn rather than predicting the footprint with launch mass.
            for (int iteration = 0; iteration < 6; iteration++)
            {
                entry.Mass = body.Mass * Math.Exp(-(targetVelocity - orbitalVelocity).Length / exhaustVelocity);
                if (!Target(entry, position, targetVelocity, padNow, padAltitude, out targetVelocity, out prediction)) return false;
                double correctedMass = body.Mass * Math.Exp(-(targetVelocity - orbitalVelocity).Length / exhaustVelocity);
                if (Math.Abs(correctedMass - entry.Mass) <= entry.Mass * 0.0001)
                {
                    entry.Mass = correctedMass;
                    prediction = Predict(entry, position, targetVelocity, body.Radius + padAltitude + HandoverHeight);
                    if (!prediction.Valid) return false;
                    LandingVector arrival = LandingVector.Rotate(padNow, body.AngularVelocity, prediction.Seconds).Unit
                        * (body.Radius + padAltitude + HandoverHeight);
                    return (prediction.Position - arrival).Length < 2500;
                }
            }
            return false;
        }

        public static double AvailableDeltaV(double mass, double usableFuel, double exhaustVelocity)
            => mass > 0 && usableFuel > 0 && usableFuel < mass && exhaustVelocity > 0
                ? exhaustVelocity * Math.Log(mass / (mass - usableFuel)) : 0;

        public static double LandingReserve(LandingBodyModel body, LandingPrediction prediction)
        {
            if (!prediction.Valid) return double.PositiveInfinity;
            LandingVector relative = prediction.Velocity - LandingVector.Cross(body.AngularVelocity, prediction.Position);
            double down = Math.Max(0, -LandingVector.Dot(relative, prediction.Position.Unit));
            double gravity = body.Mu / prediction.Position.LengthSquared;
            double time = 2 * HandoverHeight / Math.Max(50, down);
            if (body.Thrust > 0)
            {
                double net = body.Thrust / Math.Max(1, prediction.Mass > 0 ? prediction.Mass : body.Mass) - gravity;
                if (net <= 0) return double.PositiveInfinity;
                time = Math.Max(time, down / net);
                time *= 1 + 0.5 * gravity / net;
            }
            // Braking alone omits the gravity loss while lining up over the pad. Keep that
            // reserve, plus margin for model/attitude errors, before allowing a deorbit burn.
            return Math.Max(500, relative.Length * 1.35 + gravity * time + 250);
        }

        public static LandingDescentCommand Descend(LandingBodyModel body, LandingVector position, LandingVector velocity,
            LandingVector target, double clearance, double maximumAcceleration)
        {
            LandingVector up = position.Unit;
            LandingVector relative = velocity - LandingVector.Cross(body.AngularVelocity, position);
            double vertical = LandingVector.Dot(relative, up);
            LandingVector horizontal = relative.Horizontal(up), error = (target - position).Horizontal(up);
            double gravity = body.Mu / position.LengthSquared;
            double net = Math.Max(0.1, maximumAcceleration - gravity);
            double stoppingHeight = Math.Max(0, vertical * vertical - 0.16) / (2 * net);
            // Maintain clearance until both lateral position and motion have converged.
            // A ground-level lateral correction is unsafe even with a gentle vertical speed.
            double alignmentHeight = Math.Clamp(Math.Max((error.Length - 15) * 0.03, (horizontal.Length - 0.5) * 5), 0, 300);
            double heightError = clearance - alignmentHeight + Math.Min(0, vertical) * 0.18 - 0.3;
            double controlHeight = Math.Max(0, heightError);
            double lateralBudget = Math.Max(1, maximumAcceleration * 0.55);
            double towards = LandingVector.Dot(horizontal, error.Unit);
            // Time bounds for the initial and final acceleration of a cubic approach.
            // They work for motion towards or away from the pad, including zero speed.
            double lateralTime = Math.Max((-2 * towards + Math.Sqrt(4 * towards * towards + 6 * lateralBudget * error.Length)) / lateralBudget,
                (-towards + Math.Sqrt(towards * towards + 6 * lateralBudget * error.Length)) / lateralBudget);
            lateralTime = Math.Max(lateralTime, horizontal.Length / lateralBudget);
            double approachSpeed = Math.Max(3, Math.Max(-vertical, 0.6 * Math.Sqrt(2 * gravity * controlHeight)));
            // The 12-second terminal horizon leaves time for the native 8 deg/s attitude
            // controller. A shorter lateral horizon causes oscillations with real engines.
            double time = Math.Clamp(Math.Max(2 * controlHeight / approachSpeed, lateralTime), 12, 100);
            double desiredVertical = -Math.Clamp(2 * controlHeight / time, 0.4, 2000);
            double verticalDemand = -6 * heightError / (time * time) - 4 * vertical / time - 0.8 / time
                - LandingVector.Dot(body.Acceleration(position, velocity), up);
            LandingVector lateral = error * (6 / (time * time)) - horizontal * (4 / time);
            LandingVector rotation = LandingVector.Cross(body.AngularVelocity, LandingVector.Cross(body.AngularVelocity, position))
                + LandingVector.Cross(body.AngularVelocity, relative) * 2;
            if (vertical < -0.6 && (controlHeight < 5 || stoppingHeight * 1.2 >= controlHeight))
                verticalDemand = Math.Max(verticalDemand,
                    gravity + 1.12 * (vertical * vertical - 0.16) / (2 * Math.Max(0.15, controlHeight)));
            // Keep a useful upward attitude even between minimum-throttle pulses. Do not
            // aim the rocket downwards when aerodynamic braking already meets the demand.
            verticalDemand = Math.Clamp(verticalDemand + LandingVector.Dot(rotation, up), 0.05 * maximumAcceleration, maximumAcceleration);
            lateral += rotation.Horizontal(up);
            double lateralLimit = Math.Min(maximumAcceleration * 0.65,
                Math.Sqrt(Math.Max(0, maximumAcceleration * maximumAcceleration - verticalDemand * verticalDemand)));
            lateral = lateral.Limited(lateralLimit);
            return new LandingDescentCommand(up * verticalDemand + lateral,
                desiredVertical, stoppingHeight, error.Length, alignmentHeight, time);
        }
    }

    internal sealed class LandingTouchdown
    {
        private double _since = double.NaN, _last = double.NaN;
        public bool Confirm(double time, bool terrainContact, double clearance, double surfaceSpeed, double distance, double padRadius)
        {
            if (!double.IsFinite(time) || time < _last || !terrainContact || clearance > 3 || surfaceSpeed > 1.5
                || !double.IsFinite(clearance) || !double.IsFinite(surfaceSpeed) || !double.IsFinite(distance)
                || !double.IsFinite(padRadius) || distance > Math.Max(25, padRadius))
                _since = double.NaN;
            _last = time;
            if (!terrainContact || clearance > 3 || surfaceSpeed > 1.5 || distance > Math.Max(25, padRadius)
                || !double.IsFinite(time) || !double.IsFinite(clearance) || !double.IsFinite(surfaceSpeed)
                || !double.IsFinite(distance) || !double.IsFinite(padRadius)) return false;
            if (!double.IsFinite(_since)) _since = time;
            return time - _since >= 0.75;
        }
        public void Reset() { _since = _last = double.NaN; }
    }
}
