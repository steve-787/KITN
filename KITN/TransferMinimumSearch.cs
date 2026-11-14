using System;
using System.Collections.Generic;
using System.Threading;

namespace KITN
{
    internal static class TransferMinimumSearch
    {
        public static void Refine(TransferGrid grid, CancellationToken cancellation, Action<int> progress)
        {
            var request = grid.Request;
            double stepX = (request.Latest - request.Earliest) / (request.Width - 1);
            double stepY = (request.MaximumDuration - request.MinimumDuration) / (request.Height - 1);
            var seeds = new List<(TransferSolution solution, bool capture)>();
            foreach (bool capture in new[] { false, true })
            {
                var ordered = new List<TransferSolution>();
                foreach (var candidate in grid.Cells) if (candidate.Valid) ordered.Add(candidate);
                foreach (var candidate in grid.Candidates) if (candidate.Valid) ordered.Add(candidate);
                ordered.Sort((a, b) => a.Cost(capture).CompareTo(b.Cost(capture)));
                var chosen = new List<TransferSolution>();
                foreach (var candidate in ordered)
                {
                    bool nearby = false;
                    foreach (var previous in chosen)
                        nearby |= Math.Abs(previous.Departure - candidate.Departure) < stepX * 3
                            && Math.Abs(previous.Duration - candidate.Duration) < stepY * 3
                            && previous.Flyby?.Body.Id == candidate.Flyby?.Body.Id;
                    if (nearby) continue;
                    chosen.Add(candidate); seeds.Add((candidate, capture));
                    if (chosen.Count >= 6) break;
                }
            }
            for (int i = 0; i < seeds.Count; i++)
            {
                var best = seeds[i].solution;
                bool discrete = request.Oberth && request.Parking.Eccentricity >= 1e-5;
                double dx = discrete ? Math.Max(request.Parking.Period, Math.Round(stepX / request.Parking.Period) * request.Parking.Period) : stepX, dy = stepY;
                for (int pass = 0; pass < 36; pass++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var anchor = best;
                    for (int y = -1; y <= 1; y++)
                        for (int x = -1; x <= 1; x++)
                        {
                            if ((x == 0 && y == 0) || (dx == 0 && x != 0)) continue;
                            var candidate = anchor.Flyby == null
                                ? TransferMath.Evaluate(request, anchor.Departure + x * dx, anchor.Duration + y * dy, anchor.BestEffort, cancellation)
                                : TransferGravityAssist.Improve(request, anchor, anchor.Departure + x * dx, anchor.Duration + y * dy, cancellation);
                            if (candidate.Cost(seeds[i].capture) < best.Cost(seeds[i].capture)) best = candidate;
                        }
                    if (best.Departure == anchor.Departure && best.Duration == anchor.Duration)
                    {
                        dx *= 0.5; dy *= 0.5;
                        if (discrete && dx < request.Parking.Period * 0.75) dx = 0;
                    }
                    if (dx < 0.05 && dy < 0.5) break;
                }
                grid.Candidates.Add(best);
                progress?.Invoke((i + 1) * 100 / seeds.Count);
            }
            progress?.Invoke(100);
        }
    }
}
