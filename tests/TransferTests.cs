using KITN;

internal static class TransferTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double actual, double expected, double tolerance, string message)
        => Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} != {expected}");

    public static void Lambert()
    {
        var start = new LandingVector(5e6, 10e6, 2.1e6); var end = new LandingVector(-14.6e6, 2.5e6, 7e6);
        var (a, b) = TransferLambert.Solve(3.986004418e14, start, end, 3600);
        Near(a.X, -5992.495, 0.02, "Vallado departure x"); Near(a.Y, 1925.367, 0.02, "Vallado departure y"); Near(a.Z, 3245.638, 0.02, "Vallado departure z");
        Near(b.X, -3312.459, 0.02, "Vallado arrival x"); Near(b.Y, -4196.619, 0.02, "Vallado arrival y"); Near(b.Z, -385.289, 0.02, "Vallado arrival z");
        double mu = 3.986004418e14, r1 = 7e6, r2 = 42.164e6, axis = (r1 + r2) / 2;
        double duration = Math.PI * Math.Sqrt(axis * axis * axis / mu);
        (a, b) = TransferLambert.Solve(mu, new LandingVector(r1, 0, 0), new LandingVector(-r2, 0, 0), duration,
            TransferGeometry.Prograde, 0, new LandingVector(0, 0, 1));
        Near(a.X, 0, 1e-3, "Antipodal departure radial"); Near(a.Y, Math.Sqrt(mu * (2 / r1 - 1 / axis)), 1e-3, "Hohmann departure");
        Near(b.Y, -Math.Sqrt(mu * (2 / r2 - 1 / axis)), 1e-3, "Hohmann arrival");
        var reverse = TransferLambert.Solve(mu, new LandingVector(r1, 0, 0), new LandingVector(0, -r2, 0), duration,
            TransferGeometry.Prograde, 0, new LandingVector(0, 0, -1));
        Check(reverse.Vi.Y < 0, "Retrograde transfer must remain retrograde");
        bool rejected = false;
        try { TransferLambert.Solve(mu, start, end, double.NaN); } catch (ArgumentException) { rejected = true; } catch (Exception) { rejected = true; }
        Check(rejected, "Nonfinite Lambert time accepted");
    }

    public static void Ephemerides()
    {
        double mu = 3.986004418e14;
        foreach (double eccentricity in new[] { 0.0, 0.5, 0.95 })
            foreach (int direction in new[] { -1, 1 })
            {
                var r = new LandingVector(7e6, 0, 0);
                var v = new LandingVector(0, Math.Sqrt(mu * (1 + eccentricity) / r.Length) * direction * 0.8,
                    Math.Sqrt(mu * (1 + eccentricity) / r.Length) * direction * 0.6);
                var orbit = new TransferOrbit(mu, 321, r, v);
                Check(orbit.Valid, "Bound orbit rejected");
                Check(orbit.State(321 + orbit.Period * 100, out var rp, out var vp), "Long ephemeris propagation failed");
                Near((rp - r).Length, 0, 0.01, "Periodic position"); Near((vp - v).Length, 0, 1e-5, "Periodic velocity");
                orbit.State(321 + orbit.Period * 0.37, out rp, out vp);
                Near(vp.LengthSquared / 2 - mu / rp.Length, v.LengthSquared / 2 - mu / r.Length, 1e-5, "Energy conservation");
                Near((LandingVector.Cross(rp, vp) - LandingVector.Cross(r, v)).Length / LandingVector.Cross(r, v).Length, 0, 1e-12, "Angular momentum");
            }
        Check(!new TransferOrbit(mu, 0, new LandingVector(7e6, 0, 0), new LandingVector(0, 30000, 0)).Valid, "Hyperbolic parking orbit accepted");
    }

    public static void Ejections()
    {
        double mu = 3.986004418e14;
        foreach (double eccentricity in new[] { 0.0, 0.4, 0.8 })
            foreach (var infinity in new[] { new LandingVector(3000, 1200, 0), new LandingVector(-1500, 2200, 2700), new LandingVector(0, 0, 4000) })
            {
                var r = new LandingVector(7e6, 0, 0); var v = new LandingVector(0, Math.Sqrt(mu * (1 + eccentricity) / r.Length), 0);
                var result = TransferMath.Eject(mu, r, v, infinity, 6.5e6);
                Check(result.Valid, "Ejection must be finite");
                Near(result.After.LengthSquared - 2 * mu / result.Position.Length, infinity.LengthSquared, 0.02, "Ejection excess energy");
                var h = LandingVector.Cross(result.Position, result.After);
                var e = LandingVector.Cross(result.After, h) / mu - result.Position.Unit;
                var expected = e.Unit * (-1 / e.Length) + LandingVector.Cross(h.Unit, e.Unit) * Math.Sqrt(1 - 1 / e.LengthSquared);
                Near((expected.Unit - infinity.Unit).Length, 0, 1e-7, "Outgoing asymptote");
                var parking = new TransferOrbit(mu, 0, r, v);
                parking.State(result.Coast, out var rb, out var vb);
                Near((rb - result.Position).Length, 0, 0.02, "Burn occurs on parking orbit");
                Near((vb - result.Before).Length, 0, 1e-5, "Parking velocity at burn");
            }
    }

    public static void Selection()
    {
        var grid = new TransferGrid(new TransferRequest { Width = 2, Height = 2 });
        TransferSolution Cell(double time, double dv, double capture) => new TransferSolution(time, 100, new LandingVector(1, 0, 0), new LandingVector(dv, 0, 0), default, capture);
        grid.Cells[0] = Cell(10, 1000, 500); grid.Cells[1] = Cell(20, 800, 100); grid.Cells[2] = Cell(10, 1100, 100);
        Check(grid.Best(false) == 1 && grid.Best(true) == 1, "Lowest cost selection");
        Check(grid.Earliest(false, 1050) == 0, "Earliest departure under budget");
        Check(grid.Earliest(true, 1250) == 2, "Earliest considers capture and lower-cost equal-time alternatives");
        Check(grid.Earliest(true, 500) == -1, "Impossible budget cannot select a node");
        Check(grid.Best(true, 21) == -1, "Past transfers cannot be selected");
        grid.Cells[0] = Cell(10, 100, 5000);
        Check(grid.Best(false) == 0 && grid.Best(true) == 1, "Capture toggle must change optimization");
    }

    public static void EscapePreference()
    {
        var request = new TransferRequest { Width = 3, Height = 2, Earliest = 0, Latest = 100, MinimumDuration = 100, MaximumDuration = 200 };
        var grid = new TransferGrid(request);
        TransferSolution Cell(double departure, double duration, double dv, bool escape) => new(departure, duration,
            new LandingVector(7e6, 0, 0), new LandingVector(dv, 0, 0), default, 0, escape);
        grid.Cells[0] = Cell(10, 100, 100, true);
        grid.Cells[1] = Cell(40, 120, 2000, false);
        grid.Cells[2] = Cell(80, 180, 1500, false);
        Check(grid.Best(false) == 0 && grid.Pick(false, false, 2500, 0).Departure == 10,
            "Lowest delta-v ignores the actual cheaper feasible transfer");
        Check(grid.Earliest(false, 2500) == 1, "Early escape displaces an affordable bound transfer");
        Check(grid.Available(0, 0) && grid.Available(2, 0), "Manual selection blocks a valid escape or bound cell");
        Check(grid.Earliest(false, 500) == 0, "Necessary escape under the departure budget was rejected");
        grid.Cells[1] = grid.Cells[2] = default;
        Check(grid.Best(true) == 0 && grid.Available(0, 0), "Only possible escape is unavailable");
        grid.Cells[1] = Cell(5, 100, 2000, false);
        Check(grid.Best(false, 6) == 0 && grid.Available(0, 6) && !grid.Available(1, 6), "Expired bound window blocks the remaining escape");
        var excluded = new[] { new TransferWindowExclusion(10, 100, 1, 1) };
        Check(!grid.Available(0, 6, excluded) && grid.Best(false, 6, excluded) == -1 && grid.Earliest(false, 1000, 6, excluded) == -1,
            "A previously rejected window can be selected manually or automatically");

        const double mu = 3.986004418e14, radius = 7e6;
        var r = new LandingVector(radius, 0, 0);
        Check(!TransferMath.Escapes(mu, r, new LandingVector(0, Math.Sqrt(mu / radius), 0), 9.24e8), "Circular orbit is classified as escape");
        Check(TransferMath.Escapes(mu, r, new LandingVector(0, Math.Sqrt(2 * mu / radius) * 1.01, 0), 9.24e8), "Hyperbolic transfer is classified as bound");
        var crossing = new LandingVector(0, Math.Sqrt(mu * (2 / radius - 1 / 5e8)), 0);
        Check(TransferMath.Escapes(mu, r, crossing, 9.24e8), "An elliptic orbit leaving the SOI is classified as contained");
        Check(!TransferMath.Escapes(mu, r, crossing, double.PositiveInfinity), "A central star's unbounded SOI blocks an elliptic interplanetary transfer");
    }

    public static void ReturnAndReplanning()
    {
        const double mu = 3.986004418e14, radius = 7e6;
        var r = new LandingVector(radius, 0, 0);
        var v = new LandingVector(0, Math.Sqrt(mu * (2 / radius - 1 / 2e8)), 0);
        var transfer = new TransferOrbit(mu, 0, r, v);
        Check(!TransferMath.ReturnsBeforeEncounter(mu, r, v, transfer.Period * 0.4), "Departure at periapsis is mistaken for a return before a valid encounter");
        Check(TransferMath.ReturnsBeforeEncounter(mu, r, v, transfer.Period * 1.1), "A complete return before target arrival is accepted");
        transfer.State(transfer.Period * 0.75, out var inbound, out var inboundVelocity);
        Check(TransferMath.ReturnsBeforeEncounter(mu, inbound, inboundVelocity, transfer.Period * 0.3), "Inbound fallback before arrival is accepted");
        Check(!TransferMath.ReturnsBeforeEncounter(mu, r, new LandingVector(0, 12000, 0), 1e6), "A necessary hyperbolic departure is rejected as a bound return");
        var request = SampleRequest();
        var original = TransferMath.Search(request, CancellationToken.None);
        var selected = original.Cells[original.Best(true)];
        request.ExcludedWindows = new[] { new TransferWindowExclusion(selected.Departure, selected.Duration,
            (request.Latest - request.Earliest) / (request.Width - 1), (request.MaximumDuration - request.MinimumDuration) / (request.Height - 1)) };
        var replanned = TransferMath.Search(request, CancellationToken.None);
        Check(replanned.Best(true) >= 0, "Rejecting one unsafe window destroys every alternative");
        foreach (var cell in replanned.Cells)
            Check(!cell.Valid || !request.ExcludedWindows[0].Contains(cell.Departure, cell.Duration), "Requested replanning reuses a rejected transfer window");
        Check(new TransferWindowExclusion(100, 200, 5, 10).Contains(103, 207), "Nearby unsafe windows are not excluded");
        Check(!new TransferWindowExclusion(100, 200, 5, 10).Contains(106, 207), "An unrelated departure window is excluded");
        request.MinimumTransferApoapsis = 500e6;
        request.MinimumTransferPeriapsis = 6.9e6;
        var adjusted = TransferMath.Search(request, CancellationToken.None);
        Check(adjusted.Best(true) >= 0, "Correcting transfer apsides destroys all reachable alternatives");
        foreach (var cell in adjusted.Cells)
        {
            if (!cell.Valid) continue;
            request.Parking.State(cell.Departure, out var position, out var before);
            var after = before + cell.DeltaV;
            var orbit = new TransferOrbit(request.CentralMu, cell.Departure, position, after);
            Check(!orbit.Valid || orbit.Apoapsis >= request.MinimumTransferApoapsis, "Replanned bound transfer ignores the required apoapsis reach");
            var h = LandingVector.Cross(position, after);
            double eccentricity = (LandingVector.Cross(after, h) / request.CentralMu - position.Unit).Length;
            Check(h.LengthSquared / request.CentralMu / (1 + eccentricity) >= request.MinimumTransferPeriapsis,
                "Replanned transfer ignores the corrected periapsis limit");
        }
    }

    internal static TransferRequest SampleRequest()
    {
        double mu = 3.986004418e14, r1 = 7e6, r2 = 384.4e6;
        return new TransferRequest
        {
            Now = 0, CentralMu = mu, CentralSoi = 9.24e8,
            Parking = new TransferOrbit(mu, 0, new LandingVector(r1, 0, 0), new LandingVector(0, Math.Sqrt(mu / r1), 0)),
            Target = new TransferOrbit(mu, 0, new LandingVector(0, r2, 0), new LandingVector(-Math.Sqrt(mu / r2), 0, 0)),
            TargetMu = 4.9049e12, TargetRadius = 1.7374e6, PeriapsisAltitude = 100000, DepartureSafetyRadius = 6.5e6,
            Earliest = 300, Latest = 25000, MinimumDuration = 180000, MaximumDuration = 750000,
            Width = 24, Height = 24, IncludeCapture = true
        };
    }

    public static void BestEffortFallback()
    {
        var request = SampleRequest(); request.UseBestEffortFallback = true;
        var safe = TransferMath.Search(request, CancellationToken.None);
        Check(safe.Best(true) >= 0 && safe.Cells.All(cell => !cell.Valid || !cell.BestEffort), "Best-effort routes replace available non-returning transfers");
        request.ExcludedWindows = new[] { new TransferWindowExclusion((request.Earliest + request.Latest) / 2,
            (request.MinimumDuration + request.MaximumDuration) / 2, request.Latest - request.Earliest,
            request.MaximumDuration - request.MinimumDuration, true) };
        var progress = new List<int>();
        var fallback = TransferMath.Search(request, CancellationToken.None, value => progress.Add(value));
        int selected = fallback.Best(true);
        Check(selected >= 0 && fallback.Cells[selected].BestEffort && fallback.Available(selected, 0, request.ExcludedWindows),
            "No fallback can be selected after all non-returning native windows fail");
        Check(progress[^1] == 100 && progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second), "The fallback pass resets search progress and causes UI status to jump");
        request.ExcludedWindows = new[] { new TransferWindowExclusion((request.Earliest + request.Latest) / 2,
            (request.MinimumDuration + request.MaximumDuration) / 2, request.Latest - request.Earliest,
            request.MaximumDuration - request.MinimumDuration) };
        Check(TransferMath.Search(request, CancellationToken.None).Best(true) < 0, "Fallback ignores hard impact or arrival exclusions");
        request.ExcludedWindows = Array.Empty<TransferWindowExclusion>();
        request.MinimumTransferPeriapsis = 7.1e6;
        var apsisFallback = TransferMath.Search(request, CancellationToken.None);
        Check(apsisFallback.Best(true) >= 0 && apsisFallback.Cells[apsisFallback.Best(true)].BestEffort,
            "Impossible corrective apsis limits prevent the best available transfer from being offered");
        request.MinimumTransferPeriapsis = 0;
        request.Refine = (candidate, token) => candidate.BestEffort ? candidate : request.Reject("finite_burn_test");
        var refinedFallback = TransferMath.Search(request, CancellationToken.None);
        int refinedBest = refinedFallback.Best(true);
        Check(refinedBest >= 0 && refinedFallback.Cells[refinedBest].BestEffort && request.Rejections["finite_burn_test"] > 0,
            "Fallback is decided before finite-burn validation eliminates the strict candidates");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { TransferMath.Search(request, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "Cancelled fallback search still publishes a transfer");
    }

    public static void Search()
    {
        var request = SampleRequest(); int progress = 0;
        var grid = TransferMath.Search(request, CancellationToken.None, p => progress = p);
        int best = grid.Best(true); Check(best >= 0 && progress == 100, "Search did not complete with a useful transfer");
        var cell = grid.Cells[best]; request.Parking.State(cell.Departure, out var r, out var v);
        Check(!cell.EscapesCentral && v.Length > 0 && (v + cell.DeltaV).LengthSquared < 2 * request.CentralMu / r.Length,
            "Lunar search selected a central escape instead of an available bound transfer");
        // Independently integrate the transfer orbit with RK4 to check rendezvous with the moving target.
        var model = new LandingBodyModel { Mu = request.CentralMu, Radius = 1, Mass = 1 };
        v += cell.DeltaV;
        int steps = 18000; double dt = cell.Duration / steps;
        for (int i = 0; i < steps; i++) LandingMath.Step(model, ref r, ref v, dt);
        request.Target.State(cell.Departure + cell.Duration, out var arrival, out _);
        var excess = cell.ArrivalExcess;
        double periapsis = request.TargetRadius + request.PeriapsisAltitude;
        double impact = periapsis * Math.Sqrt(1 + 2 * request.TargetMu / (periapsis * excess.LengthSquared));
        Near((r - arrival).Length, impact, 3000, "Transfer must reach the target's capture corridor");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { TransferMath.Search(request, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Cancelled search continued");
        request.MinimumDuration = -1;
        bool rejected = false;
        try { TransferMath.Search(request, CancellationToken.None); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Invalid search bounds accepted");
        request.MinimumDuration = 60; request.TargetSoi = double.NaN;
        Check(!request.Valid, "Invalid target sphere of influence enters the worker");
    }

    public static void Interplanetary()
    {
        double sunMu = 1.32712440018e20, earthMu = 3.986004418e14, earthR = 1.495978707e11, marsR = 2.2794382e11;
        var request = new TransferRequest
        {
            Now = 0, CentralMu = sunMu,
            Parking = new TransferOrbit(earthMu, 0, new LandingVector(7e6, 0, 0), new LandingVector(0, Math.Sqrt(earthMu / 7e6), 0)),
            Source = new TransferOrbit(sunMu, 0, new LandingVector(earthR, 0, 0), new LandingVector(0, Math.Sqrt(sunMu / earthR), 0)),
            Target = new TransferOrbit(sunMu, 0, new LandingVector(marsR * Math.Cos(1), marsR * Math.Sin(1), 0),
                new LandingVector(-Math.Sqrt(sunMu / marsR) * Math.Sin(1), Math.Sqrt(sunMu / marsR) * Math.Cos(1), 0)),
            Earliest = 6000, Latest = 6e7, MinimumDuration = 1e7, MaximumDuration = 3e7,
            TargetMu = 4.2828e13, TargetRadius = 3.3895e6, PeriapsisAltitude = 150000,
            DepartureSafetyRadius = 6.5e6, DepartureSoi = 9.24e8, Width = 12, Height = 12
        };
        var grid = TransferMath.Search(request, CancellationToken.None);
        int best = grid.Best(true); Check(best >= 0, "No interplanetary windows found");
        var solution = grid.Cells[best];
        request.Parking.State(solution.Departure, out var rb, out var vb);
        var ejection = new TransferEjection(rb, vb, vb + solution.DeltaV, 0);
        Check(TransferMath.EscapeState(earthMu, ejection, request.DepartureSoi, out var re, out var ve, out double escapeTime), "No SOI escape");
        double exit = solution.Departure + escapeTime, arrival = solution.Departure + solution.Duration;
        request.Source.State(exit, out var rs, out var vs); request.Target.State(arrival, out var rt, out var vt);
        var excess = solution.ArrivalExcess;
        double periapsis = request.TargetRadius + request.PeriapsisAltitude;
        double impact = periapsis * Math.Sqrt(1 + 2 * request.TargetMu / (periapsis * excess.LengthSquared));
        var offset = LandingVector.Cross(excess, LandingVector.Cross(rt, vt)).Unit * impact;
        var lambert = TransferLambert.Solve(sunMu, rs + re, rt + offset, arrival - exit, TransferGeometry.Prograde, 0, LandingVector.Cross(rs, vs));
        Near((lambert.Vi - vs - ve).Length, 0, 0.03, "Patched-conic ejection matches heliocentric transfer");
        Check(solution.DepartureDv < 10000, "Earth/Mars departure should have a practical window");
        Console.WriteLine($"  interplanetary: departure Δv {solution.DepartureDv:F0} m/s, capture {solution.CaptureDv:F0} m/s, SOI coast {escapeTime / 86400:F2} d");
    }

    public static void Porkchop()
    {
        var request = SampleRequest();
        Near(TransferMath.DefaultDepartureWindow(request), 2 * request.Parking.Period, 1e-6, "Moon window must resolve individual orbital opportunities");
        request.Latest = request.Earliest + 5 * 86400;
        int samples = TransferMath.DepartureSamples(request);
        Check((request.Latest - request.Earliest) / (samples - 1) <= request.Parking.Period / 24,
            "Departure sampling aliases the parking orbit across a five-day moon window");
        var grid = new TransferGrid(new TransferRequest { Width = 3, Height = 3 });
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                grid.Cells[y * 3 + x] = new TransferSolution(100 + x, 100 + y, new LandingVector(1, 0, 0),
                    new LandingVector(1000 + x * 500, 0, 0), default, 0);
        var plot = new TransferPlotData(grid, false);
        Check(plot.Contours.Count > 0, "No contours extracted from a cost slope");
        foreach (var line in plot.Contours)
        {
            Near(line.X1, line.X2, 1e-6, "Contours of a horizontal cost slope must be vertical");
            Check(line.X1 >= 0 && line.X1 <= 1 && line.Y1 >= 0 && line.Y1 <= 1 && line.Y2 >= 0 && line.Y2 <= 1,
                "Contour escapes the plot bounds");
        }
        grid.Cells[4] = default;
        plot = new TransferPlotData(grid, false);
        Check(float.IsNaN(plot.Values[4]) && plot.Contours.Count > 0 && plot.DisplayValues.All(float.IsFinite)
            && !grid.Available(4, 0), "Filling display holes changes route validity or leaves interrupted color transitions");
        Array.Clear(grid.Cells);
        plot = new TransferPlotData(grid, false);
        Check(plot.DisplayValues.All(value => value == 1) && grid.Best(false) == -1,
            "An entirely invalid grid leaves holes or invents a selectable transfer");
    }
}
