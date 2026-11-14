using System;

namespace KITN
{
    internal enum DockingPhase { Idle, Matching, Retreating, Aligning, Approaching, Docking, Docked, Matched, Stopped }

    internal readonly struct DockingCommand
    {
        public readonly DockingPhase Phase;
        public readonly LandingVector DesiredVelocity;
        public DockingCommand(DockingPhase phase, LandingVector velocity) { Phase = phase; DesiredVelocity = velocity; }
    }

    /// <summary>Port-relative approach on the outside of the target, with room to brake.</summary>
    internal static class DockingMath
    {
        public const double MaximumRange = 2000;
        public static DockingCommand Approach(LandingVector position, double pointingError, double angularRate,
            double acceleration, double standOff)
        {
            if (!position.IsFinite || !double.IsFinite(pointingError) || !double.IsFinite(angularRate)
                || !double.IsFinite(acceleration) || acceleration <= 0 || !double.IsFinite(standOff) || standOff <= 0)
                return new DockingCommand(DockingPhase.Aligning, default);
            double lateral = Math.Sqrt(position.Y * position.Y + position.Z * position.Z);
            bool aligned = pointingError < 2 && angularRate < 0.25;
            LandingVector waypoint;
            DockingPhase phase;
            double speed;
            if (position.X < standOff - 0.5 && (lateral > 0.3 || !aligned))
            {
                phase = DockingPhase.Retreating;
                waypoint = new LandingVector(standOff, position.Y, position.Z); speed = 0.5;
            }
            else if (lateral > 0.3)
            {
                phase = DockingPhase.Aligning;
                waypoint = new LandingVector(Math.Max(standOff, position.X), 0, 0); speed = 0.5;
            }
            else if (!aligned) return new DockingCommand(DockingPhase.Aligning, default);
            else
            {
                phase = position.X < 10 ? DockingPhase.Docking : DockingPhase.Approaching;
                // Cross the connector plane gently; the game decides whether real contact latches.
                waypoint = new LandingVector(-0.1, 0, 0);
                speed = position.X > 50 ? 1 : position.X > 10 ? 0.3 : 0.08;
            }
            var offset = waypoint - position;
            double limit = Math.Min(speed, Math.Sqrt(0.8 * acceleration * offset.Length));
            return new DockingCommand(phase, offset.Unit * limit);
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
}
