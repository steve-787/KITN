using System;

namespace KITN
{
    // Elliptic and hyperbolic coast in a fixed inertial frame; no live game objects.
    internal sealed class TransferCoast
    {
        private readonly TransferOrbit _ellipse;
        private readonly LandingVector _p, _q;
        private readonly double _a, _e, _mean, _motion;
        public readonly bool Valid;
        public readonly double Periapsis, Energy, NextPeriapsis;

        public TransferCoast(double mu, LandingVector r, LandingVector v)
        {
            if (!(mu > 0 && r.Length > 0) || !r.IsFinite || !v.IsFinite) return;
            var h = LandingVector.Cross(r, v);
            var ev = LandingVector.Cross(v, h) / mu - r.Unit;
            _e = ev.Length; Energy = v.LengthSquared / 2 - mu / r.Length;
            Periapsis = h.LengthSquared / mu / (1 + _e);
            _ellipse = new TransferOrbit(mu, 0, r, v);
            if (_ellipse.Valid)
            {
                Valid = true; NextPeriapsis = _ellipse.TimeToPeriapsis(0); return;
            }
            if (!(_e > 1 && Energy > 0 && h.Length > 0)) return;
            _a = -mu / (2 * Energy); _p = ev.Unit; _q = LandingVector.Cross(h.Unit, _p).Unit;
            double anomaly = Math.Atan2(LandingVector.Dot(r, _q), LandingVector.Dot(r, _p));
            double ha = Math.Asinh(Math.Sqrt(_e * _e - 1) * Math.Sin(anomaly) / (1 + _e * Math.Cos(anomaly)));
            _mean = _e * Math.Sinh(ha) - ha;
            _motion = Math.Sqrt(mu / (-_a * _a * _a));
            NextPeriapsis = -_mean / _motion;
            Valid = double.IsFinite(_mean) && double.IsFinite(_motion);
        }

        public bool State(double seconds, out LandingVector r, out LandingVector v)
        {
            r = v = default;
            if (!Valid || !double.IsFinite(seconds)) return false;
            if (_ellipse.Valid) return _ellipse.State(seconds, out r, out v);
            double mean = _mean + seconds * _motion, ha = Math.Asinh(mean / _e);
            for (int pass = 0; pass < 32; pass++)
            {
                double step = (_e * Math.Sinh(ha) - ha - mean) / (_e * Math.Cosh(ha) - 1);
                ha -= step;
                if (Math.Abs(step) < 1e-12) break;
            }
            double c = Math.Cosh(ha), s = Math.Sinh(ha), b = Math.Sqrt(_e * _e - 1);
            r = (_p * (_e - c) + _q * (b * s)) * -_a;
            v = (_p * -s + _q * (b * c)) * (-_a * _motion / (_e * c - 1));
            return r.IsFinite && v.IsFinite;
        }

        public double OutgoingRadiusTime(double radius)
        {
            if (!Valid || !(_e > 1 && radius > Periapsis)) return double.NaN;
            double ha = Math.Acosh((radius / -_a + 1) / _e);
            return NextPeriapsis + (_e * Math.Sinh(ha) - ha) / _motion;
        }
    }
}
