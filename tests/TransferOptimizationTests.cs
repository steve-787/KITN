using KITN;

internal static class TransferOptimizationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Minimum()
    {
        var request = TransferTests.SampleRequest(); request.Width = request.Height = 12;
        var grid = TransferMath.Search(request, default);
        var coarse = grid.Cells[grid.Best(true)]; var refined = grid.Pick(true, false, double.PositiveInfinity, 0);
        Console.WriteLine($"  minimum: coarse={coarse.Cost(true):F3}, refined={refined.Cost(true):F3} m/s");
        Check(refined.Valid && refined.Cost(true) < coarse.Cost(true) - 0.1, "Minimum remains locked to a coarse sample");
        Check(refined.Departure >= request.Earliest && refined.Departure <= request.Latest
            && refined.Duration >= request.MinimumDuration && refined.Duration <= request.MaximumDuration, "Refinement leaves the chosen window");
        Check(Math.Abs(new TransferPlotData(grid, true).Minimum - refined.Cost(true)) < 1e-6,
            "Legend omits optimized minimum");
    }

    public static void Oberth()
    {
        var request = TransferTests.SampleRequest(); request.Oberth = true;
        request.Parking = new TransferOrbit(request.CentralMu, 0, new LandingVector(7e6, 0, 0),
            new LandingVector(0, Math.Sqrt(request.CentralMu * 1.55 / 7e6), 0));
        request.Latest = request.Parking.Period * 3;
        var grid = TransferMath.Search(request, default); var best = grid.Pick(true, false, double.PositiveInfinity, 0);
        if (!best.Valid) throw new Exception("No valid Earth periapsis transfer found");
        foreach (var solution in grid.Cells.Concat(grid.Candidates).Where(candidate => candidate.Valid))
        {
            request.Parking.State(solution.Departure, out var position, out _);
            if (Math.Abs(position.Length - request.Parking.Periapsis) > 0.1) throw new Exception("Oberth selection creates a burn away from periapsis");
        }
        request.Parking.State(request.Parking.Period / 2, out _, out var apoVelocity);
        request.Parking.State(request.Parking.Period, out _, out var periVelocity);
        if (!(periVelocity.Length * 100 + 5000 > apoVelocity.Length * 100 + 5000)) throw new Exception("Oberth energy gain does not follow periapsis speed");
        request.Earliest = request.Parking.Period * 0.2; request.Latest = request.Parking.Period * 0.3;
        if (TransferMath.Search(request, default).Pick(true, false, double.PositiveInfinity, 0).Valid)
            throw new Exception("Oberth search invents a periapsis outside the configured window");
        Console.WriteLine($"  Oberth: {best.Cost(true):F0} m/s, periapsis departure at {best.Departure:F1}s");
    }

    public static void GravityAssist()
    {
        const double mu = 1.3271244e20, au = 149597870700, day = 86400;
        TransferOrbit Circle(double radius, double phase) => new(mu, 0,
            new LandingVector(radius * Math.Cos(phase), radius * Math.Sin(phase), 0),
            new LandingVector(-Math.Sqrt(mu / radius) * Math.Sin(phase), Math.Sqrt(mu / radius) * Math.Cos(phase), 0));
        var request = new TransferRequest { CentralMu = mu, CentralSafetyRadius = 7e8, DepartureSafetyRadius = 7e8,
            CentralSoi = double.PositiveInfinity, Parking = Circle(au, 0), Target = Circle(1.52 * au, Math.PI / 2),
            TargetMu = 4.2828e13, TargetRadius = 3390000, TargetSoi = 5.77e8, PeriapsisAltitude = 200000,
            Earliest = 300, Latest = 400 * day, MinimumDuration = 100 * day, MaximumDuration = 800 * day,
            Width = 32, Height = 24, GravityAssists = true, IncludeCapture = true };
        {
            request.AssistBodies = new[] { new TransferBodySnapshot("Venus", Circle(0.723 * au, Math.PI / 2),
                3.24859e14, 6051800, 6.16e8, 6251800) };
            var grid = new TransferGrid(request); TransferGravityAssist.Search(grid, default, null);
            Console.WriteLine($"  Venus assist: candidates={grid.Candidates.Count}");
            Check(grid.Candidates.Count > 0, "Gravity assist search never finds a physically feasible flyby");
            var candidate = grid.Pick(true, false, double.PositiveInfinity, 0);
            Check(candidate.Flyby != null && candidate.Flyby.Periapsis > request.AssistBodies[0].SafetyRadius + 1000,
                "Chosen assist intersects atmosphere or terrain");
            var disabled = new TransferGrid(request); request.GravityAssists = false;
            TransferGravityAssist.Search(disabled, default, null);
            Check(disabled.Candidates.Count == 0, "Disabled gravity assists still enter the search");
            request.GravityAssists = true;
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { TransferGravityAssist.Search(new TransferGrid(request), stop.Token, null); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Flyby search ignores cancellation");
            var incoming = new LandingVector(3000, 0, 0); var outgoing = new LandingVector(1500, 1500 * Math.Sqrt(3), 0);
            double pe = TransferGravityAssist.RequiredPeriapsis(3.24859e14, incoming, outgoing);
            Check(Math.Abs(pe - 3.24859e14 / 9e6) < 0.1, "Flyby turning angle ignores hyperbolic excess energy");
            request.AssistBodies = new[] { new TransferBodySnapshot("Unsafe Venus", request.AssistBodies[0].Orbit,
                3.24859e14, 6051800, 6.16e8, 4e8) };
            var unsafeGrid = new TransferGrid(request); TransferGravityAssist.Search(unsafeGrid, default, null);
            Check(unsafeGrid.Candidates.Count == 0, "Unsafe flyby periapsides enter the candidate list");
        }
    }

    public static void OberthPorkchop()
    {
        var request = TransferTests.SampleRequest(); request.Oberth = true; request.Width = request.Height = 24;
        request.Parking = new TransferOrbit(request.CentralMu, 0, new LandingVector(7e6, 0, 0),
            new LandingVector(0, Math.Sqrt(request.CentralMu * 1.55 / 7e6), 0));
        request.Latest = request.Parking.Period * 3;
        var overview = request.CopyForPorkchop();
        Check(!overview.Oberth && request.Oberth && overview.Valid, "Plot copy changes the constrained search or drops its bounds");
        request.Reject("constrained"); overview.Reject("overview");
        Check(!request.Rejections.ContainsKey("overview") && !overview.Rejections.ContainsKey("constrained"), "Search diagnostics are shared across grids");
        int lastProgress = 0;
        var result = TransferMath.SearchWithPorkchop(request, overview, default, progress =>
        {
            Check(progress >= lastProgress && progress <= 100, "Combined search progress jumps backward");
            lastProgress = progress;
        });
        var normal = TransferMath.Search(overview.CopyForPorkchop(), default);
        Check(lastProgress == 100 && result.PorkchopGrid != null, "Oberth search does not complete its cached overview");
        for (int i = 0; i < normal.Cells.Length; i++)
        {
            var a = result.PorkchopGrid.Cells[i]; var b = normal.Cells[i];
            Check(a.Valid == b.Valid && (!a.Valid || Math.Abs(a.Cost(true) - b.Cost(true)) < 1e-6),
                "Switching from Oberth differs from an independently calculated normal Porkchop");
        }
        Check(result.PorkchopGrid.Cells.Any(cell => cell.Valid
            && overview.Parking.State(cell.Departure, out var position, out _)
            && Math.Abs(position.Length - overview.Parking.Periapsis) > 100), "Overview still repeats snapped periapsis columns");
        foreach (var cell in result.Cells.Concat(result.Candidates).Where(cell => cell.Valid))
        {
            request.Parking.State(cell.Departure, out var position, out _);
            Check(Math.Abs(position.Length - request.Parking.Periapsis) < 0.1, "Overview replaces the selected Oberth calculation");
        }
        using var stop = new CancellationTokenSource(); bool cancelled = false;
        try { TransferMath.SearchWithPorkchop(request, overview, stop.Token, progress => { if (progress >= 50) stop.Cancel(); }); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Cancelling between the constrained result and the plot search is ignored");
    }
}
