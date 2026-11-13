using System;

namespace KITN
{
    /// <summary>Keep TVC available until the nose is aligned AND its motion is damped.</summary>
    internal sealed class InsertionAttitudeGate
    {
        public bool NeedsThrust { get; private set; }
        public string Reason { get; private set; } = "settled";
        public void Reset() { NeedsThrust = false; Reason = "settled"; }

        public bool Update(double pointingError, double transverseRateDegS, bool hasRcs, bool steeringWindow,
            double settleRateDegS = 0.15)
        {
            if (hasRcs || !steeringWindow) { Reset(); return false; }
            if (!double.IsFinite(pointingError) || !double.IsFinite(transverseRateDegS))
            { Reason = "unavailable_attitude"; return false; }
            if (pointingError > 3.0 || transverseRateDegS > settleRateDegS * 2.0) NeedsThrust = true;
            if (pointingError < 1.0 && transverseRateDegS < settleRateDegS) NeedsThrust = false;
            Reason = NeedsThrust ? transverseRateDegS >= settleRateDegS ? "braking_rotation" : "aligning_nose" : "settled";
            return NeedsThrust;
        }
    }
}
