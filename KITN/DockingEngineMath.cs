using System;

namespace KITN
{
    internal enum DockingEngineReason { Waiting, Layout, Fuel, TooStrong, Braking, Final, Coasting, Push }

    internal readonly struct DockingEngineData
    {
        public readonly LandingVector Acceleration;
        public readonly double AngularAcceleration, Throttle, MinimumPulse;
        public DockingEngineData(LandingVector acceleration, double angularAcceleration, double throttle, double minimumPulse)
        { Acceleration = acceleration; AngularAcceleration = angularAcceleration; Throttle = throttle; MinimumPulse = minimumPulse; }
    }

    /// <summary>Actual nozzle geometry relative to centre of mass and the selected own port.</summary>
    internal struct DockingEngineGeometry
    {
        private readonly LandingVector _port, _forward;
        public LandingVector Force, Torque;
        public int Count;
        public bool Valid;
        public DockingEngineGeometry(LandingVector port, LandingVector forward)
        { _port = port; _forward = forward.Unit; Force = Torque = default; Count = 0; Valid = port.IsFinite && forward.IsFinite && forward.Length > 0.9; }
        public void Add(LandingVector location, LandingVector direction, double thrust)
        {
            // Every active nozzle must be behind both the CoM and the port, pushing toward
            // the port's outward direction. An unknown or sideways engine rejects the bank.
            if (!location.IsFinite || !direction.IsFinite || !double.IsFinite(thrust) || thrust <= 0
                || LandingVector.Dot(location, _forward) >= -0.25
                || LandingVector.Dot(location - _port, _forward) >= -0.25
                || LandingVector.Dot(direction.Unit, _forward) < Math.Cos(Math.PI / 180))
            { Valid = false; return; }
            var force = direction.Unit * thrust;
            Force += force; Torque += LandingVector.Cross(location, force); Count++;
        }
    }

    internal static class DockingEngineMath
    {
        public static bool Plan(DockingCommand command, LandingVector position, LandingVector velocity,
            double orientation, double rate, double rcsAcceleration, double safeRadius, double step,
            DockingSettings settings, DockingEngineData engine, out double worstDeltaV, out DockingEngineReason reason)
        {
            worstDeltaV = 0; reason = DockingEngineReason.Waiting;
            if (settings?.EngineAssist != true || !command.EntryCleared || command.Issue != DockingIssue.None
                || command.Phase != DockingPhase.Approaching || !position.IsFinite || !velocity.IsFinite
                || !double.IsFinite(orientation) || !double.IsFinite(rate)
                || orientation > Math.Min(1, settings.AlignmentToleranceDeg * 0.5) || rate > 0.1) return false;
            if (position.X <= Math.Max(30, safeRadius + 5)) { reason = DockingEngineReason.Final; return false; }
            if (!engine.Acceleration.IsFinite || engine.Acceleration.X >= 0 || engine.Acceleration.Length <= 0
                || !double.IsFinite(engine.Throttle) || engine.Throttle < 0.005 || engine.Throttle > 1
                || !double.IsFinite(engine.MinimumPulse) || engine.MinimumPulse < 0
                || !double.IsFinite(engine.AngularAcceleration) || engine.AngularAcceleration < 0)
            { reason = DockingEngineReason.Fuel; return false; }
            // One guidance-frame command, with hardware minimum duration and two extra
            // frames reserved for cutoff latency. Reject long/unknown simulation steps.
            double duration = Math.Max(engine.MinimumPulse, step) + 2 * step;
            if (!double.IsFinite(step) || step <= 0 || step > 0.1 || duration > 0.5)
            { reason = DockingEngineReason.TooStrong; return false; }
            var impulse = engine.Acceleration * (duration * 1.1);
            worstDeltaV = impulse.Length;
            double closing = -velocity.X, desired = -command.DesiredVelocity.X;
            if (closing < -0.01 || desired - closing < 0.02 || worstDeltaV > Math.Min(0.15, desired - closing)
                || engine.AngularAcceleration * duration * 180 / Math.PI > 0.05)
            { reason = DockingEngineReason.TooStrong; return false; }
            double after = closing - impulse.X;
            if (!double.IsFinite(rcsAcceleration) || rcsAcceleration <= 0)
            { reason = DockingEngineReason.Braking; return false; }
            double stopTime = after / rcsAcceleration + duration + 1;
            double stopping = after * after * settings.BrakingMargin / (2 * rcsAcceleration) + after * (duration + 1);
            double lateral = Math.Sqrt(position.Y * position.Y + position.Z * position.Z);
            double lateralSpeed = Math.Sqrt(velocity.Y * velocity.Y + velocity.Z * velocity.Z);
            double sideImpulse = Math.Sqrt(impulse.Y * impulse.Y + impulse.Z * impulse.Z);
            if (position.X - Math.Max(10, safeRadius + 2) <= stopping + 5
                || lateral + (lateralSpeed + sideImpulse) * stopTime >= settings.CorridorRadiusM * 0.8)
            { reason = DockingEngineReason.Braking; return false; }
            reason = DockingEngineReason.Push; return true;
        }
    }
}
