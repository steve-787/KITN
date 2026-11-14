using System;
using System.Threading;

namespace KITN
{
    internal static class TransferOberth
    {
        // A circular orbit has the same speed and altitude everywhere; it has no
        // privileged periapsis. An eccentric orbit has discrete Oberth opportunities.
        public static double Departure(TransferRequest request, double time)
        {
            if (!request.Oberth || request.Parking.Eccentricity < 1e-5) return time;
            double first = request.Earliest - 0.001 + request.Parking.TimeToPeriapsis(request.Earliest - 0.001);
            if (first < request.Earliest - 1e-6) first += request.Parking.Period;
            double count = Math.Floor((request.Latest - first) / request.Parking.Period);
            if (count < 0) return double.NaN;
            return first + Math.Clamp(Math.Round((time - first) / request.Parking.Period), 0, count) * request.Parking.Period;
        }

        public static TransferSolution Rebase(TransferRequest request, TransferSolution seed, double departure, double duration)
        {
            if (!seed.Valid || !request.Parking.State(departure, out var r, out var v)
                || !request.Parking.State(seed.Departure, out _, out var before)) return default;
            var normal = LandingVector.Cross(seed.Position, before).Unit;
            var tangent = LandingVector.Cross(normal, seed.Position.Unit);
            var newNormal = LandingVector.Cross(r, v).Unit;
            var newTangent = LandingVector.Cross(newNormal, r.Unit);
            var delta = r.Unit * LandingVector.Dot(seed.DeltaV, seed.Position.Unit)
                + newTangent * LandingVector.Dot(seed.DeltaV, tangent) + newNormal * LandingVector.Dot(seed.DeltaV, normal);
            return new TransferSolution(departure, duration, r, delta, seed.ArrivalExcess, seed.CaptureDv,
                seed.EscapesCentral, seed.BestEffort, seed.Flyby);
        }

        public static TransferSolution Refine(TransferRequest request, TransferSolution seed, double departure, double duration, CancellationToken cancellation)
        {
            if (!seed.Valid) return seed;
            if (request.Ejection)
            {
                seed = Rebase(request, seed, departure, duration);
                return TransferGravityAssist.RefineEjection(request, seed, cancellation);
            }
            return request.Refine == null ? seed : request.Refine(seed, cancellation);
        }
    }
}
