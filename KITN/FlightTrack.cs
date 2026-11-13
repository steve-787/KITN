using System;

namespace KITN
{
    /// <summary>Surface distance from body-fixed positions, independent of drawing and logging.</summary>
    internal sealed class FlightTrack
    {
        private readonly double[] _distanceKm, _altitudeKm;
        private double _lastX, _lastY, _lastZ, _lastTime, _sampleTime, _startTime;
        private bool _havePosition;

        public int Count { get; private set; }
        public bool IsRecording { get; private set; }
        public double DistanceKm { get; private set; }
        public double ElapsedSeconds { get; private set; }
        public ReadOnlySpan<double> Distances => _distanceKm.AsSpan(0, Count);
        public ReadOnlySpan<double> Altitudes => _altitudeKm.AsSpan(0, Count);

        public FlightTrack(int capacity = 4096)
        {
            if (capacity < 4) throw new ArgumentOutOfRangeException(nameof(capacity));
            _distanceKm = new double[capacity];
            _altitudeKm = new double[capacity];
        }

        public void Clear()
        {
            Count = 0;
            DistanceKm = ElapsedSeconds = 0;
            _havePosition = IsRecording = false;
        }

        public void Begin(double time, double x, double y, double z, double altitudeM, double bodyRadiusM)
        {
            Clear();
            IsRecording = true;
            Sample(time, x, y, z, altitudeM, bodyRadiusM, true);
        }

        public void Stop() => IsRecording = false;

        public void Sample(double time, double x, double y, double z, double altitudeM, double bodyRadiusM,
            bool force = false)
        {
            if (!IsRecording || !double.IsFinite(time) || !double.IsFinite(altitudeM)
                || !double.IsFinite(bodyRadiusM) || bodyRadiusM <= 0) return;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (!double.IsFinite(length) || length < 1) return;
            x /= length; y /= length; z /= length;
            if (_havePosition && time < _lastTime)
            {
                // A reverted save starts a new trace instead of joining two unrelated flights.
                Clear();
                IsRecording = true;
            }
            if (!_havePosition)
            {
                _startTime = _sampleTime = time;
                _havePosition = true;
            }
            else
            {
                if (time == _lastTime && !force) return;
                double cx = y * _lastZ - z * _lastY;
                double cy = z * _lastX - x * _lastZ;
                double cz = x * _lastY - y * _lastX;
                double angle = Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz),
                    Math.Clamp(x * _lastX + y * _lastY + z * _lastZ, -1, 1));
                DistanceKm += angle * bodyRadiusM / 1000;
            }
            _lastX = x; _lastY = y; _lastZ = z; _lastTime = time;
            ElapsedSeconds = Math.Max(0, time - _startTime);
            if (Count == 0 || force || time - _sampleTime >= 0.25)
            {
                Append(DistanceKm, altitudeM / 1000);
                _sampleTime = time;
            }
        }

        private void Append(double distance, double altitude)
        {
            if (Count == _distanceKm.Length)
            {
                // Keep the launch and the whole flight rather than dropping the oldest samples.
                int retained = 1;
                for (int i = 2; i < Count; i += 2)
                {
                    _distanceKm[retained] = _distanceKm[i];
                    _altitudeKm[retained++] = _altitudeKm[i];
                }
                Count = retained;
            }
            _distanceKm[Count] = distance;
            _altitudeKm[Count++] = altitude;
        }
    }
}
