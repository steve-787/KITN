using System;

namespace KITN
{
    internal static class PhysicsWarpPolicy
    {
        public const double MaximumSpeed = 4;
        public const double MaximumStepSeconds = 0.1;

        public static double Limit(bool enabled, double playerStep, bool precise = false)
        {
            if (!enabled || !double.IsFinite(playerStep) || playerStep <= 0) return 1;
            double budget = precise ? 0.05 : MaximumStepSeconds;
            double limit = Math.Min(precise ? 2 : MaximumSpeed, budget / playerStep);
            return limit >= 4 - 1e-9 ? 4 : limit >= 2 - 1e-9 ? 2 : 1;
        }
    }
}
