using System;
using System.Collections.Generic;
using System.Threading;

namespace KITN
{
    internal sealed class TransferBodySnapshot
    {
        public readonly string Id;
        public readonly TransferOrbit Orbit;
        public readonly double Mu, Radius, Soi, SafetyRadius;
        public TransferBodySnapshot(string id, TransferOrbit orbit, double mu, double radius, double soi, double safetyRadius)
        { Id = id; Orbit = orbit; Mu = mu; Radius = radius; Soi = soi; SafetyRadius = safetyRadius; }
    }

    internal sealed class TransferFlyby
    {
        public readonly TransferBodySnapshot Body;
        public readonly double Time, Periapsis;
        public TransferFlyby(TransferBodySnapshot body, double time, double periapsis)
        { Body = body; Time = time; Periapsis = periapsis; }
    }

    internal readonly struct TransferBurnState
    {
        public readonly double Cutoff;
        public readonly LandingVector Position, Velocity;
        public readonly bool Valid;
        public TransferBurnState(double cutoff, LandingVector position, LandingVector velocity)
        { Cutoff = cutoff; Position = position; Velocity = velocity; Valid = double.IsFinite(cutoff) && position.IsFinite && velocity.IsFinite && position.Length > 0; }
    }

    internal static class TransferGravityAssist
    {
        private sealed class Flight
        {
            public LandingVector FinalR, FinalV, CentralR, CentralV;
            public TransferCoast First, Last, Flyby;
            public double FirstEpoch, FirstEntry, LastEpoch, FlybyTime;
        }

        public static double RequiredPeriapsis(double mu, LandingVector incoming, LandingVector outgoing)
        {
            if (!(mu > 0 && incoming.Length > 0 && outgoing.Length > 0)) return double.NaN;
            double sine = Math.Sqrt(Math.Max(0, (1 - Math.Clamp(LandingVector.Dot(incoming.Unit, outgoing.Unit), -1, 1)) / 2));
            return sine > 1e-8 ? mu / incoming.LengthSquared * (1 / sine - 1) : double.PositiveInfinity;
        }

        public static TransferSolution RefineEjection(TransferRequest request, TransferSolution solution, CancellationToken cancellation)
            => Shoot(request, null, solution.Departure, solution.Departure + solution.Duration, 0,
                solution.Position, solution.DeltaV, solution.BestEffort, cancellation);

        public static TransferSolution Improve(TransferRequest request, TransferSolution seed, double departure, double duration, CancellationToken cancellation)
        {
            departure = TransferOberth.Departure(request, departure);
            if (!double.IsFinite(departure)) return default;
            if (!request.Parking.State(departure, out var position, out _)) return default;
            double fraction = (seed.Flyby.Time - seed.Departure) / seed.Duration;
            return Shoot(request, seed.Flyby.Body, departure, departure + duration, departure + duration * fraction,
                position, seed.DeltaV, seed.BestEffort, cancellation);
        }

        public static void Search(TransferGrid grid, CancellationToken cancellation, Action<int> progress)
        {
            var request = grid.Request;
            if (!request.GravityAssists || request.AssistBodies.Length == 0) { progress?.Invoke(100); return; }
            bool bestEffort = grid.BestEffortPass;
            int departures = Math.Min(32, request.Width), durations = 16;
            for (int bodyIndex = 0; bodyIndex < request.AssistBodies.Length; bodyIndex++)
            {
                var body = request.AssistBodies[bodyIndex];
                var found = new List<TransferSolution>();
                for (int x = 0; x < departures; x++)
                    for (int y = 0; y < durations; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        double departure = request.Earliest + (request.Latest - request.Earliest) * x / (departures - 1);
                        double duration = request.MinimumDuration + (request.MaximumDuration - request.MinimumDuration) * y / (durations - 1);
                        var candidate = Solve(request, body, departure, duration, bestEffort, cancellation);
                        if (candidate.Valid) found.Add(candidate);
                    }
                foreach (bool capture in new[] { false, true })
                {
                    found.Sort((a, b) => a.Cost(capture).CompareTo(b.Cost(capture)));
                    for (int i = 0; i < Math.Min(12, found.Count); i++) grid.Candidates.Add(found[i]);
                }
                progress?.Invoke((bodyIndex + 1) * 100 / request.AssistBodies.Length);
            }
        }

        private static TransferSolution Solve(TransferRequest request, TransferBodySnapshot body, double departure, double duration,
            bool bestEffort, CancellationToken cancellation)
        {
            departure = TransferOberth.Departure(request, departure);
            if (!double.IsFinite(departure)) return default;
            if (!body.Orbit.Valid || !(body.Soi > body.SafetyRadius && body.Mu > 0)) return default;
            double arrival = departure + duration;
            if (!(request.Source ?? request.Parking).State(departure, out var sourceR, out var sourceV)
                || !request.Target.State(arrival, out var targetR, out var targetV)) return default;
            bool Geometry(double fraction, out LandingVector outgoing, out LandingVector incoming, out LandingVector exit, out double mismatch)
            {
                outgoing = incoming = exit = default; mismatch = double.NaN;
                double flyby = departure + duration * fraction;
                if (!body.Orbit.State(flyby, out var assistR, out var assistV)) return false;
                try
                {
                    var first = TransferLambert.Solve(request.CentralMu, sourceR, assistR, flyby - departure,
                        TransferGeometry.Prograde, 0, LandingVector.Cross(sourceR, sourceV));
                    var second = TransferLambert.Solve(request.CentralMu, assistR, targetR, arrival - flyby,
                        TransferGeometry.Prograde, 0, LandingVector.Cross(assistR, assistV));
                    outgoing = first.Vi; incoming = first.Vf - assistV; exit = second.Vi - assistV;
                    mismatch = incoming.Length - exit.Length; return double.IsFinite(mismatch);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is ArithmeticException || ex.GetType() == typeof(Exception)) { return false; }
            }
            double previousFraction = 0.08, previousMismatch = double.NaN;
            Geometry(previousFraction, out _, out _, out _, out previousMismatch);
            var best = default(TransferSolution);
            for (int sample = 1; sample <= 16; sample++)
            {
                cancellation.ThrowIfCancellationRequested();
                double fraction = 0.08 + 0.84 * sample / 16;
                if (!Geometry(fraction, out _, out _, out _, out double mismatch)) { previousMismatch = double.NaN; previousFraction = fraction; continue; }
                if (double.IsFinite(previousMismatch) && previousMismatch * mismatch <= 0)
                {
                    double lo = previousFraction, hi = fraction, lowMismatch = previousMismatch;
                    for (int pass = 0; pass < 36; pass++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        double mid = (lo + hi) / 2;
                        if (!Geometry(mid, out _, out _, out _, out double error)) break;
                        if (lowMismatch * error <= 0) hi = mid; else { lo = mid; lowMismatch = error; }
                    }
                    double root = (lo + hi) / 2;
                    if (Geometry(root, out var outgoing, out var incoming, out var exit, out double speedError))
                    {
                        double periapsis = RequiredPeriapsis(body.Mu, incoming, exit);
                        if (Math.Abs(speedError) < 0.1 && periapsis > body.SafetyRadius + 1000 && periapsis < body.Soi * 0.5)
                        {
                            double flybyTime = departure + duration * root;
                            body.Orbit.State(flybyTime, out var assistR, out _);
                            double impact = periapsis * Math.Sqrt(1 + 2 * body.Mu / (periapsis * incoming.LengthSquared));
                            var normal = LandingVector.Cross(incoming, exit).Unit;
                            var aim = assistR + LandingVector.Cross(incoming, normal).Unit * impact;
                            try
                            {
                                outgoing = TransferLambert.Solve(request.CentralMu, sourceR, aim, flybyTime - departure,
                                    TransferGeometry.Prograde, 0, LandingVector.Cross(sourceR, sourceV)).Vi;
                            }
                            catch (Exception ex) when (ex is ArgumentException || ex is ArithmeticException || ex.GetType() == typeof(Exception)) { continue; }
                            LandingVector delta = outgoing - sourceV, position = sourceR;
                            double burnTime = departure;
                            if (request.Ejection)
                            {
                                if (!request.Parking.State(departure, out var parkingR, out var parkingV)) return default;
                                var ejection = TransferMath.Eject(request.Parking.Mu, parkingR, parkingV, delta, request.DepartureSafetyRadius);
                                if (!ejection.Valid) continue;
                                double coast = ejection.Coast > request.Parking.Period / 2 ? ejection.Coast - request.Parking.Period : ejection.Coast;
                                burnTime += coast; position = ejection.Position; delta = ejection.After - ejection.Before;
                                if (request.Oberth)
                                {
                                    var rotated = TransferOberth.Rebase(request, new TransferSolution(burnTime, arrival - burnTime,
                                        position, delta, incoming, 0, false, bestEffort), departure, duration);
                                    if (!rotated.Valid) continue;
                                    burnTime = departure; position = rotated.Position; delta = rotated.DeltaV;
                                }
                                // Correct the first leg at the moving SOI boundary and finite engine cutoff
                                // before asking the complete flyby shooter to turn toward the final target.
                                var approachRequest = new TransferRequest
                                {
                                    Parking = request.Parking, Source = request.Source, Target = body.Orbit,
                                    CentralMu = request.CentralMu, CentralSafetyRadius = request.CentralSafetyRadius,
                                    CentralSoi = request.CentralSoi, Now = request.Now,
                                    Earliest = request.Earliest, Latest = request.Latest,
                                    MinimumDuration = 60, MaximumDuration = request.MaximumDuration,
                                    TargetMu = body.Mu, TargetRadius = body.Radius, TargetSoi = body.Soi,
                                    PeriapsisAltitude = periapsis - body.Radius,
                                    DepartureSafetyRadius = request.DepartureSafetyRadius, DepartureSoi = request.DepartureSoi,
                                    PredictBurn = request.PredictBurn
                                };
                                var seed = new TransferSolution(burnTime, flybyTime - burnTime, position, delta, incoming, 0, false, bestEffort);
                                var corrected = RefineEjection(approachRequest, seed, cancellation);
                                if (!corrected.Valid) continue;
                                delta = corrected.DeltaV;
                            }
                            var candidate = Shoot(request, body, burnTime, arrival, flybyTime, position, delta, bestEffort, cancellation);
                            if (candidate.Cost(request.IncludeCapture) < best.Cost(request.IncludeCapture)) best = candidate;
                        }
                    }
                }
                previousFraction = fraction; previousMismatch = mismatch;
            }
            return best;
        }

        private static TransferSolution Shoot(TransferRequest request, TransferBodySnapshot body, double departure, double arrival,
            double flybyTime, LandingVector position, LandingVector delta, bool bestEffort, CancellationToken cancellation)
        {
            if (departure < request.Earliest || departure > request.Latest || arrival - departure < request.MinimumDuration
                || arrival - departure > request.MaximumDuration || !request.Target.State(arrival, out var targetR, out var targetV)) return default;
            var aim = targetR;
            double requestedPe = request.TargetRadius + request.PeriapsisAltitude;
            for (int pass = 0; pass < 32; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!TryFlight(request, body, departure, arrival, flybyTime, delta, cancellation, out var flight)) return request.Reject("assist_missed");
                var error = aim - flight.FinalR;
                if (error.Length < 2)
                {
                    if (!Entry(flight.Last, flight.LastEpoch, request.Target, request.TargetSoi, arrival, cancellation,
                        out double entry, out var relativeR, out var relativeV))
                    {
                        // First iteration aims at the center; gravity focusing is then corrected at the finite SOI.
                        return request.Reject("assist_arrival_missed");
                    }
                    var approach = new TransferCoast(request.TargetMu, relativeR, relativeV);
                    if (!approach.Valid || approach.NextPeriapsis <= 0) return default;
                    if (Math.Abs(approach.Periapsis - requestedPe) > 30)
                    {
                        var h = LandingVector.Cross(relativeR, relativeV);
                        var direction = h.Length > 1 ? LandingVector.Cross(relativeV, h.Unit).Unit
                            : LandingVector.Cross(relativeV, LandingVector.Cross(targetR, targetV)).Unit;
                        double needed = 2 * request.TargetMu * requestedPe + 2 * approach.Energy * requestedPe * requestedPe;
                        if (!(needed > 0)) return default;
                        aim += direction * ((Math.Sqrt(needed) - h.Length) / relativeV.Length);
                        continue;
                    }
                    if ((body != null && flight.Flyby.Periapsis <= body.SafetyRadius + 1000) || approach.Periapsis <= request.TargetRadius) return request.Reject("assist_unsafe_periapsis");
                    if (!SafeCoast(request, flight.First, flight.FirstEntry, bestEffort)
                        || !SafeCoast(request, flight.Last, entry - flight.LastEpoch, bestEffort)) return request.Reject("assist_unsafe_coast");
                    foreach (var excluded in request.ExcludedWindows)
                        if (excluded.Contains(departure, arrival - departure) && !(bestEffort && excluded.AllowBestEffort)) return default;
                    double capture = Math.Abs(Math.Sqrt(2 * (approach.Energy + request.TargetMu / approach.Periapsis)) - Math.Sqrt(request.TargetMu / approach.Periapsis));
                    return new TransferSolution(departure, arrival - departure, position, delta, relativeV, capture,
                        TransferMath.Escapes(request.CentralMu, flight.CentralR, flight.CentralV, request.CentralSoi), bestEffort,
                        body == null ? null : new TransferFlyby(body, flight.FlybyTime, flight.Flyby.Periapsis));
                }
                const double epsilon = 0.05;
                var columns = new LandingVector[3];
                bool derivatives = true;
                for (int axis = 0; axis < 3; axis++)
                {
                    var perturbation = axis == 0 ? new LandingVector(epsilon, 0, 0) : axis == 1 ? new LandingVector(0, epsilon, 0) : new LandingVector(0, 0, epsilon);
                    if (!TryFlight(request, body, departure, arrival, flybyTime, delta + perturbation, cancellation, out var perturbed)) { derivatives = false; break; }
                    columns[axis] = (perturbed.FinalR - flight.FinalR) / epsilon;
                }
                if (!derivatives) return default;
                double determinant = LandingVector.Dot(columns[0], LandingVector.Cross(columns[1], columns[2]));
                if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-6) return default;
                var correction = new LandingVector(LandingVector.Dot(error, LandingVector.Cross(columns[1], columns[2])),
                    LandingVector.Dot(columns[0], LandingVector.Cross(error, columns[2])),
                    LandingVector.Dot(columns[0], LandingVector.Cross(columns[1], error))) / determinant;
                correction = correction.Limited(500);
                bool improved = false;
                for (double damping = 1; damping >= 1.0 / 64; damping *= 0.5)
                    if (TryFlight(request, body, departure, arrival, flybyTime, delta + correction * damping, cancellation, out var trial)
                        && (trial.FinalR - aim).Length < error.Length)
                    { delta += correction * damping; improved = true; break; }
                if (!improved) return request.Reject("assist_convergence");
            }
            return request.Reject("assist_convergence");
        }

        private static bool TryFlight(TransferRequest request, TransferBodySnapshot body, double departure, double arrival, double hint,
            LandingVector delta, CancellationToken cancellation, out Flight result)
        {
            result = null; cancellation.ThrowIfCancellationRequested();
            if (!request.Parking.State(departure, out var r, out var v)) return false;
            double epoch = departure; v += delta;
            if (request.PredictBurn != null)
            {
                var burn = request.PredictBurn(departure, delta, cancellation);
                if (!burn.Valid) return false;
                epoch = burn.Cutoff; r = burn.Position; v = burn.Velocity;
            }
            if (request.Ejection)
            {
                var ejection = new TransferCoast(request.Parking.Mu, r, v);
                double exit = ejection.OutgoingRadiusTime(request.DepartureSoi);
                if (!(exit > 0) || !ejection.State(exit, out r, out v)
                    || !request.Source.State(epoch + exit, out var sourceR, out var sourceV)) return false;
                if (ejection.Periapsis < request.DepartureSafetyRadius && ejection.NextPeriapsis > 0 && ejection.NextPeriapsis < exit) return false;
                epoch += exit; r += sourceR; v += sourceV;
            }
            var first = new TransferCoast(request.CentralMu, r, v);
            if (body == null)
            {
                if (!first.State(arrival - epoch, out var finalR, out var finalV)) return false;
                result = new Flight { FinalR = finalR, FinalV = finalV, CentralR = r, CentralV = v, First = first,
                    Last = first, FirstEpoch = epoch, FirstEntry = 0, LastEpoch = epoch };
                return true;
            }
            if (!Entry(first, epoch, body.Orbit, body.Soi, hint, cancellation, out double entry, out var localR, out var localV)) return false;
            var flyby = new TransferCoast(body.Mu, localR, localV);
            double localExit = flyby.OutgoingRadiusTime(body.Soi);
            if (!(flyby.Energy > 0 && localExit > 0 && flyby.NextPeriapsis > 0)
                || !flyby.State(localExit, out var exitR, out var exitV)) return false;
            double exitEpoch = entry + localExit;
            if (exitEpoch >= arrival || !body.Orbit.State(exitEpoch, out var bodyR, out var bodyV)) return false;
            var last = new TransferCoast(request.CentralMu, bodyR + exitR, bodyV + exitV);
            if (!last.State(arrival - exitEpoch, out var endR, out var endV)) return false;
            result = new Flight { FinalR = endR, FinalV = endV, CentralR = r, CentralV = v, First = first, Last = last,
                Flyby = flyby, FirstEpoch = epoch, FirstEntry = entry - epoch, LastEpoch = exitEpoch, FlybyTime = entry + flyby.NextPeriapsis };
            return true;
        }

        private static bool Entry(TransferCoast coast, double epoch, TransferOrbit body, double soi, double hint,
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

        private static bool SafeCoast(TransferRequest request, TransferCoast coast, double duration, bool bestEffort)
            => (bestEffort || (coast.Periapsis >= request.MinimumTransferPeriapsis
                && (coast.Energy >= 0 || -request.CentralMu / coast.Energy - coast.Periapsis >= request.MinimumTransferApoapsis)))
            && !(coast.NextPeriapsis > 0.001 && coast.NextPeriapsis < duration
                && (coast.Periapsis < (request.CentralSafetyRadius > 0 ? request.CentralSafetyRadius : request.DepartureSafetyRadius)
                    || (!request.Ejection && !bestEffort)));
    }
}
