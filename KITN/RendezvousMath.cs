/*
 * Two-impulse objective and synodic search scale adapted from MechJebLib/Maneuvers/TwoImpulseTransfer.cs.
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors.
 * Used under its MIT alternative; see THIRD_PARTY_NOTICES.md.
 */
using System;
using System.Collections.Generic;
using System.Threading;

namespace KITN
{
    internal sealed class RendezvousRequest
    {
        public TransferOrbit Own, Target;
        public double Now, Mu, SafetyRadius, Soi, StandOff = 500, Budget = double.PositiveInfinity;
        public double AlignmentLead = 180;
        public int MaximumPhasingOrbits = 5;
        public bool Close;
        public double MinimumSafeCost = double.PositiveInfinity;
        public Func<double, LandingVector, CancellationToken, TransferBurnState> PredictBurn;
        public bool Valid => Own?.Valid == true && Target?.Valid == true && double.IsFinite(Now)
            && double.IsFinite(Mu) && Mu > 0 && double.IsFinite(SafetyRadius) && SafetyRadius > 0
            && !double.IsNaN(Soi) && Soi > SafetyRadius && double.IsFinite(StandOff) && StandOff > 0
            && !double.IsNaN(Budget) && Budget > 0 && double.IsFinite(AlignmentLead) && AlignmentLead >= 60
            && MaximumPhasingOrbits >= 1 && MaximumPhasingOrbits <= 10;
    }

    internal readonly struct RendezvousSolution
    {
        public readonly double Departure, Arrival;
        public readonly LandingVector DepartureDelta, ArrivalDelta;
        public readonly int Revolutions;
        public readonly bool Valid;
        public double Cost => Valid ? DepartureDelta.Length + ArrivalDelta.Length : double.PositiveInfinity;
        public RendezvousSolution(double departure, double arrival, LandingVector first, LandingVector second, int revolutions)
        {
            Departure = departure; Arrival = arrival; DepartureDelta = first; ArrivalDelta = second; Revolutions = revolutions;
            Valid = double.IsFinite(departure) && double.IsFinite(arrival) && arrival > departure && first.IsFinite && second.IsFinite;
        }
    }

    internal static class RendezvousMath
    {
        public static double SearchPeriod(TransferOrbit own, TransferOrbit target, double now)
            => SearchPeriod(own, target, now, 5);
        private static double SearchPeriod(TransferOrbit own, TransferOrbit target, double now, int orbits)
        {
            own.State(now, out var r1, out var v1); target.State(now, out var r2, out var v2);
            int sign = Math.Sign(LandingVector.Dot(LandingVector.Cross(r1, v1), LandingVector.Cross(r2, v2)));
            double difference = Math.Abs(1 / own.Period - sign / target.Period);
            return Math.Min(difference > 1e-12 ? 1 / difference : double.PositiveInfinity,
                orbits * Math.Max(own.Period, target.Period));
        }

        public static bool Aim(TransferOrbit target, double time, double distance, out LandingVector r, out LandingVector v)
        {
            if (!target.State(time, out r, out v)) return false;
            // A radial stand-off avoids aiming through the target's center. Match its moving offset as well.
            var radial = r.Unit;
            v += (v - radial * LandingVector.Dot(v, radial)) * (distance / r.Length);
            r += radial * distance;
            return r.IsFinite && v.IsFinite;
        }

        public static bool SafeCoast(double mu, LandingVector r, LandingVector v, double duration, double safety, double soi)
        {
            var coast = new TransferCoast(mu, r, v);
            if (!coast.Valid || !double.IsFinite(duration) || duration <= 0) return false;
            var ellipse = new TransferOrbit(mu, 0, r, v);
            if (ellipse.Valid && duration >= ellipse.Period && ellipse.Apoapsis >= soi) return false;
            if (coast.NextPeriapsis >= 0 && coast.NextPeriapsis <= duration && coast.Periapsis <= safety) return false;
            // Rendezvous stays within the same primary body throughout the coast.
            for (int i = 0; i <= 48; i++)
                if (!coast.State(duration * i / 48, out var sample, out _) || sample.Length <= safety || sample.Length >= soi) return false;
            return true;
        }

        public static RendezvousSolution Solve(RendezvousRequest request, double departure, double arrival, int revolutions,
            CancellationToken cancellation, bool finite = true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!request.Valid || departure < request.Now + request.AlignmentLead || arrival <= departure
                || !request.Own.State(departure, out var r, out var v)
                || !Aim(request.Target, arrival, request.StandOff, out var targetR, out var targetV)) return default;
            try
            {
                var normal = LandingVector.Cross(r, v);
                var (outgoing, incoming) = TransferLambert.Solve(request.Mu, r, targetR, arrival - departure,
                    TransferGeometry.Prograde, revolutions, normal);
                var delta = outgoing - v;
                double cutoff = departure;
                var coastR = r; var coastV = outgoing;
                if (finite && request.PredictBurn != null)
                {
                    bool converged = false;
                    for (int pass = 0; pass < 32; pass++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var burn = request.PredictBurn(departure, delta, cancellation);
                        if (!burn.Valid || burn.Cutoff >= arrival - request.AlignmentLead - 30) return default;
                        cutoff = burn.Cutoff; coastR = burn.Position; coastV = burn.Velocity;
                        (outgoing, incoming) = TransferLambert.Solve(request.Mu, coastR, targetR, arrival - cutoff,
                            TransferGeometry.Prograde, revolutions, normal);
                        var correction = outgoing - coastV;
                        if (correction.Length < 0.001) { converged = true; break; }
                        delta += correction.Limited(500);
                        if (!delta.IsFinite) return default;
                    }
                    if (!converged) return default;
                }
                var result = new RendezvousSolution(departure, arrival, delta, targetV - incoming, revolutions);
                if (!SafeCoast(request.Mu, coastR, coastV, arrival - cutoff, request.SafetyRadius, request.Soi)) return default;
                request.MinimumSafeCost = Math.Min(request.MinimumSafeCost, result.Cost);
                return result.Cost <= request.Budget ? result : default;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArithmeticException || ex.GetType() == typeof(Exception)) { return default; }
        }

        public static RendezvousSolution Search(RendezvousRequest request, CancellationToken cancellation)
        {
            if (!request.Valid) throw new ArgumentException("Invalid rendezvous orbital state.");
            double period = Math.Max(request.Own.Period, request.Target.Period);
            double window = request.Close ? 300 : SearchPeriod(request.Own, request.Target, request.Now, request.MaximumPhasingOrbits);
            double minimum = Math.Max(request.AlignmentLead * 2, request.Close ? 90 : Math.Min(request.Own.Period, request.Target.Period) * 0.1);
            double maximum = request.Close ? Math.Max(minimum * 2, Math.Min(period / 4, 1800)) : period * request.MaximumPhasingOrbits;
            var seeds = new List<RendezvousSolution>();
            int width = request.Close ? 8 : 24, height = request.Close ? 16 : 32;
            int[] branches = request.Close ? new[] { 0 } : Branches(request.MaximumPhasingOrbits);
            for (int x = 0; x < width; x++)
            {
                cancellation.ThrowIfCancellationRequested();
                double departure = request.Now + request.AlignmentLead + 60 + window * x / (width - 1);
                for (int y = 0; y < height; y++)
                {
                    double arrival = departure + minimum + (maximum - minimum) * y / (height - 1);
                    foreach (int rev in branches)
                    {
                        var candidate = Solve(request, departure, arrival, rev, cancellation, false);
                        if (candidate.Valid) seeds.Add(candidate);
                    }
                }
            }
            seeds.Sort((a, b) => a.Cost.CompareTo(b.Cost));
            var best = default(RendezvousSolution);
            var chosen = new List<RendezvousSolution>();
            foreach (var seed in seeds)
            {
                bool nearby = false;
                foreach (var previous in chosen)
                    nearby |= seed.Revolutions == previous.Revolutions && Math.Abs(seed.Departure - previous.Departure) < window / width
                        && Math.Abs(seed.Arrival - seed.Departure - previous.Arrival + previous.Departure) < (maximum - minimum) / height;
                if (nearby) continue;
                chosen.Add(seed);
                var anchor = Solve(request, seed.Departure, seed.Arrival, seed.Revolutions, cancellation);
                double dx = window / width, dy = (maximum - minimum) / height;
                for (int pass = 0; anchor.Valid && pass < 24; pass++)
                {
                    var before = anchor;
                    for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                    {
                        if (x == 0 && y == 0) continue;
                        double departure = before.Departure + x * dx, duration = before.Arrival - before.Departure + y * dy;
                        if (departure < request.Now + request.AlignmentLead + 60 || departure > request.Now + request.AlignmentLead + 60 + window || duration < minimum || duration > maximum) continue;
                        var trial = Solve(request, departure, departure + duration, seed.Revolutions, cancellation);
                        if (trial.Cost < anchor.Cost) anchor = trial;
                    }
                    if (anchor.Departure == before.Departure && anchor.Arrival == before.Arrival) { dx *= 0.5; dy *= 0.5; }
                    if (dx < 0.1 && dy < 0.1) break;
                }
                if (anchor.Cost < best.Cost) best = anchor;
                if (chosen.Count >= 8) break;
            }
            return best;
        }

        // Correct the actual post-burn orbit while retaining the current encounter time.
        // The branch count can change as completed revolutions drop out of the remaining coast.
        public static RendezvousSolution Correct(RendezvousRequest request, double arrival, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!request.Valid || !double.IsFinite(arrival)) return default;
            double earliest = request.Now + request.AlignmentLead + 60;
            if (arrival - earliest <= request.AlignmentLead + 60) return default;
            // Prefer a midcourse correction closer to the encounter: small cutoff errors
            // then have less coast time to grow. Fall back to an earlier burn if needed.
            double late = Math.Max(earliest, arrival - Math.Max(request.AlignmentLead * 4 + 60,
                Math.Min(7200, (arrival - request.Now) * 0.25)));
            foreach (double departure in new[] { late, earliest })
            {
                var best = default(RendezvousSolution);
                foreach (int revolutions in Branches(request.MaximumPhasingOrbits))
                {
                    var candidate = Solve(request, departure, arrival, revolutions, cancellation);
                    if (candidate.Cost < best.Cost) best = candidate;
                }
                if (best.Valid) return best;
            }
            return default;
        }

        public static LandingVector ClosingVelocity(LandingVector relativePosition, double acceleration, double standOff)
            => ClosingVelocity(relativePosition, acceleration, standOff, 5, 2);
        internal static LandingVector ClosingVelocity(LandingVector relativePosition, double acceleration, double standOff,
            double maximumSpeed, double brakingMargin)
        {
            if (!relativePosition.IsFinite || !(acceleration > 0 && standOff > 0 && maximumSpeed > 0 && brakingMargin >= 1)
                || !double.IsFinite(acceleration + standOff + maximumSpeed + brakingMargin)) return default;
            double error = relativePosition.Length - standOff;
            double speed = Math.Min(maximumSpeed, Math.Sqrt(acceleration * Math.Abs(error) / brakingMargin));
            return -relativePosition.Unit * (Math.Sign(error) * speed);
        }
        public static bool Arrived(double distance, double relativeSpeed, double standOff)
            => Arrived(distance, relativeSpeed, standOff, Math.Max(5, standOff * 0.05), 0.02);
        internal static bool Arrived(double distance, double relativeSpeed, double standOff, double positionTolerance, double velocityTolerance)
            => double.IsFinite(distance) && double.IsFinite(relativeSpeed) && relativeSpeed >= 0
                && Math.Abs(distance - standOff) <= positionTolerance && relativeSpeed <= velocityTolerance;

        private static int[] Branches(int orbits)
        {
            var branches = new int[1 + (orbits - 1) * 2];
            for (int i = 1; i < orbits; i++) { branches[i * 2 - 1] = i; branches[i * 2] = -i; }
            return branches;
        }
    }
}
