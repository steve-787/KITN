using System;
using System.Threading;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    // Only copied ephemerides and engine phases cross the worker boundary.
    internal static class TransferBurnRefiner
    {
        public static void Attach(TransferRequest request, FiniteBurnPhase[] phases)
        {
            if (phases.Length > 0)
            {
                request.PredictBurn = (departure, delta, cancellation) => Predict(request, phases, departure, delta, cancellation);
                request.Refine = (solution, cancellation) => Refine(request, solution, phases, cancellation);
            }
        }

        private static TransferBurnState Predict(TransferRequest request, FiniteBurnPhase[] phases, double departure,
            LandingVector delta, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            double seconds = FiniteBurnPredictor.Duration(phases, delta.Length, out double delivered);
            double ignition = departure - seconds / 2;
            if (delivered < delta.Length - 0.01 || ignition < request.Now + TransferSafety.AlignmentLeadSeconds
                || !request.Parking.State(ignition, out var r, out var v)) return default;
            var arc = FiniteBurnPredictor.Fly(request.Ejection ? request.Parking.Mu : request.CentralMu,
                new UniverseTime(ignition), Native(r), Native(v), Native(delta.Unit), phases, delta.Length, double.PositiveInfinity);
            for (int sample = 0; sample <= 32; sample++)
                if (arc.Sample(sample).PositionCci.Length() <= request.DepartureSafetyRadius) return default;
            return new TransferBurnState(arc.Cutoff.Seconds(), Vector(arc.PositionCci), Vector(arc.VelocityCci));
        }

        public static TransferSolution Refine(TransferRequest request, TransferSolution solution,
            FiniteBurnPhase[] phases, CancellationToken cancellation)
        {
            if (!solution.Valid || phases.Length == 0) return solution;
            if (request.Ejection) return TransferGravityAssist.RefineEjection(request, solution, cancellation);
            double arrival = solution.Departure + solution.Duration;
            if (!request.Target.State(arrival, out var targetR, out var targetV)) return default;
            var excess = solution.ArrivalExcess;
            double periapsis = request.TargetRadius + request.PeriapsisAltitude;
            double impact = periapsis * Math.Sqrt(1 + 2 * request.TargetMu / (periapsis * excess.LengthSquared));
            var aim = targetR + LandingVector.Cross(excess, LandingVector.Cross(targetR, targetV)).Unit * impact;
            var delta = solution.DeltaV;
            try
            {
                for (int pass = 0; pass < 64; pass++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double seconds = FiniteBurnPredictor.Duration(phases, delta.Length, out double delivered);
                    double ignition = solution.Departure - seconds / 2;
                    if (delivered < delta.Length - 0.01) return request.Reject("insufficient_departure_fuel");
                    if (ignition < request.Now + TransferSafety.AlignmentLeadSeconds) return request.Reject("ignition_too_soon");
                    if (!request.Parking.State(ignition, out var r, out var v)) return request.Reject("burn_ephemeris");
                    var arc = FiniteBurnPredictor.Fly(request.CentralMu, new UniverseTime(ignition),
                        Native(r), Native(v), Native(delta.Unit), phases, delta.Length, double.PositiveInfinity);
                    double cutoff = arc.Cutoff.Seconds();
                    if (cutoff >= arrival) return default;
                    var position = Vector(arc.PositionCci); var velocity = Vector(arc.VelocityCci);
                    var (desired, incoming) = TransferLambert.Solve(request.CentralMu, position, aim, arrival - cutoff,
                        TransferGeometry.Prograde, 0, LandingVector.Cross(solution.Position, v));
                    var correction = desired - velocity;
                    if (correction.Length < 0.001)
                    {
                        for (int sample = 0; sample <= 32; sample++)
                            if (arc.Sample(sample).PositionCci.Length() <= request.DepartureSafetyRadius) return request.Reject("burn_impact");
                        var coast = new TransferCoast(request.CentralMu, position, velocity);
                        if (!coast.Valid) return default;
                        double encounterSeconds = arrival - cutoff;
                        double capture = TransferMath.CaptureDv(request.TargetMu, periapsis, (incoming - targetV).Length);
                        if (request.TargetSoi > 0)
                        {
                            if (!TryArrival(request, coast, cutoff, arrival, cancellation, out encounterSeconds, out var relativeR, out var relativeV)) return request.Reject("missed_target_soi");
                            var approach = new TransferCoast(request.TargetMu, relativeR, relativeV);
                            if (!approach.Valid || approach.NextPeriapsis <= 0 || periapsis >= request.TargetSoi) return request.Reject("invalid_arrival_orbit");
                            if (Math.Abs(approach.Periapsis - periapsis) > 100)
                            {
                                // Correct the B-plane offset from the finite SOI state, rather than assuming infinity.
                                var h = LandingVector.Cross(relativeR, relativeV);
                                double neededH2 = 2 * request.TargetMu * periapsis + 2 * approach.Energy * periapsis * periapsis;
                                if (!(neededH2 > 0)) return default;
                                aim += LandingVector.Cross(relativeV, h.Unit).Unit
                                    * ((Math.Sqrt(neededH2) - h.Length) / relativeV.Length);
                                continue;
                            }
                            if (approach.Periapsis <= request.TargetRadius) return request.Reject("arrival_impact");
                            capture = Math.Abs(Math.Sqrt(2 * (approach.Energy + request.TargetMu / approach.Periapsis))
                                - Math.Sqrt(request.TargetMu / approach.Periapsis));
                        }
                        if (coast.NextPeriapsis > 0.001 && coast.NextPeriapsis <= encounterSeconds)
                        {
                            if (coast.Periapsis < request.DepartureSafetyRadius) return request.Reject("central_impact");
                            if (!solution.BestEffort) return request.Reject("return_before_encounter");
                        }
                        if (!solution.BestEffort && request.MinimumTransferPeriapsis > 0 && coast.Periapsis < request.MinimumTransferPeriapsis) return request.Reject("transfer_periapsis");
                        if (!solution.BestEffort && request.MinimumTransferApoapsis > 0 && coast.Energy < 0)
                        {
                            double apoapsis = -request.CentralMu / coast.Energy - coast.Periapsis;
                            if (apoapsis < request.MinimumTransferApoapsis) return request.Reject("transfer_apoapsis");
                        }
                        return new TransferSolution(solution.Departure, solution.Duration, solution.Position, delta,
                            incoming - targetV, capture,
                            TransferMath.Escapes(request.CentralMu, position, velocity, request.CentralSoi), solution.BestEffort);
                    }
                    delta += correction;
                    if (!delta.IsFinite) return default;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArithmeticException || ex.GetType() == typeof(Exception))
            { return request.Reject("burn_solver_failure"); }
            return request.Reject("burn_arrival_convergence");
        }

        private static bool TryArrival(TransferRequest request, TransferCoast coast, double cutoff, double arrival,
            CancellationToken cancellation, out double entry, out LandingVector r, out LandingVector v)
        {
            entry = 0; r = v = default;
            bool Relative(double seconds, out LandingVector relativeR, out LandingVector relativeV)
            {
                relativeR = relativeV = default;
                if (!coast.State(seconds, out var shipR, out var shipV)
                    || !request.Target.State(cutoff + seconds, out var moonR, out var moonV)) return false;
                relativeR = shipR - moonR; relativeV = shipV - moonV; return true;
            }
            double lo = 0, hi = arrival - cutoff;
            if (!Relative(lo, out var start, out _) || start.Length <= request.TargetSoi
                || !Relative(hi, out var end, out _) || end.Length >= request.TargetSoi) return false;
            for (int pass = 0; pass < 40; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                double mid = (lo + hi) / 2;
                if (!Relative(mid, out var relative, out _)) return false;
                if (relative.Length > request.TargetSoi) lo = mid; else hi = mid;
            }
            entry = hi;
            return Relative(entry, out r, out v);
        }

        private static double3 Native(LandingVector value) => new double3(value.X, value.Y, value.Z);
        private static LandingVector Vector(double3 value) => new LandingVector(value.X, value.Y, value.Z);
    }
}
