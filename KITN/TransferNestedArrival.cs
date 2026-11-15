using System;
using System.Collections.Generic;
using System.Threading;

namespace KITN
{
    internal sealed class TransferEncounter
    {
        public readonly TransferBodySnapshot Body;
        public readonly double Entry, PeriapsisTime, Periapsis;
        public readonly LandingVector Position, Velocity;
        public TransferEncounter(TransferBodySnapshot body, double entry, LandingVector r, LandingVector v, TransferCoast coast)
        { Body = body; Entry = entry; Position = r; Velocity = v; PeriapsisTime = entry + coast.NextPeriapsis; Periapsis = coast.Periapsis; }
    }

    internal sealed class TransferArrival
    {
        public readonly TransferEncounter[] Encounters;
        public TransferArrival(TransferEncounter[] encounters) { Encounters = encounters; }
    }

    // A single departure burn, ballistic passage through the destination's ancestors,
    // and capture only at the final body. All state is copied before worker execution.
    internal static class TransferNestedArrival
    {
        private sealed class Flight
        {
            public LandingVector FinalR, FinalV, CentralR, CentralV;
            public TransferCoast Last;
            public double LastEpoch;
            public readonly List<TransferEncounter> Encounters = new List<TransferEncounter>();
            public readonly List<(TransferCoast coast, double duration, double safety)> Coasts = new List<(TransferCoast, double, double)>();
        }

        public static TransferSolution Refine(TransferRequest request, TransferSolution seed, CancellationToken cancellation)
        {
            if (!seed.Valid || !request.NestedArrival) return seed;
            double arrival = seed.Departure + seed.Duration;
            var target = request.ArrivalPath[request.ArrivalPath.Length - 1];
            if (!target.Orbit.State(arrival, out var targetR, out var targetV)) return request.Reject("nested_ephemeris");
            double requestedPe = request.TargetRadius + request.PeriapsisAltitude;
            if (!(requestedPe > target.SafetyRadius && requestedPe < target.Soi)) return request.Reject("nested_arrival_radius");
            var aim = targetR;
            var delta = seed.DeltaV;
            try
            {
                for (int pass = 0; pass < 64; pass++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!TryFlight(request, seed.Departure, arrival, delta, cancellation, out var flight)) return request.Reject("nested_parent_missed");
                    var error = aim - flight.FinalR;
                    if (error.Length < 1)
                    {
                        if (!Entry(flight.Last, flight.LastEpoch, target.Orbit, target.Soi, arrival, cancellation,
                            out double entry, out var localR, out var localV)) return request.Reject("nested_target_missed");
                        var approach = new TransferCoast(target.Mu, localR, localV);
                        if (!approach.Valid || !(approach.NextPeriapsis > 0)) return request.Reject("nested_arrival_orbit");
                        if (Math.Abs(approach.Periapsis - requestedPe) > 30)
                        {
                            var h = LandingVector.Cross(localR, localV);
                            var direction = h.Length > 1 ? LandingVector.Cross(localV, h.Unit).Unit
                                : LandingVector.Cross(localV, LandingVector.Cross(targetR, targetV)).Unit;
                            double needed = 2 * target.Mu * requestedPe + 2 * approach.Energy * requestedPe * requestedPe;
                            if (!(needed > 0) || direction.Length < 0.5) return request.Reject("nested_bplane");
                            aim += direction * ((Math.Sqrt(needed) - h.Length) / localV.Length);
                            continue;
                        }
                        // Safety applies to the segment actually flown, up to the next SOI entry.
                        flight.Coasts.Add((flight.Last, entry - flight.LastEpoch,
                            request.ArrivalPath[request.ArrivalPath.Length - 2].SafetyRadius));
                        foreach (var leg in flight.Coasts)
                            if (leg.coast.NextPeriapsis > 0.001 && leg.coast.NextPeriapsis <= leg.duration
                                && leg.coast.Periapsis <= leg.safety) return request.Reject("nested_parent_impact");
                        for (int i = 0; i < flight.Encounters.Count; i++)
                        {
                            var encounter = flight.Encounters[i];
                            double nextEntry = i + 1 < flight.Encounters.Count ? flight.Encounters[i + 1].Entry : entry;
                            var parentCoast = new TransferCoast(encounter.Body.Mu, encounter.Position, encounter.Velocity);
                            double parentExit = parentCoast.OutgoingRadiusTime(encounter.Body.Soi);
                            if (double.IsFinite(parentExit) && encounter.Entry + parentExit <= nextEntry)
                                return request.Reject("nested_parent_exited");
                        }
                        if (approach.Periapsis <= target.SafetyRadius) return request.Reject("nested_target_impact");
                        double exit = approach.OutgoingRadiusTime(target.Soi);
                        if (double.IsFinite(exit) && approach.NextPeriapsis >= exit) return request.Reject("nested_capture_missed");
                        foreach (var excluded in request.ExcludedWindows)
                            if (excluded.Contains(seed.Departure, seed.Duration)) return request.Reject("excluded_window");
                        flight.Encounters.Add(new TransferEncounter(target, entry, localR, localV, approach));
                        double capture = Math.Abs(Math.Sqrt(2 * (approach.Energy + target.Mu / approach.Periapsis))
                            - Math.Sqrt(target.Mu / approach.Periapsis));
                        return new TransferSolution(seed.Departure, seed.Duration, seed.Position, delta, localV, capture,
                            TransferMath.Escapes(request.CentralMu, flight.CentralR, flight.CentralV, request.CentralSoi), false, null,
                            new TransferArrival(flight.Encounters.ToArray()));
                    }
                    // Finite differences include the departure burn and every moving SOI crossing.
                    const double epsilon = 0.01;
                    var columns = new LandingVector[3];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var perturbation = Axis(axis) * epsilon;
                        if (!TryFlight(request, seed.Departure, arrival, delta + perturbation, cancellation, out var shifted))
                            return request.Reject("nested_derivative");
                        columns[axis] = (shifted.FinalR - flight.FinalR) / epsilon;
                    }
                    double determinant = LandingVector.Dot(columns[0], LandingVector.Cross(columns[1], columns[2]));
                    if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-6) return request.Reject("nested_singular");
                    var correction = new LandingVector(LandingVector.Dot(error, LandingVector.Cross(columns[1], columns[2])),
                        LandingVector.Dot(columns[0], LandingVector.Cross(error, columns[2])),
                        LandingVector.Dot(columns[0], LandingVector.Cross(columns[1], error))) / determinant;
                    correction = correction.Limited(500);
                    bool improved = false;
                    for (double damping = 1; damping >= 1.0 / 128; damping *= 0.5)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (TryFlight(request, seed.Departure, arrival, delta + correction * damping, cancellation, out var trial)
                            && (trial.FinalR - aim).Length < error.Length)
                        { delta += correction * damping; improved = true; break; }
                    }
                    if (!improved) return request.Reject("nested_convergence");
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArithmeticException || ex.GetType() == typeof(Exception))
            { return request.Reject("nested_solver_failure"); }
            return request.Reject("nested_convergence");
        }

        private static LandingVector Axis(int axis) => axis == 0 ? new LandingVector(1, 0, 0)
            : axis == 1 ? new LandingVector(0, 1, 0) : new LandingVector(0, 0, 1);

        private static bool TryFlight(TransferRequest request, double departure, double arrival, LandingVector delta,
            CancellationToken cancellation, out Flight result)
        {
            result = null;
            cancellation.ThrowIfCancellationRequested();
            if (!request.Parking.State(departure, out var r, out var v)) return false;
            double epoch = departure; v += delta;
            if (request.PredictBurn != null)
            {
                var burn = request.PredictBurn(departure, delta, cancellation);
                if (!burn.Valid) return false;
                epoch = burn.Cutoff; r = burn.Position; v = burn.Velocity;
            }
            var flight = new Flight();
            if (request.Ejection)
            {
                var departureCoast = new TransferCoast(request.Parking.Mu, r, v);
                double exit = departureCoast.OutgoingRadiusTime(request.DepartureSoi);
                if (!(exit > 0) || !departureCoast.State(exit, out r, out v)
                    || !request.Source.State(epoch + exit, out var sourceR, out var sourceV)) return false;
                flight.Coasts.Add((departureCoast, exit, request.DepartureSafetyRadius));
                epoch += exit; r += sourceR; v += sourceV;
            }
            if (epoch >= arrival) return false;
            flight.CentralR = r; flight.CentralV = v;
            var coast = new TransferCoast(request.CentralMu, r, v);
            double safety = request.CentralSafetyRadius > 0 ? request.CentralSafetyRadius : request.DepartureSafetyRadius;
            for (int i = 0; i < request.ArrivalPath.Length - 1; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var parent = request.ArrivalPath[i];
                if (!Entry(coast, epoch, parent.Orbit, parent.Soi, arrival, cancellation, out double entry, out var localR, out var localV)) return false;
                flight.Coasts.Add((coast, entry - epoch, safety));
                coast = new TransferCoast(parent.Mu, localR, localV);
                if (!coast.Valid) return false;
                flight.Encounters.Add(new TransferEncounter(parent, entry, localR, localV, coast));
                epoch = entry; safety = parent.SafetyRadius;
            }
            if (!coast.State(arrival - epoch, out flight.FinalR, out flight.FinalV)) return false;
            flight.Last = coast; flight.LastEpoch = epoch;
            result = flight; return true;
        }

        internal static bool Entry(TransferCoast coast, double epoch, TransferOrbit body, double soi, double hint,
            CancellationToken cancellation, out double time, out LandingVector r, out LandingVector v)
        {
            time = 0; r = v = default;
            bool Relative(double t, out LandingVector relativeR, out LandingVector relativeV)
            {
                relativeR = relativeV = default;
                if (!coast.State(t - epoch, out var shipR, out var shipV) || !body.State(t, out var bodyR, out var bodyV)) return false;
                relativeR = shipR - bodyR; relativeV = shipV - bodyV; return true;
            }
            if (!(hint > epoch && soi > 0) || !Relative(epoch, out var initial, out _) || initial.Length <= soi
                || !Relative(hint, out var final, out _) || final.Length >= soi) return false;
            double lo = epoch, hi = hint;
            for (int pass = 0; pass < 52; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                double mid = (lo + hi) / 2;
                if (!Relative(mid, out var relative, out _)) return false;
                if (relative.Length > soi) lo = mid; else hi = mid;
            }
            time = hi; return Relative(time, out r, out v);
        }
    }
}
