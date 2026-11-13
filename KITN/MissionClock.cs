using System;
using System.Globalization;

namespace KITN
{
    /// <summary>Countdown and mission elapsed time, driven by simulation time at the physics hook.</summary>
    internal sealed class MissionClock
    {
        public enum ClockMode { Ready, Countdown, Flight, Cancelled, Stopped }
        public ClockMode Mode { get; private set; }
        public bool IsCountingDown => Mode == ClockMode.Countdown;
        public bool HasLaunched { get; private set; }
        public double Seconds { get; private set; }
        public string Display => Format(HasLaunched, Seconds);
        private double _epoch, _lastTime, _duration;
        private bool _haveTime, _launchIssued;

        public void BeginCountdown(int durationSeconds, double simulationTime)
        {
            if (durationSeconds < 1 || durationSeconds > 3600) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            if (!double.IsFinite(simulationTime)) throw new ArgumentOutOfRangeException(nameof(simulationTime));
            _epoch = _lastTime = simulationTime;
            _duration = Seconds = durationSeconds;
            _haveTime = true;
            _launchIssued = HasLaunched = false;
            Mode = ClockMode.Countdown;
        }

        /// <returns>True once when the countdown reaches zero; the caller starts guidance at the synchronized hook.</returns>
        public bool Advance(double simulationTime)
        {
            if (!double.IsFinite(simulationTime)) return false;
            if (_haveTime && simulationTime < _lastTime)
            {
                Reset();
                return false;
            }
            _lastTime = simulationTime;
            _haveTime = true;
            if (Mode == ClockMode.Countdown)
            {
                Seconds = Math.Max(0, _duration - (simulationTime - _epoch));
                if (Seconds == 0 && !_launchIssued)
                {
                    _launchIssued = true;
                    return true;
                }
            }
            else if (Mode == ClockMode.Flight) Seconds = Math.Max(0, simulationTime - _epoch);
            return false;
        }

        public void MarkLaunched(double simulationTime)
        {
            if (!double.IsFinite(simulationTime)) throw new ArgumentOutOfRangeException(nameof(simulationTime));
            _epoch = _lastTime = simulationTime;
            _haveTime = HasLaunched = true;
            Seconds = 0;
            Mode = ClockMode.Flight;
        }

        public void CancelCountdown()
        {
            if (IsCountingDown) Mode = ClockMode.Cancelled;
        }

        public void OnVehicleChanged()
        {
            // A launched mission outlives guidance and switching to its separated booster.
            if (!HasLaunched) Reset();
        }

        public void Stop()
        {
            if (Mode == ClockMode.Flight) Mode = ClockMode.Stopped;
            else CancelCountdown();
        }

        public void Reset()
        {
            Mode = ClockMode.Ready;
            Seconds = 0;
            _haveTime = _launchIssued = HasLaunched = false;
        }

        public static string Format(bool launched, double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
            long whole = (long)Math.Min(1e12, launched ? Math.Floor(seconds) : Math.Ceiling(seconds));
            return string.Format(CultureInfo.InvariantCulture, "T{0}{1:00}:{2:00}:{3:00}",
                launched ? "+" : "-", whole / 3600, whole / 60 % 60, whole % 60);
        }
    }
}
