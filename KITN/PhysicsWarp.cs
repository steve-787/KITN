using System;

namespace KITN
{
    /// <summary>Preserves modest manual warp and reduces it when control or physics cannot keep up.</summary>
    internal static class PhysicsWarp
    {
        private static bool _enabled;
        private static double _playerStep, _previousSpeed = double.NaN;
        private static bool _slowPhysics;

        public static void Configure(bool enabled, double playerStep)
        {
            _enabled = enabled; _playerStep = playerStep;
            double speed = KSA.Universe.SimulationSpeed;
            double achievedStep = KSA.Universe.GetLastSimStep().DeltaTime;
            _slowPhysics = speed > 1 && speed <= PhysicsWarpPolicy.MaximumSpeed
                && Math.Abs(_previousSpeed - speed) < 0.01 && playerStep > 0
                && achievedStep > 0 && achievedStep / (playerStep * speed) < 0.75;
            _previousSpeed = speed;
        }

        public static void LimitForControl(KSA.Vehicle vessel, bool precise = false)
        {
            if (KSA.Universe.IsAutoWarpActive) KSA.Universe.AutoWarpStop(false);
            double cap = PhysicsWarpPolicy.Limit(_enabled && !_slowPhysics && vessel?.HasPhysicsBubble == true, _playerStep, precise);
            if (KSA.Universe.SimulationSpeed > cap) KSA.Universe.SetSimulationSpeed(cap, false);
        }
    }
}
