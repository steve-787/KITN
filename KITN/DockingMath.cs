using System;

namespace KITN
{
    internal enum DockingPhase { Idle, Matching, Retreating, Aligning, Approaching, Docking, Docked, Matched, Stopped,
        Planning, Transferring, Coasting, Braking, Rendezvoused, Routing, Correcting }

    internal enum DockingIssue { None, Lateral, Pointing, Orientation, AngularRate, BehindTarget, UnsafePath, NoProgress, InvalidMotion }

    internal readonly struct DockingCommand
    {
        public readonly DockingPhase Phase;
        public readonly LandingVector DesiredVelocity;
        public readonly LandingVector Waypoint;
        public readonly DockingIssue Issue;
        public readonly double CorrectionError;
        public readonly bool EntryCleared;
        public DockingCommand(DockingPhase phase, LandingVector velocity, DockingIssue issue = DockingIssue.None, double error = 0,
            LandingVector waypoint = default, bool entryCleared = false)
        { Phase = phase; DesiredVelocity = velocity; Issue = issue; CorrectionError = error; Waypoint = waypoint; EntryCleared = entryCleared; }
    }

    /// <summary>Port-relative approach on the outside of the target, with room to brake.</summary>
    internal static class DockingMath
    {
        public const double MaximumRange = 2000;
        private static readonly DockingSettings Defaults = new DockingSettings();
        public static DockingCommand Approach(LandingVector position, double pointingError, double angularRate,
            double acceleration, double standOff)
            => Approach(position, pointingError, angularRate, acceleration, standOff, Defaults);

        internal static DockingCommand Approach(LandingVector position, double pointingError, double angularRate,
            double acceleration, double standOff, DockingSettings settings, bool routeAround = false, double safeDistance = 0,
            double orientationError = double.NaN, bool closing = false, double nativeAngleWindow = 0, double nativeRateWindow = 0,
            LandingVector relativeVelocity = default, bool restart = false, bool entryCleared = false)
        {
            if (!position.IsFinite || !double.IsFinite(pointingError) || !double.IsFinite(angularRate)
                || !double.IsFinite(acceleration) || acceleration <= 0 || !double.IsFinite(standOff) || standOff <= 0
                || settings?.IsValid != true || !relativeVelocity.IsFinite)
                return new DockingCommand(DockingPhase.Aligning, default, DockingIssue.InvalidMotion);
            double lateral = Math.Sqrt(position.Y * position.Y + position.Z * position.Z);
            if (double.IsNaN(orientationError)) orientationError = pointingError;
            if (!double.IsFinite(orientationError)) return new DockingCommand(DockingPhase.Aligning, default, DockingIssue.InvalidMotion);
            double finalAngle = Math.Max(settings.AlignmentToleranceDeg, NativeWindow(nativeAngleWindow, 5));
            double finalRate = Math.Max(settings.MaximumAngularRateDegS, NativeWindow(nativeRateWindow, 1));
            if (safeDistance > 0)
            {
                // Every approach, including one from the front/side, first reaches the outside
                // alignment plane. A latched admission survives small correctable disturbances.
                double clearance = Math.Max(5, safeDistance);
                standOff = Math.Max(standOff, clearance + 5);
                double sideRate = Math.Sqrt(relativeVelocity.Y * relativeVelocity.Y + relativeVelocity.Z * relativeVelocity.Z);
                bool facing = pointingError <= finalAngle && orientationError <= finalAngle && angularRate <= finalRate;
                bool stable = lateral + sideRate * (0.5 + sideRate / acceleration) <= settings.CorridorRadiusM;
                bool atGate = position.X >= standOff - 0.25 && lateral <= settings.CorridorRadiusM * 0.75;
                entryCleared = !restart && (entryCleared || atGate && facing && stable);
                // Large disturbances revoke admission. Small errors are corrected in the
                // existing corridor rather than restarting an otherwise safe docking.
                if (orientationError > 90 || pointingError > 30 || lateral > Math.Max(2, position.X * 0.5))
                    entryCleared = false;
                if (!entryCleared)
                {
                    var gate = new LandingVector(standOff, 0, 0);
                    var outside = ClearanceWaypoint(position, relativeVelocity, gate, clearance, out bool bypass);
                    bool around = bypass || position.X < clearance + 2 && position.X < standOff - 0.25;
                    DockingIssue gateIssue = lateral > settings.CorridorRadiusM * 0.75 ? DockingIssue.Lateral
                        : pointingError > finalAngle ? DockingIssue.Pointing : orientationError > finalAngle ? DockingIssue.Orientation
                        : !stable || angularRate > finalRate ? DockingIssue.AngularRate : DockingIssue.None;
                    var gatePhase = restart ? DockingPhase.Retreating : around ? DockingPhase.Routing : DockingPhase.Aligning;
                    double travelSpeed = Math.Min(settings.MaximumSpeedMps, settings.AlignmentSpeedMps);
                    if (bypass) travelSpeed = Math.Min(travelSpeed, Math.Sqrt(acceleration * (clearance + 3) / (3 * settings.BrakingMargin)));
                    // A single vector keeps diagonal legs straight. Tangent checkpoints
                    // advance during travel, so intermediate corners do not require a stop.
                    var gateVelocity = TravelVelocity(outside - position, acceleration, travelSpeed, settings.BrakingMargin);
                    // Never authorize a diagonal crossing of the boundary. Existing inward
                    // momentum is braked while the lateral/attitude alignment is completed.
                    double inwardLimit = Math.Sqrt(acceleration * Math.Max(0, position.X - standOff) / settings.BrakingMargin);
                    if (!around && gateVelocity.X < -inwardLimit)
                        gateVelocity = new LandingVector(-inwardLimit, gateVelocity.Y, gateVelocity.Z);
                    double gateError = gateIssue == DockingIssue.Lateral ? lateral : gateIssue == DockingIssue.Pointing ? pointingError
                        : gateIssue == DockingIssue.Orientation ? orientationError : angularRate;
                    return new DockingCommand(gatePhase, gateVelocity.Limited(settings.MaximumSpeedMps),
                        around ? DockingIssue.BehindTarget : gateIssue, gateError, outside);
                }
            }
            // Translation is in the target-port frame, so small pointing errors and ongoing
            // roll alignment do not prevent a safe distant approach. Tighten over the last 30 m.
            double far = Math.Clamp((position.X - 3) / 30, 0, 1);
            double angleLimit = finalAngle + far * (Math.Max(8, finalAngle) - finalAngle);
            double orientationLimit = finalAngle + far * (Math.Max(35, finalAngle) - finalAngle);
            double rateLimit = finalRate + far * (Math.Max(finalRate, settings.MaximumTurnRateDegS * 1.2) - finalRate);
            if (safeDistance > 0) { angleLimit = orientationLimit = finalAngle; rateLimit = finalRate; }
            // Once closing, do not brake/retreat on every minimum-impulse excursion over
            // the entry threshold. A substantially larger error still stops the approach.
            if (closing)
            {
                angleLimit += Math.Max(0.5, angleLimit * 0.2);
                orientationLimit += Math.Max(0.5, orientationLimit * 0.2);
                rateLimit += Math.Max(0.05, rateLimit * 0.2);
            }
            bool aligned = pointingError <= angleLimit && orientationError <= orientationLimit && angularRate <= rateLimit;
            double corridor = settings.CorridorRadiusM + (closing ? Math.Min(0.05, settings.CorridorRadiusM * 0.25) : 0);
            LandingVector waypoint;
            DockingPhase phase;
            double speed;
            DockingIssue issue;
            if (safeDistance <= 0 && (routeAround || position.X < -0.5) && position.X < standOff - 0.5)
            {
                // Two legs outside an enclosing sphere: move sideways first, then around to the front.
                double clearance = Math.Max(5, safeDistance);
                var side = lateral > 0.01 ? new LandingVector(0, position.Y / lateral, position.Z / lateral) : new LandingVector(0, 1, 0);
                waypoint = lateral < clearance - 0.3
                    ? new LandingVector(position.X, 0, 0) + side * (clearance + 1)
                    : new LandingVector(standOff, position.Y, position.Z);
                phase = DockingPhase.Routing; speed = settings.AlignmentSpeedMps; issue = DockingIssue.BehindTarget;
            }
            else if (restart || (position.X < standOff - 0.5 && position.X <= 10
                && (lateral > Math.Max(2, position.X * 0.5) || orientationError > 90)))
            {
                phase = DockingPhase.Retreating;
                waypoint = new LandingVector(standOff, position.Y, position.Z); speed = settings.AlignmentSpeedMps;
                issue = restart ? DockingIssue.NoProgress : DockingIssue.UnsafePath;
            }
            else
            {
                issue = lateral > corridor ? DockingIssue.Lateral : pointingError > angleLimit ? DockingIssue.Pointing
                    : orientationError > orientationLimit ? DockingIssue.Orientation
                    : angularRate > rateLimit ? DockingIssue.AngularRate : DockingIssue.None;
                phase = issue != DockingIssue.None ? DockingPhase.Correcting
                    : position.X < 10 ? DockingPhase.Docking : DockingPhase.Approaching;
                speed = position.X > Math.Max(50, standOff) ? settings.MaximumSpeedMps
                    : position.X > 10 ? Math.Min(settings.MaximumSpeedMps, settings.NearSpeedMps)
                    : Math.Min(settings.MaximumSpeedMps, Math.Min(settings.NearSpeedMps, settings.ContactSpeedMps));
                // Keep translating while the remaining angles settle, but reduce closing speed.
                double quality = Math.Min(1, Math.Min(finalAngle / Math.Max(finalAngle, pointingError),
                    Math.Max(5, finalAngle) / Math.Max(Math.Max(5, finalAngle), orientationError)));
                quality = Math.Min(quality, finalRate / Math.Max(finalRate, angularRate));
                speed *= Math.Max(0.15, quality);
                speed = Math.Min(speed, Math.Sqrt(acceleration * Math.Max(0, position.X + 0.1) / settings.BrakingMargin));
                if (!aligned) speed = 0;
                if (lateral > corridor)
                {
                    // Correct sideways at the current distance. Slow/hold closing until there
                    // is enough time to enter the final corridor; do not reset to stand-off.
                    double lateralSpeed = Math.Sqrt(relativeVelocity.Y * relativeVelocity.Y + relativeVelocity.Z * relativeVelocity.Z);
                    double correctionTime = 2 * Math.Sqrt((lateral - corridor) * settings.BrakingMargin / acceleration)
                        + lateralSpeed / acceleration;
                    speed = Math.Min(speed, Math.Max(0, position.X - 3) / Math.Max(1, correctionTime));
                }
                if (safeDistance > 0 && position.X >= safeDistance)
                {
                    double lateralRate = Math.Sqrt(relativeVelocity.Y * relativeVelocity.Y + relativeVelocity.Z * relativeVelocity.Z);
                    bool corridorStable = lateral + lateralRate * (0.5 + lateralRate / acceleration) <= corridor;
                    if (!aligned || !corridorStable)
                        speed = Math.Min(speed, Math.Sqrt(acceleration * Math.Max(0, position.X - safeDistance - 2) / settings.BrakingMargin));
                }
                double sideSpeed = Math.Min(settings.AlignmentSpeedMps, Math.Min(lateral / 4, Math.Sqrt(acceleration * lateral / settings.BrakingMargin)));
                var desired = new LandingVector(-speed, 0, 0)
                    - new LandingVector(0, position.Y, position.Z).Unit * sideSpeed;
                double cap = position.X <= 3 || phase == DockingPhase.Docking
                    ? Math.Min(settings.MaximumSpeedMps, Math.Min(settings.NearSpeedMps, settings.ContactSpeedMps)) : settings.MaximumSpeedMps;
                double error = issue == DockingIssue.Lateral ? lateral : issue == DockingIssue.Pointing ? pointingError
                    : issue == DockingIssue.Orientation ? orientationError : issue == DockingIssue.AngularRate ? angularRate : 0;
                waypoint = issue == DockingIssue.Lateral ? new LandingVector(position.X, 0, 0) : !aligned ? position : default;
                return new DockingCommand(phase, desired.Limited(cap), issue, error, waypoint, entryCleared);
            }
            var offset = waypoint - position;
            double limit = Math.Min(Math.Min(speed, settings.MaximumSpeedMps), Math.Sqrt(acceleration * offset.Length / settings.BrakingMargin));
            return new DockingCommand(phase, offset.Unit * limit, issue, waypoint: waypoint);
        }

        private static double NativeWindow(double value, double maximum)
            => double.IsFinite(value) && value > 0 ? Math.Min(value, maximum) : 0;

        internal static double SegmentClearance(LandingVector start, LandingVector end)
        {
            var leg = end - start;
            double fraction = leg.LengthSquared > 1e-12
                ? Math.Clamp(-LandingVector.Dot(start, leg) / leg.LengthSquared, 0, 1) : 0;
            return (start + leg * fraction).Length;
        }

        internal static LandingVector ClearanceWaypoint(LandingVector position, LandingVector velocity, LandingVector gate,
            double clearance, out bool bypass)
        {
            // Circumscribed tangent legs stay outside the sphere, including the space
            // between checkpoints. Three metres cover small tracking/pulse excursions.
            double radius = clearance + 3;
            bypass = SegmentClearance(position, gate) < radius;
            if (!bypass) return gate;
            double distance = position.Length;
            if (distance < clearance + 0.5)
                return (distance > 0.001 ? position.Unit : new LandingVector(0, 1, 0)) * (radius + 2);
            double lateral = Math.Sqrt(position.Y * position.Y + position.Z * position.Z);
            var side = lateral > 0.001 ? new LandingVector(0, position.Y, position.Z) / lateral
                : new LandingVector(0, velocity.Y, velocity.Z).Length > 0.001
                    ? new LandingVector(0, velocity.Y, velocity.Z).Unit : new LandingVector(0, 1, 0);
            double angle = Math.Acos(Math.Clamp(position.X / distance, -1, 1));
            if (distance < radius + 5)
            {
                // Near the arc, gently restore radius while retaining tangential
                // momentum. Recomputing a tangent at each tiny radial excursion
                // would alternately pull inward and kick outward with coarse jets.
                var tangentDirection = new LandingVector(Math.Sin(angle), 0, 0) - side * Math.Cos(angle);
                return position.Unit * radius + tangentDirection * (radius * 0.6);
            }
            double tangent = angle - Math.Acos(Math.Clamp(radius / distance, 0, 1));
            double gateTangent = Math.Acos(Math.Clamp(radius / gate.X, 0, 1));
            double next = Math.Max(gateTangent, tangent - Math.PI / 6);
            double middle = (tangent + next) * 0.5;
            double waypointRadius = radius / Math.Cos((tangent - next) * 0.5);
            return new LandingVector(Math.Cos(middle), 0, 0) * waypointRadius + side * (Math.Sin(middle) * waypointRadius);
        }

        internal static LandingVector TravelVelocity(LandingVector offset, double acceleration, double speed, double margin)
            => offset.Unit * Math.Min(speed, Math.Min(offset.Length / 4, Math.Sqrt(acceleration * offset.Length / margin)));

        internal static LandingVector WaypointVelocity(LandingVector offset, double acceleration, double speed, double margin)
        {
            double Axis(double error) => Math.CopySign(Math.Min(speed,
                Math.Min(Math.Abs(error) / 4, Math.Sqrt(acceleration * Math.Abs(error) / margin))), error);
            return new LandingVector(Axis(offset.X), Axis(offset.Y), Axis(offset.Z)).Limited(speed);
        }

        public static int TranslationSign(double velocityError, double minimumDeltaV)
        {
            if (!double.IsFinite(velocityError) || !double.IsFinite(minimumDeltaV) || minimumDeltaV < 0) return 0;
            double deadband = Math.Max(0.005, minimumDeltaV * 0.6);
            return Math.Abs(velocityError) <= deadband ? 0 : Math.Sign(velocityError);
        }

        public static bool HasBrakingRoom(double distance, double speed, double acceleration, double clearance)
            => double.IsFinite(distance) && double.IsFinite(speed) && double.IsFinite(acceleration)
                && double.IsFinite(clearance) && acceleration > 0 && speed >= 0
                && distance > clearance + speed * speed / (2 * acceleration);
    }

    /// <summary>Retry only after sustained lack of correction progress, in simulation time.</summary>
    internal sealed class DockingRecoveryMonitor
    {
        private DockingIssue _issue;
        private double _bestError, _lastProgress = double.NaN;
        public double SecondsWithoutProgress(double now) => double.IsFinite(_lastProgress) ? Math.Max(0, now - _lastProgress) : 0;
        public void Reset() { _issue = DockingIssue.None; _lastProgress = double.NaN; }
        public bool Observe(double now, DockingIssue issue, double error, double patience)
        {
            if (issue != DockingIssue.Lateral && issue != DockingIssue.Pointing
                && issue != DockingIssue.Orientation && issue != DockingIssue.AngularRate)
            { Reset(); return false; }
            if (!double.IsFinite(now) || !double.IsFinite(error) || error < 0 || !double.IsFinite(patience)) return false;
            if (_issue != issue || !double.IsFinite(_lastProgress) || now < _lastProgress)
            { _issue = issue; _bestError = error; _lastProgress = now; return false; }
            double improvement = Math.Max(issue == DockingIssue.Lateral ? 0.02 : 0.05, _bestError * 0.05);
            if (error < _bestError - improvement) { _bestError = error; _lastProgress = now; }
            return now - _lastProgress >= Math.Max(30, patience);
        }
    }
}
