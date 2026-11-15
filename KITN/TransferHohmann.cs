/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: MIT
 * Adapted from MechJebLib/Functions/Astro.cs, HohmannTransferParameters.
 * Changes: scalar radii, input validation, phase seeds and bounded KSA transfer searches.
 */
using System;
using System.Threading;

namespace KITN
{
    internal static class TransferHohmann
    {
        // Signed circular-orbit impulses, half-ellipse flight time and departure phase.
        public static (double DepartureDv, double ArrivalDv, double Duration, double Phase) Parameters(double mu, double r1, double r2)
        {
            if (!double.IsFinite(mu) || mu <= 0 || !double.IsFinite(r1) || r1 <= 0 || !double.IsFinite(r2) || r2 <= 0)
                throw new ArgumentException("Hohmann transfers require positive finite gravity and orbit radii.");
            double sum = r1 + r2;
            double dv1 = Math.Sqrt(mu / r1) * (Math.Sqrt(2 * r2 / sum) - 1);
            double dv2 = Math.Sqrt(mu / r2) * (1 - Math.Sqrt(2 * r1 / sum));
            double duration = Math.PI * Math.Sqrt(sum * sum * sum / (8 * mu));
            double ratio = r1 / r2 + 1;
            double phase = Math.PI * (1 - 0.35355339059327373 * Math.Sqrt(ratio * ratio * ratio));
            if (!double.IsFinite(dv1) || !double.IsFinite(dv2) || !double.IsFinite(duration) || !double.IsFinite(phase))
                throw new ArgumentException("Hohmann transfer parameters overflowed.");
            return (dv1, dv2, duration, phase);
        }

        public static bool Configure(TransferRequest request, HohmannSettings settings)
        {
            if (request == null || settings?.IsValid != true || request.Parking?.Valid != true || request.Target?.Valid != true)
                return false;
            var source = request.Source ?? request.Parking;
            if (!source.Valid || !source.State(request.Now, out var r1, out var v1)
                || !request.Target.State(request.Now, out var r2, out var v2)) return false;
            // A classical Hohmann window requires orbits travelling in the same direction.
            if (LandingVector.Dot(LandingVector.Cross(r1, v1), LandingVector.Cross(r2, v2)) <= 0) return false;
            double rate = Math.Abs(1 / source.Period - 1 / request.Target.Period);
            if (rate <= Math.Max(1 / source.Period, 1 / request.Target.Period) * 1e-8) return false;
            var nominal = Parameters(request.CentralMu, source.A, request.Target.A);
            double tolerance = settings.DurationTolerancePercent / 100.0;
            request.Earliest = request.Now + settings.DepartureLeadSeconds;
            request.Latest = request.Earliest + settings.SynodicWindows / rate;
            request.MinimumDuration = nominal.Duration * (1 - tolerance);
            request.MaximumDuration = nominal.Duration * (1 + tolerance);
            request.IncludeCapture = settings.IncludeCapture;
            request.Hohmann = true;
            request.Oberth = request.GravityAssists = request.UseBestEffortFallback = false;
            request.Width = TransferMath.DepartureSamples(request);
            // An odd sample count includes the nominal Hohmann duration exactly.
            request.Height = 49;
            return request.Valid;
        }

        public static double NextDeparture(TransferRequest request, double earliest)
        {
            var source = request.Source ?? request.Parking;
            if (!source.State(earliest, out var r1, out var v1) || !request.Target.State(earliest, out var r2, out _)) return double.NaN;
            var normal = LandingVector.Cross(r1, v1).Unit;
            double phase = Math.Atan2(LandingVector.Dot(normal, LandingVector.Cross(r1.Unit, r2.Unit)), LandingVector.Dot(r1.Unit, r2.Unit));
            double desired = Parameters(request.CentralMu, source.A, request.Target.A).Phase;
            double relativeMotion = Math.Tau * (1 / request.Target.Period - 1 / source.Period);
            if (relativeMotion == 0) return double.NaN;
            double advance = (desired - phase) * Math.Sign(relativeMotion);
            advance = (advance % Math.Tau + Math.Tau) % Math.Tau;
            // Avoid sending an already aligned departure to the following window due to roundoff.
            if (Math.Tau - advance < 1e-10) advance = 0;
            return earliest + advance / Math.Abs(relativeMotion);
        }

        public static void AddSeeds(TransferGrid grid, CancellationToken cancellation)
        {
            var request = grid.Request;
            double duration = Parameters(request.CentralMu, (request.Source ?? request.Parking).A, request.Target.A).Duration;
            double synodic = 1 / Math.Abs(1 / (request.Source ?? request.Parking).Period - 1 / request.Target.Period);
            double departure = NextDeparture(request, request.Earliest);
            for (int window = 0; window < 4 && double.IsFinite(departure) && departure <= request.Latest; window++, departure += synodic)
            {
                cancellation.ThrowIfCancellationRequested();
                // Ejection and finite burn corrections remain the existing MechJeb-derived solver's responsibility.
                var candidate = TransferMath.Evaluate(request, departure, duration, false, cancellation);
                if (candidate.Valid) grid.Candidates.Add(candidate);
            }
        }
    }
}
