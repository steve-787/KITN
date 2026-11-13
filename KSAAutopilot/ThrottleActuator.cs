using System;

namespace KSAAutopilot
{
    /// <summary>Produces short minimum-throttle burns when a liquid engine cannot throttle lower.</summary>
    internal sealed class ThrottleActuator
    {
        private double _duty;
        private double _holdSeconds;
        public void Reset() { _duty = _holdSeconds = 0.0; }

        public double Command(double requested, double minimum, double simulationStep, bool minimumBurnSafe,
            double minimumPulseSeconds = 0.001)
        {
            if (!double.IsFinite(requested) || requested <= 0.001)
            {
                Reset();
                return 0.0;
            }
            minimum = double.IsFinite(minimum) ? Math.Clamp(minimum, 0.001, 1.0) : 0.1;
            if (requested >= minimum) { Reset(); return Math.Clamp(requested, 0.0, 1.0); }
            if (!minimumBurnSafe || !double.IsFinite(simulationStep) || simulationStep <= 0.0)
            { Reset(); return 0.0; }
            double step = Math.Min(0.1, simulationStep);
            double pulse = double.IsFinite(minimumPulseSeconds) ? Math.Max(step, minimumPulseSeconds) : step;
            pulse = Math.Ceiling(pulse / step - 1e-9) * step;
            _duty += requested / minimum * step;
            if (_holdSeconds <= 0.0)
            {
                if (_duty + 1e-12 < pulse) return 0.0;
                _holdSeconds = pulse;
            }
            _duty -= step;
            _holdSeconds = Math.Max(0.0, _holdSeconds - step);
            if (_holdSeconds < 1e-10) _holdSeconds = 0.0;
            return minimum;
        }
    }
}
