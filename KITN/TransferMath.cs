/*
 * Ejection calculation adapted from MechJebLib.
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors.
 * Used under the MIT alternative; see THIRD_PARTY_NOTICES.md.
 */
using System;
using System.Collections.Generic;
using System.Threading;

namespace KITN
{
    /// <summary>Immutable elliptical ephemeris. All worker calculations use SI units and inertial vectors.</summary>
    internal sealed class TransferOrbit
    {
        public readonly double Mu, Epoch, A, Eccentricity, Period, Periapsis, Apoapsis;
        private readonly LandingVector _p, _q;
        private readonly double _meanAtEpoch, _motion;
        public readonly bool Valid;

        public TransferOrbit(double mu, double epoch, LandingVector position, LandingVector velocity)
        {
            Mu = mu; Epoch = epoch;
            double r = position.Length;
            if (!(mu > 0 && r > 0) || !position.IsFinite || !velocity.IsFinite) return;
            var h = LandingVector.Cross(position, velocity);
            var eccentric = LandingVector.Cross(velocity, h) / mu - position / r;
            A = 1 / (2 / r - velocity.LengthSquared / mu);
            Eccentricity = eccentric.Length;
            if (!(A > 0 && Eccentricity < 1 && h.Length > 0) || !double.IsFinite(A)) return;
            _p = Eccentricity > 1e-9 ? eccentric.Unit : position.Unit;
            _q = LandingVector.Cross(h.Unit, _p).Unit;
            double anomaly = Math.Atan2(LandingVector.Dot(position, _q), LandingVector.Dot(position, _p));
            double eccentricAnomaly = Math.Atan2(Math.Sqrt(1 - Eccentricity * Eccentricity) * Math.Sin(anomaly), Eccentricity + Math.Cos(anomaly));
            _meanAtEpoch = eccentricAnomaly - Eccentricity * Math.Sin(eccentricAnomaly);
            _motion = Math.Sqrt(mu / (A * A * A));
            Period = 2 * Math.PI / _motion;
            Periapsis = A * (1 - Eccentricity);
            Apoapsis = A * (1 + Eccentricity);
            Valid = double.IsFinite(Period) && double.IsFinite(epoch);
        }

        public bool State(double time, out LandingVector position, out LandingVector velocity)
        {
            position = velocity = default;
            if (!Valid || !double.IsFinite(time)) return false;
            double mean = Math.IEEERemainder(_meanAtEpoch + (time - Epoch) * _motion, 2 * Math.PI);
            double ea = Eccentricity < 0.8 ? mean : Math.CopySign(Math.PI, mean);
            for (int i = 0; i < 32; i++)
            {
                double correction = (ea - Eccentricity * Math.Sin(ea) - mean) / (1 - Eccentricity * Math.Cos(ea));
                ea -= correction;
                if (Math.Abs(correction) < 1e-12) break;
            }
            if (Math.Abs(ea - Eccentricity * Math.Sin(ea) - mean) > 1e-8) return false;
            double c = Math.Cos(ea), s = Math.Sin(ea), b = Math.Sqrt(1 - Eccentricity * Eccentricity);
            position = (_p * (c - Eccentricity) + _q * (b * s)) * A;
            velocity = (_p * -s + _q * (b * c)) * (A * _motion / (1 - Eccentricity * c));
            return position.IsFinite && velocity.IsFinite;
        }
        public double TimeToPeriapsis(double time)
        {
            double mean = (_meanAtEpoch + (time - Epoch) * _motion) % (2 * Math.PI);
            if (mean < 0) mean += 2 * Math.PI;
            return (2 * Math.PI - mean) / _motion;
        }
    }

    internal readonly struct TransferEjection
    {
        public readonly LandingVector Position, Before, After;
        public readonly double Coast;
        public bool Valid => Position.IsFinite && Before.IsFinite && After.IsFinite && Position.Length > 0 && double.IsFinite(Coast);
        public TransferEjection(LandingVector position, LandingVector before, LandingVector after, double coast)
        { Position = position; Before = before; After = after; Coast = coast; }
    }

    internal sealed class TransferRequest
    {
        public TransferOrbit Parking, Source, Target;
        public double Now, Earliest, Latest, MinimumDuration, MaximumDuration, CentralMu;
        public double TargetMu, TargetRadius, TargetSoi, PeriapsisAltitude, DepartureSafetyRadius, DepartureSoi, CentralSoi;
        public double MinimumTransferApoapsis, MinimumTransferPeriapsis;
        public double CentralSafetyRadius;
        public bool GravityAssists;
        public bool Oberth;
        public TransferBodySnapshot[] AssistBodies = Array.Empty<TransferBodySnapshot>();
        public Func<double, LandingVector, CancellationToken, TransferBurnState> PredictBurn;
        public bool IncludeCapture, UseBestEffortFallback;
        public Func<TransferSolution, CancellationToken, TransferSolution> Refine;
        public Dictionary<string, int> Rejections { get; private set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public TransferRequest CopyForPorkchop()
        {
            // Ephemerides and arrays are immutable snapshots. Rebind finite-burn callbacks to this copy before searching.
            var copy = (TransferRequest)MemberwiseClone();
            copy.Oberth = false;
            copy.PredictBurn = null; copy.Refine = null;
            copy.Rejections = new Dictionary<string, int>(StringComparer.Ordinal);
            return copy;
        }
        public TransferSolution Reject(string reason)
        {
            Rejections.TryGetValue(reason, out int count); Rejections[reason] = count + 1; return default;
        }
        public TransferWindowExclusion[] ExcludedWindows = Array.Empty<TransferWindowExclusion>();
        public int Width = 96, Height = 96;
        public bool Ejection => Source != null;
        public bool Valid => Parking?.Valid == true && Target?.Valid == true && (!Ejection || Source.Valid)
            && CentralMu > 0 && double.IsFinite(CentralMu) && double.IsFinite(Now)
            && double.IsFinite(Earliest) && Earliest >= Now && double.IsFinite(Latest) && Latest > Earliest
            && double.IsFinite(MinimumDuration) && MinimumDuration > 0 && double.IsFinite(MaximumDuration) && MaximumDuration > MinimumDuration
            && double.IsFinite(PeriapsisAltitude) && PeriapsisAltitude >= 0 && TargetMu > 0 && double.IsFinite(TargetMu)
            && TargetRadius > 0 && double.IsFinite(TargetRadius) && double.IsFinite(DepartureSafetyRadius)
            && double.IsFinite(TargetSoi) && TargetSoi >= 0
            && double.IsFinite(DepartureSoi) && DepartureSoi >= 0
            && !double.IsNaN(CentralSoi) && CentralSoi >= 0
            && double.IsFinite(MinimumTransferApoapsis) && MinimumTransferApoapsis >= 0
            && double.IsFinite(MinimumTransferPeriapsis) && MinimumTransferPeriapsis >= 0
            && Width >= 2 && Height >= 2 && Width <= 2048 && Height <= 200;
    }

    internal readonly struct TransferWindowExclusion
    {
        public readonly double Departure, Duration, DepartureMargin, DurationMargin;
        public readonly bool AllowBestEffort;
        public TransferWindowExclusion(double departure, double duration, double departureMargin, double durationMargin)
            : this(departure, duration, departureMargin, durationMargin, false) { }
        public TransferWindowExclusion(double departure, double duration, double departureMargin, double durationMargin, bool allowBestEffort)
        { Departure = departure; Duration = duration; DepartureMargin = departureMargin; DurationMargin = durationMargin; AllowBestEffort = allowBestEffort; }
        public bool Contains(double departure, double duration)
            => Math.Abs(departure - Departure) <= DepartureMargin && Math.Abs(duration - Duration) <= DurationMargin;
    }

    internal readonly struct TransferSolution
    {
        public readonly double Departure, Duration, DepartureDv, CaptureDv;
        public readonly LandingVector Position, DeltaV, ArrivalExcess;
        public readonly bool Valid, EscapesCentral, BestEffort;
        public readonly TransferFlyby Flyby;
        public TransferSolution(double departure, double duration, LandingVector position, LandingVector deltaV, LandingVector arrivalExcess, double capture, bool escapesCentral = false)
            : this(departure, duration, position, deltaV, arrivalExcess, capture, escapesCentral, false) { }
        public TransferSolution(double departure, double duration, LandingVector position, LandingVector deltaV, LandingVector arrivalExcess, double capture, bool escapesCentral, bool bestEffort)
            : this(departure, duration, position, deltaV, arrivalExcess, capture, escapesCentral, bestEffort, null) { }
        public TransferSolution(double departure, double duration, LandingVector position, LandingVector deltaV, LandingVector arrivalExcess, double capture, bool escapesCentral, bool bestEffort, TransferFlyby flyby)
        {
            Departure = departure; Duration = duration; Position = position; DeltaV = deltaV; ArrivalExcess = arrivalExcess;
            DepartureDv = deltaV.Length; CaptureDv = capture;
            EscapesCentral = escapesCentral;
            BestEffort = bestEffort;
            Flyby = flyby;
            Valid = double.IsFinite(departure) && double.IsFinite(duration) && duration > 0 && position.IsFinite
                && deltaV.IsFinite && arrivalExcess.IsFinite && double.IsFinite(capture) && capture >= 0 && position.Length > 0;
        }
        public double Cost(bool capture) => Valid ? DepartureDv + (capture ? CaptureDv : 0) : double.PositiveInfinity;
    }

    internal sealed class TransferGrid
    {
        public readonly TransferRequest Request;
        public readonly TransferSolution[] Cells;
        public readonly List<TransferSolution> Candidates = new List<TransferSolution>();
        public bool BestEffortPass;
        public TransferGrid PorkchopGrid;
        public TransferGrid(TransferRequest request) { Request = request; Cells = new TransferSolution[request.Width * request.Height]; }
        public bool Available(int index, double notBefore, IReadOnlyList<TransferWindowExclusion> excluded = null)
        {
            if (index < 0 || index >= Cells.Length || !Cells[index].Valid || Cells[index].Departure < notBefore) return false;
            if (excluded != null)
                foreach (var window in excluded)
                    if (window.Contains(Cells[index].Departure, Cells[index].Duration) && !(Cells[index].BestEffort && window.AllowBestEffort)) return false;
            return true;
        }

        public int Best(bool capture, double notBefore = double.NegativeInfinity, IReadOnlyList<TransferWindowExclusion> excluded = null)
        {
            int best = -1; double cost = double.PositiveInfinity;
            for (int i = 0; i < Cells.Length; i++)
                if (Available(i, notBefore, excluded)
                    && (best < 0 || Cells[i].Cost(capture) < cost))
                { best = i; cost = Cells[i].Cost(capture); }
            return best;
        }

        public TransferSolution Pick(bool capture, bool earliest, double maximumDv, double notBefore,
            IReadOnlyList<TransferWindowExclusion> excluded = null)
        {
            var best = default(TransferSolution);
            void Consider(TransferSolution candidate)
            {
                if (!candidate.Valid || candidate.Departure < notBefore || (earliest && candidate.Cost(capture) > maximumDv)) return;
                if (excluded != null)
                    foreach (var window in excluded)
                        if (window.Contains(candidate.Departure, candidate.Duration) && !(candidate.BestEffort && window.AllowBestEffort)) return;
                if (!best.Valid || (!earliest && candidate.Cost(capture) < best.Cost(capture))
                    || (earliest && ((best.EscapesCentral && !candidate.EscapesCentral)
                        || (best.EscapesCentral == candidate.EscapesCentral && (candidate.Departure < best.Departure - 1e-6
                            || (Math.Abs(candidate.Departure - best.Departure) <= 1e-6 && candidate.Cost(capture) < best.Cost(capture))))))) best = candidate;
            }
            foreach (var candidate in Cells) Consider(candidate);
            foreach (var candidate in Candidates) Consider(candidate);
            return best;
        }
        public int Earliest(bool capture, double maximumDv, double notBefore = double.NegativeInfinity, IReadOnlyList<TransferWindowExclusion> excluded = null)
        {
            if (!double.IsFinite(maximumDv) || maximumDv <= 0) return -1;
            int best = -1;
            for (int i = 0; i < Cells.Length; i++)
            {
                var cell = Cells[i];
                if (!Available(i, notBefore, excluded) || cell.Cost(capture) > maximumDv) continue;
                if (best < 0 || (Cells[best].EscapesCentral && !cell.EscapesCentral)
                    || (Cells[best].EscapesCentral == cell.EscapesCentral
                        && (cell.Departure < Cells[best].Departure - 1e-6
                            || (Math.Abs(cell.Departure - Cells[best].Departure) <= 1e-6 && cell.Cost(capture) < Cells[best].Cost(capture))))) best = i;
            }
            return best;
        }

    }

    internal static class TransferMath
    {
        public static TransferGrid SearchWithPorkchop(TransferRequest request, TransferRequest overview, CancellationToken cancellation, Action<int> progress = null)
        {
            if (overview == null) return Search(request, cancellation, progress);
            var result = Search(request, cancellation, value => progress?.Invoke(value / 2));
            cancellation.ThrowIfCancellationRequested();
            result.PorkchopGrid = Search(overview, cancellation, value => progress?.Invoke(50 + value / 2));
            return result;
        }
        public static double DefaultDepartureWindow(TransferRequest request)
        {
            if (!request.Ejection) return request.Parking.Period * 2;
            var source = request.Source;
            double axis = (source.A + request.Target.A) / 2;
            double hohmann = Math.PI * Math.Sqrt(axis * axis * axis / request.CentralMu);
            double difference = Math.Abs(1 / source.Period - 1 / request.Target.Period);
            double synodic = difference > 1e-12 ? 1 / difference : Math.Max(source.Period, request.Target.Period);
            return Math.Max(hohmann, Math.Min(synodic * 1.25, Math.Max(source.Period, request.Target.Period) * 3));
        }

        public static int DepartureSamples(TransferRequest request)
        {
            if (request.Ejection) return 96;
            // Resolve the parking orbit's phase even when the departure range spans many revolutions.
            double samples = (request.Latest - request.Earliest) / request.Parking.Period * 24 + 1;
            return (int)Math.Clamp(Math.Ceiling(samples), 96, 2048);
        }

        public static TransferGrid Search(TransferRequest request, CancellationToken cancellation, Action<int> progress = null)
        {
            if (!request.Valid) throw new ArgumentException("Invalid transfer search bounds or orbital state.");
            request.Rejections.Clear();
            var grid = SearchPass(request, cancellation, false, value => progress?.Invoke(value * 40 / 100));
            TransferGravityAssist.Search(grid, cancellation, value => progress?.Invoke(40 + value * 25 / 100));
            TransferMinimumSearch.Refine(grid, cancellation, value => progress?.Invoke(65 + value * 15 / 100));
            cancellation.ThrowIfCancellationRequested();
            if (request.UseBestEffortFallback && !grid.Pick(request.IncludeCapture, false, double.PositiveInfinity, request.Now).Valid)
            {
                grid = SearchPass(request, cancellation, true, value => progress?.Invoke(80 + value * 10 / 100));
                TransferGravityAssist.Search(grid, cancellation, value => progress?.Invoke(90 + value * 5 / 100));
                TransferMinimumSearch.Refine(grid, cancellation, value => progress?.Invoke(95 + value * 5 / 100));
            }
            progress?.Invoke(100);
            return grid;
        }

        private static TransferGrid SearchPass(TransferRequest request, CancellationToken cancellation, bool bestEffort, Action<int> progress)
        {
            var grid = new TransferGrid(request) { BestEffortPass = bestEffort };
            for (int x = 0; x < request.Width; x++)
            {
                cancellation.ThrowIfCancellationRequested();
                double departure = request.Earliest + (request.Latest - request.Earliest) * x / (request.Width - 1);
                for (int y = 0; y < request.Height; y++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double duration = request.MinimumDuration + (request.MaximumDuration - request.MinimumDuration) * y / (request.Height - 1);
                    if (request.Oberth)
                    {
                        grid.Cells[y * request.Width + x] = Evaluate(request, departure, duration, bestEffort, cancellation);
                        continue;
                    }
                    var solution = SolveCandidate(request, departure, duration, bestEffort);
                    grid.Cells[y * request.Width + x] = solution.Valid && request.Refine != null
                        ? request.Refine(solution, cancellation) : solution;
                }
                progress?.Invoke((x + 1) * 100 / request.Width);
            }
            return grid;
        }

        public static TransferSolution Solve(TransferRequest request, double departure, double duration)
            => SolveCandidate(request, departure, duration, false);

        public static TransferSolution Evaluate(TransferRequest request, double departure, double duration, bool bestEffort, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            departure = TransferOberth.Departure(request, departure);
            if (!double.IsFinite(departure)) return request.Reject("no_periapsis_window");
            if (departure < request.Earliest || departure > request.Latest || duration < request.MinimumDuration || duration > request.MaximumDuration) return default;
            var candidate = SolveCandidate(request, departure, duration, bestEffort);
            if (request.Oberth) return TransferOberth.Refine(request, candidate, departure, duration, cancellation);
            return candidate.Valid && request.Refine != null ? request.Refine(candidate, cancellation) : candidate;
        }

        private static TransferSolution SolveCandidate(TransferRequest request, double departure, double duration, bool bestEffort)
        {
            if (!(duration > 0 && departure >= request.Now)) return default;
            try
            {
                var start = request.Source ?? request.Parking;
                if (!start.State(departure, out var r1, out var v1)
                    || !request.Target.State(departure + duration, out var r2, out var v2)) return request.Reject("ephemeris");
                var (outgoing, incoming) = TransferLambert.Solve(request.CentralMu, r1, r2, duration,
                    TransferGeometry.Prograde, 0, LandingVector.Cross(r1, v1));
                var departureDv = outgoing - v1;
                var transferPosition = r1;
                var burnPosition = r1;
                double burnTime = departure;
                double arrivalTime = departure + duration;
                var excess = incoming - v2;
                LandingVector ArrivalOffset(LandingVector relativeVelocity)
                {
                    if (relativeVelocity.Length < 1) return default;
                    double periapsis = request.TargetRadius + request.PeriapsisAltitude;
                    double impact = periapsis * Math.Sqrt(1 + 2 * request.TargetMu / (periapsis * relativeVelocity.LengthSquared));
                    var perpendicular = LandingVector.Cross(relativeVelocity, LandingVector.Cross(r2, v2)).Unit;
                    return perpendicular * impact;
                }
                if (request.Ejection)
                {
                    if (!request.Parking.State(departure, out var r0, out var v0)) return default;
                    var infinity = departureDv;
                    bool converged = request.DepartureSoi <= 0;
                    for (int pass = 0; pass < 12; pass++)
                    {
                        var ejection = Eject(request.Parking.Mu, r0, v0, infinity, request.DepartureSafetyRadius);
                        if (!ejection.Valid) return request.Reject("ejection_geometry");
                        double coast = ejection.Coast;
                        // Use the nearest occurrence of this point on the parking orbit.
                        if (coast > request.Parking.Period / 2) coast -= request.Parking.Period;
                        burnTime = departure + coast;
                        if (burnTime < request.Earliest || burnTime > request.Latest || burnTime < request.Now) return default;
                        burnPosition = ejection.Position; departureDv = ejection.After - ejection.Before;
                        if (request.DepartureSoi <= 0) break;
                        if (!EscapeState(request.Parking.Mu, ejection, request.DepartureSoi, out var escapePosition, out var escapeVelocity, out double escapeSeconds)) return default;
                        double exitTime = burnTime + escapeSeconds;
                        if (exitTime >= arrivalTime || !request.Source.State(exitTime, out var sourceR, out var sourceV)) return default;
                        var normal = LandingVector.Cross(sourceR, sourceV);
                        transferPosition = sourceR + escapePosition;
                        (outgoing, incoming) = TransferLambert.Solve(request.CentralMu, sourceR + escapePosition,
                            r2 + ArrivalOffset(excess), arrivalTime - exitTime, TransferGeometry.Prograde, 0, normal);
                        excess = incoming - v2;
                        var desiredExit = outgoing - sourceV;
                        double infinitySquared = desiredExit.LengthSquared - 2 * request.Parking.Mu / request.DepartureSoi;
                        if (!(infinitySquared > 1)) return default;
                        if ((desiredExit - escapeVelocity).Length < 0.02) { converged = true; break; }
                        // Aim at the finite SOI boundary rather than treating the asymptote as the SOI velocity.
                        infinity = RotateFromTo(infinity.Unit, escapeVelocity.Unit, desiredExit.Unit) * Math.Sqrt(infinitySquared);
                    }
                    if (!converged) return request.Reject("ejection_convergence");
                    duration = arrivalTime - burnTime;
                }
                else
                {
                    // Aim beside the body's center, allowing gravity to focus the arrival onto the requested periapsis.
                    for (int pass = 0; pass < 3; pass++)
                    {
                        (outgoing, incoming) = TransferLambert.Solve(request.CentralMu, r1, r2 + ArrivalOffset(excess), duration,
                            TransferGeometry.Prograde, 0, LandingVector.Cross(r1, v1));
                        excess = incoming - v2;
                    }
                    departureDv = outgoing - v1;
                }
                double capture = CaptureDv(request.TargetMu, request.TargetRadius + request.PeriapsisAltitude, excess.Length);
                // Native prediction can request a higher transfer apocenter or safer pericenter on the next search.
                double transferH2 = LandingVector.Cross(transferPosition, outgoing).LengthSquared;
                double transferE = (LandingVector.Cross(outgoing, LandingVector.Cross(transferPosition, outgoing)) / request.CentralMu - transferPosition.Unit).Length;
                double transferInverseAxis = 2 / transferPosition.Length - outgoing.LengthSquared / request.CentralMu;
                if (!bestEffort && request.MinimumTransferApoapsis > 0 && transferInverseAxis > 0
                    && (1 + transferE) / transferInverseAxis < request.MinimumTransferApoapsis) return request.Reject("transfer_apoapsis");
                if (!bestEffort && request.MinimumTransferPeriapsis > 0 && transferH2 / request.CentralMu / (1 + transferE) < request.MinimumTransferPeriapsis) return request.Reject("transfer_periapsis");
                // Reject transfer orbits that pass through the central body on a direct moon transfer.
                if (!request.Ejection && request.Refine == null)
                {
                    if (!bestEffort && ReturnsBeforeEncounter(request.CentralMu, r1, outgoing, duration)) return request.Reject("return_before_encounter");
                    double h2 = LandingVector.Cross(r1, outgoing).LengthSquared;
                    double e = (LandingVector.Cross(outgoing, LandingVector.Cross(r1, outgoing)) / request.CentralMu - r1.Unit).Length;
                    if (h2 / request.CentralMu / (1 + e) < request.DepartureSafetyRadius)
                    {
                        var transferOrbit = new TransferOrbit(request.CentralMu, departure, r1, outgoing);
                        bool futurePeriapsis = transferOrbit.Valid ? transferOrbit.TimeToPeriapsis(departure) <= duration
                            : LandingVector.Dot(r1, outgoing) < 0;
                        if (futurePeriapsis) return request.Reject("central_impact");
                    }
                }
                foreach (var excluded in request.ExcludedWindows)
                    if (excluded.Contains(burnTime, duration) && !(bestEffort && excluded.AllowBestEffort)) return request.Reject("excluded_window");
                return new TransferSolution(burnTime, duration, burnPosition, departureDv, excess, capture,
                    Escapes(request.CentralMu, transferPosition, outgoing, request.CentralSoi), bestEffort);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArithmeticException || ex.GetType() == typeof(Exception))
            { return request.Reject("lambert_failure"); }
        }

        public static double CaptureDv(double mu, double periapsisRadius, double excess)
            => mu > 0 && periapsisRadius > 0 && double.IsFinite(excess)
                ? Math.Sqrt(excess * excess + 2 * mu / periapsisRadius) - Math.Sqrt(mu / periapsisRadius) : double.NaN;

        public static bool Escapes(double mu, LandingVector position, LandingVector velocity, double sphere)
        {
            double inverseAxis = 2 / position.Length - velocity.LengthSquared / mu;
            if (!(inverseAxis > 0)) return true;
            if (!(sphere > 0) || double.IsPositiveInfinity(sphere)) return false;
            var h = LandingVector.Cross(position, velocity);
            double eccentricity = (LandingVector.Cross(velocity, h) / mu - position.Unit).Length;
            return (1 + eccentricity) / inverseAxis >= sphere;
        }

        public static bool ReturnsBeforeEncounter(double mu, LandingVector position, LandingVector velocity, double duration)
        {
            var orbit = new TransferOrbit(mu, 0, position, velocity);
            if (!orbit.Valid) return false;
            double next = orbit.TimeToPeriapsis(0);
            if (next < 0.001) next = orbit.Period; // The departure periapsis is not a return.
            return next < duration - 1;
        }

        private static LandingVector RotateFromTo(LandingVector value, LandingVector from, LandingVector to)
        {
            var axis = LandingVector.Cross(from, to);
            double cosine = Math.Clamp(LandingVector.Dot(from, to), -1, 1);
            if (axis.Length < 1e-12) return cosine >= 0 ? value : -value;
            return LandingVector.Rotate(value, axis.Unit, Math.Atan2(axis.Length, cosine));
        }

        public static bool EscapeState(double mu, TransferEjection ejection, double sphere,
            out LandingVector position, out LandingVector velocity, out double seconds)
        {
            position = velocity = default; seconds = 0;
            var r = ejection.Position; var v = ejection.After;
            if (!ejection.Valid || !(sphere > r.Length && mu > 0)) return false;
            var h = LandingVector.Cross(r, v);
            var ev = LandingVector.Cross(v, h) / mu - r.Unit;
            double e = ev.Length, p = h.LengthSquared / mu, a = 1 / (2 / r.Length - v.LengthSquared / mu);
            if (!(e > 1 && a < 0)) return false;
            var ep = ev.Unit; var eq = LandingVector.Cross(h.Unit, ep);
            double cosine = Math.Clamp((p / sphere - 1) / e, -1, 1), nu = Math.Acos(cosine);
            position = (ep * cosine + eq * Math.Sin(nu)) * sphere;
            velocity = (ep * -Math.Sin(nu) + eq * (e + cosine)) * Math.Sqrt(mu / p);
            double initial = Math.Atan2(LandingVector.Dot(r, eq), LandingVector.Dot(r, ep));
            double Mean(double anomaly)
            {
                double ha = Math.Asinh(Math.Sin(anomaly) * Math.Sqrt(e * e - 1) / (1 + e * Math.Cos(anomaly)));
                return e * Math.Sinh(ha) - ha;
            }
            seconds = (Mean(nu) - Mean(initial)) * Math.Sqrt(-a * a * a / mu);
            return position.IsFinite && velocity.IsFinite && double.IsFinite(seconds) && seconds > 0;
        }

        // Adapted from MechJebLib SingleImpulseHyperbolicBurn (MIT alternative).
        // Ocampo & Saudemont (2010), with a complete parking-orbit scan and bounded golden-section refinement.
        public static TransferEjection Eject(double mu, LandingVector r0, LandingVector v0, LandingVector infinity, double safetyRadius = 0)
        {
            if (!(mu > 0) || !r0.IsFinite || !v0.IsFinite || !infinity.IsFinite || infinity.Length < 0.01) return default;
            const int samples = 36;
            double step = 2 * Math.PI / samples;
            var costs = new double[samples];
            double best = double.PositiveInfinity, rotation = 0;
            double Cost(double rot)
            {
                var result = EjectAt(mu, r0, v0, infinity, rot);
                if (!result.Valid) return double.PositiveInfinity;
                var h = LandingVector.Cross(result.Position, result.After);
                double e = (LandingVector.Cross(result.After, h) / mu - result.Position.Unit).Length;
                double periapsis = h.LengthSquared / mu / (1 + e);
                // An inward burn must survive its future periapsis; an outward burn need not revisit it.
                if (result.Position.Length < safetyRadius || (LandingVector.Dot(result.Position, result.After) < 0 && periapsis < safetyRadius))
                    return double.PositiveInfinity;
                return (result.After - result.Before).Length;
            }
            double Sample(int i) => (i + 0.5) * step - Math.PI;
            for (int i = 0; i < samples; i++)
            {
                costs[i] = Cost(Sample(i));
                if (costs[i] < best) { best = costs[i]; rotation = Sample(i); }
            }
            for (int i = 0; i < samples; i++)
            {
                if (!(costs[i] <= costs[(i + samples - 1) % samples] && costs[i] < costs[(i + 1) % samples])) continue;
                double low = Sample(i) - step, high = Sample(i) + step;
                const double fraction = 0.6180339887498949;
                double a = high - fraction * (high - low), b = low + fraction * (high - low), fa = Cost(a), fb = Cost(b);
                for (int j = 0; j < 40; j++)
                {
                    if (fa < fb) { high = b; b = a; fb = fa; a = high - fraction * (high - low); fa = Cost(a); }
                    else { low = a; a = b; fa = fb; b = low + fraction * (high - low); fb = Cost(b); }
                }
                if (Math.Min(fa, fb) < best) { best = Math.Min(fa, fb); rotation = fa < fb ? a : b; }
            }
            return double.IsFinite(best) ? EjectAt(mu, r0, v0, infinity, rotation) : default;
        }

        private static TransferEjection EjectAt(double mu, LandingVector r0, LandingVector v0, LandingVector infinity, double rotation)
        {
            var h = LandingVector.Cross(r0, v0);
            double a = 1 / (2 / r0.Length - v0.LengthSquared / mu);
            var eccentric = LandingVector.Cross(v0, h) / mu - r0.Unit;
            double e = eccentric.Length, p = a * (1 - e * e);
            if (!(a > 0 && e < 1 && p > 0 && h.Length > 0)) return default;
            var normal = h.Unit;
            var periapsis = e > 1e-10 ? eccentric.Unit : r0.Unit;
            var tangent = LandingVector.Cross(normal, periapsis).Unit;
            var direction = infinity.Unit;
            var burnUnit = 1 - Math.Abs(LandingVector.Dot(normal, direction)) < 1e-10
                ? periapsis : LandingVector.Cross(direction, normal).Unit;
            burnUnit = LandingVector.Rotate(burnUnit, normal, -rotation);
            var escapeNormal = LandingVector.Cross(burnUnit, direction).Unit;
            if (escapeNormal.LengthSquared < 0.5) return default;
            double nu = Math.Atan2(LandingVector.Dot(normal, LandingVector.Cross(periapsis, burnUnit)), LandingVector.Dot(periapsis, burnUnit));
            double radius = p / (1 + e * Math.Cos(nu));
            double ah = -mu / infinity.LengthSquared, k = -ah / radius;
            double angle = Math.Acos(Math.Clamp(LandingVector.Dot(burnUnit, direction), -1, 1));
            double s = Math.Sin(angle), c = Math.Cos(angle), s2 = s * s;
            double eh = Math.Max(1 + 1e-12, Math.Sqrt(s2 + 2 * k * k + 2 * k * (1 - c) + s * Math.Sqrt(s2 + 4 * k * (1 - c))) / (Math.Sqrt(2) * k));
            double ph = ah * (1 - eh * eh), nuh = Math.Acos(-1 / eh) - angle, turn = 2 * Math.Asin(1 / eh);
            var incoming = direction * Math.Cos(turn) + LandingVector.Cross(direction, escapeNormal) * Math.Sin(turn);
            var hp = (incoming - direction).Unit; var hq = (incoming + direction).Unit;
            var after = (hp * -Math.Sin(nuh) + hq * (eh + Math.Cos(nuh))) * Math.Sqrt(mu / ph);
            var before = (periapsis * -Math.Sin(nu) + tangent * (e + Math.Cos(nu))) * Math.Sqrt(mu / p);
            double nu0 = Math.Atan2(LandingVector.Dot(normal, LandingVector.Cross(periapsis, r0.Unit)), LandingVector.Dot(periapsis, r0.Unit));
            double ea0 = Math.Atan2(Math.Sqrt(1 - e * e) * Math.Sin(nu0), e + Math.Cos(nu0));
            double ea1 = Math.Atan2(Math.Sqrt(1 - e * e) * Math.Sin(nu), e + Math.Cos(nu));
            double motion = Math.Sqrt(mu / (a * a * a));
            double dt = (ea1 - e * Math.Sin(ea1) - ea0 + e * Math.Sin(ea0)) / motion;
            if (dt < 0) dt += 2 * Math.PI / motion;
            return new TransferEjection(burnUnit * radius, before, after, dt);
        }
    }
}
