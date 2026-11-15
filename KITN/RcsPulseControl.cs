using System;

namespace KITN
{
    /// <summary>Use hardware-sized impulses, acknowledge them, then observe/coast before firing again.</summary>
    internal sealed class RcsPulseControl
    {
        private LandingVector _pending;
        private double _requestedAt = double.NaN, _acknowledgedAt = double.NegativeInfinity, _notBefore;
        private double _lastTime = double.NaN;
        private LandingVector _velocityDebt;
        public void Reset()
        { _pending = _velocityDebt = default; _lastTime = _requestedAt = double.NaN; _acknowledgedAt = double.NegativeInfinity; _notBefore = 0; }

        public LandingVector Update(double now, double nativePulseTime, double pulseDuration,
            LandingVector desired, LandingVector velocity, LandingVector positionError, LandingVector quantum,
            LandingVector velocityTrackingAxes = default)
        {
            if (!double.IsFinite(now) || !desired.IsFinite || !velocity.IsFinite || !positionError.IsFinite || !quantum.IsFinite)
                return default;
            double dt = double.IsFinite(_lastTime) ? Math.Clamp(now - _lastTime, 0, 0.5) : 0;
            _lastTime = now;
            double Debt(double previous, double target, double actual, double tracking, double bit)
                => tracking > 0 && Math.Abs(target) < bit * 0.6 && Math.Abs(target) > 1e-6
                    ? Math.Clamp(previous + (target - actual) * dt, -1, 1) : 0;
            _velocityDebt = new LandingVector(Debt(_velocityDebt.X, desired.X, velocity.X, velocityTrackingAxes.X, quantum.X),
                Debt(_velocityDebt.Y, desired.Y, velocity.Y, velocityTrackingAxes.Y, quantum.Y),
                Debt(_velocityDebt.Z, desired.Z, velocity.Z, velocityTrackingAxes.Z, quantum.Z));
            positionError = new LandingVector(velocityTrackingAxes.X > 0 ? _velocityDebt.X : positionError.X,
                velocityTrackingAxes.Y > 0 ? _velocityDebt.Y : positionError.Y,
                velocityTrackingAxes.Z > 0 ? _velocityDebt.Z : positionError.Z);
            double settling = Math.Max(0.2, double.IsFinite(pulseDuration) ? pulseDuration + 0.1 : 0.2);
            if (double.IsFinite(nativePulseTime) && nativePulseTime > _acknowledgedAt)
            {
                _acknowledgedAt = nativePulseTime;
                _notBefore = nativePulseTime + settling;
                _pending = default; _requestedAt = double.NaN;
            }
            if (now < _notBefore) return default;
            if (double.IsFinite(_requestedAt))
            {
                // Keep the request until KSA consumes it; the game need not poll on our frame.
                if (now - _requestedAt < Math.Max(0.5, settling * 2)) return _pending;
                // A missed acknowledgment must not leave stale flags held indefinitely.
                _pending = default; _requestedAt = double.NaN; _notBefore = now + settling;
                return default;
            }
            int Axis(double target, double actual, double offset, double bit, double tracking)
            {
                if (tracking <= 0) return Sign(target, actual, offset, bit);
                if (Math.Abs(target) >= bit * 0.6 || Math.Abs(target) < 1e-6)
                    return DockingMath.TranslationSign(target - actual, bit);
                // Speeds below one impulse use a bounded displacement budget. Keep a
                // useful coast leg between acceleration and braking instead of immediately
                // cancelling every pulse to reproduce an unattainable instantaneous speed.
                double budget = Math.Max(0.03, bit * 0.6);
                if (actual * target > 0 && Math.Abs(actual) > Math.Abs(target) * 1.5)
                    return offset * actual < 0 && Math.Abs(offset) > budget * 0.5
                        ? DockingMath.TranslationSign(target - actual, bit) : 0;
                if (actual * target < 0 && Math.Abs(actual) > bit * 0.4)
                    return DockingMath.TranslationSign(target - actual, bit);
                return offset * target > 0 && Math.Abs(offset) > budget ? Math.Sign(offset) : 0;
            }
            _pending = new LandingVector(Axis(desired.X, velocity.X, positionError.X, quantum.X, velocityTrackingAxes.X),
                Axis(desired.Y, velocity.Y, positionError.Y, quantum.Y, velocityTrackingAxes.Y),
                Axis(desired.Z, velocity.Z, positionError.Z, quantum.Z, velocityTrackingAxes.Z));
            if (_pending.LengthSquared > 0) _requestedAt = now;
            return _pending;
        }

        internal static int Sign(double desired, double velocity, double positionError, double quantum)
        {
            if (!double.IsFinite(desired) || !double.IsFinite(velocity) || !double.IsFinite(positionError)
                || !double.IsFinite(quantum) || quantum <= 0) return 0;
            if (Math.Abs(positionError) > Math.Max(1, quantum * 8) || Math.Abs(desired) > quantum * 1.2)
                return DockingMath.TranslationSign(desired - velocity, quantum);
            // Evaluate coasting and the two attainable velocities at a short horizon. A
            // correction inside the velocity deadband is worthwhile only if it prevents
            // positional drift; the immediately opposite pulse usually makes that worse.
            double horizon = Math.Clamp(0.06 / quantum, 0.6, 2);
            double speedLimit = Math.Max(quantum, Math.Abs(desired) + quantum * 0.6);
            double Cost(double candidate) => Math.Pow(positionError - candidate * horizon, 2)
                + Math.Pow((desired - candidate) * horizon, 2) * 0.5;
            double best = Cost(velocity), improvement = quantum * quantum * horizon * horizon * 0.12;
            int sign = 0;
            for (int direction = -1; direction <= 1; direction += 2)
            {
                double candidate = velocity + direction * quantum;
                if (Math.Abs(candidate) > speedLimit && Math.Abs(candidate) >= Math.Abs(velocity)) continue;
                double cost = Cost(candidate);
                if (cost < best - improvement) { best = cost; sign = direction; }
            }
            return sign;
        }
    }
}
