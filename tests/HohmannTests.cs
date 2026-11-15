using KITN;

internal static class HohmannTests
{
    private const double Mu = 3.986004418e14;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double actual, double expected, double tolerance, string message)
        => Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} != {expected}");
    private static TransferOrbit Circle(double radius, double phase, bool retrograde = false)
    {
        double speed = Math.Sqrt(Mu / radius) * (retrograde ? -1 : 1);
        return new TransferOrbit(Mu, 0, new LandingVector(radius * Math.Cos(phase), radius * Math.Sin(phase), 0),
            new LandingVector(-speed * Math.Sin(phase), speed * Math.Cos(phase), 0));
    }
    private static TransferRequest Request(double r1 = 7e6, double r2 = 42.164e6, bool retrograde = false)
        => new TransferRequest { Now = 100, CentralMu = Mu, Parking = Circle(r1, 0.4, retrograde),
            Target = Circle(r2, 2.2, retrograde), TargetMu = 1e-12, TargetRadius = 0.01,
            PeriapsisAltitude = 0.01, DepartureSafetyRadius = 6.371e6, CentralSafetyRadius = 6.371e6 };

    public static void Calculations()
    {
        var outward = TransferHohmann.Parameters(Mu, 7e6, 42.164e6);
        Near(outward.DepartureDv, 2336.8, 0.1, "LEO to GEO injection benchmark");
        Near(outward.ArrivalDv, 1433.9, 0.1, "LEO to GEO circularization benchmark");
        Near(outward.Duration, 19178.15, 0.1, "LEO to GEO coast benchmark");
        var inward = TransferHohmann.Parameters(Mu, 42.164e6, 7e6);
        Near(inward.DepartureDv, -outward.ArrivalDv, 1e-9, "Inward injection must be retrograde");
        Near(inward.ArrivalDv, -outward.DepartureDv, 1e-9, "Inward arrival must be retrograde");
        foreach (double bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false;
            try { TransferHohmann.Parameters(Mu, bad, 42.164e6); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid orbit radius accepted");
        }
        foreach (bool retrograde in new[] { false, true })
        foreach (bool inwardTransfer in new[] { false, true })
        {
            var request = inwardTransfer ? Request(42.164e6, 7e6, retrograde) : Request(retrograde: retrograde);
            Check(TransferHohmann.Configure(request, new HohmannSettings()), "Circular transfer cannot be configured");
            double departure = TransferHohmann.NextDeparture(request, request.Earliest);
            Check(departure >= request.Earliest && departure <= request.Latest, "Phase window violates departure bounds");
            double duration = (request.MinimumDuration + request.MaximumDuration) / 2;
            request.Parking.State(departure, out var r1, out var v1);
            request.Target.State(departure + duration, out var r2, out _);
            Near((r1.Unit + r2.Unit).Length, 0, 1e-10, "Target is not at the opposite apsis on arrival");
            var nominal = TransferHohmann.Parameters(Mu, request.Parking.A, request.Target.A);
            var coast = new TransferCoast(Mu, r1, v1 + v1.Unit * nominal.DepartureDv);
            Check(coast.State(duration, out var arrival, out _), "Hohmann coast propagation failed");
            Near((arrival - r2).Length, 0, 0.05, "Analytical burn misses the moving target");
        }
        var ejection = Request(); ejection.Source = Circle(20e6, 0.3);
        Check(TransferHohmann.Configure(ejection, new HohmannSettings()), "Ejection geometry rejected");
        Near((ejection.MinimumDuration + ejection.MaximumDuration) / 2,
            TransferHohmann.Parameters(Mu, 20e6, 42.164e6).Duration, 1e-8, "Ejection timing uses parking orbit instead of source body");
        var equal = Request(7e6, 7e6);
        Check(!TransferHohmann.Configure(equal, new HohmannSettings()), "Co-orbital geometry accepted as Hohmann");
        var opposite = Request(); opposite.Target = Circle(42.164e6, 2.2, true);
        Check(!TransferHohmann.Configure(opposite, new HohmannSettings()), "Counter-rotating geometry accepted as Hohmann");
    }

    public static void Search()
    {
        var request = Request();
        var options = new HohmannSettings { DepartureLeadSeconds = 600, SynodicWindows = 2, DurationTolerancePercent = 5, IncludeCapture = false };
        Check(TransferHohmann.Configure(request, options), "Settings do not configure a valid search");
        Check(request.Hohmann && !request.GravityAssists && !request.Oberth && !request.UseBestEffortFallback && !request.IncludeCapture,
            "Hohmann search enables an unrelated route or unsafe fallback");
        request.Width = 9; request.Height = 5;
        var grid = TransferMath.Search(request, CancellationToken.None);
        var best = grid.Pick(false, false, double.PositiveInfinity, request.Earliest);
        Check(best.Valid && !best.BestEffort && best.Flyby == null, "No safe direct transfer found");
        Near(best.DepartureDv, 2336.8, 0.1, "Hohmann search misses the analytic minimum between grid cells");
        Check(best.Departure >= request.Earliest && best.Departure <= request.Latest
            && best.Duration >= request.MinimumDuration && best.Duration <= request.MaximumDuration, "Search refinement escapes configured bounds");
        request.Parking.State(best.Departure, out _, out var v);
        var coast = new TransferCoast(Mu, best.Position, v + best.DeltaV);
        Check(coast.State(best.Duration, out var endpoint, out _), "Selected transfer fails propagation");
        request.Target.State(best.ArrivalTime, out var target, out _);
        Near((endpoint - target).Length, 0, 1, "Selected transfer misses the moving target");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { TransferMath.Search(request, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Hohmann search ignores cancellation");
    }

    public static void Persistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "KITN.Hohmann", Guid.NewGuid().ToString("N"), "ui_layout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"version\":1,\"windows\":{},\"settings\":{\"allowRcs\":false}}");
        var store = new UiLayoutStore(path);
        Check(store.Load() && store.Hohmann.Matches(new HohmannSettings()) && !store.AllowRcs, "Old settings fail to migrate");
        var settings = new HohmannSettings { DepartureLeadSeconds = 900, SynodicWindows = 3, DurationTolerancePercent = 35,
            IncludeCapture = false, AutoWarp = false };
        Check(store.SetHohmann(settings, 0) && store.Flush(0, true) == UiLayoutSaveResult.Saved, "Settings fail to save");
        settings.DepartureLeadSeconds = 1800;
        var restarted = new UiLayoutStore(path);
        Check(restarted.Load() && restarted.Hohmann.Matches(store.Hohmann) && restarted.Hohmann.DepartureLeadSeconds == 900
            && !restarted.AllowRcs, "Settings are aliased or lost after restart");
        byte[] saved = File.ReadAllBytes(path);
        settings.DurationTolerancePercent = float.NaN;
        Check(!store.SetHohmann(settings, 1) && store.Flush(1, true) == UiLayoutSaveResult.Unchanged
            && saved.SequenceEqual(File.ReadAllBytes(path)), "Invalid settings overwrite saved data");
        File.WriteAllText(path, "{\"version\":1,\"windows\":{},\"settings\":{\"hohmann\":{\"synodicWindows\":0}}}");
        saved = File.ReadAllBytes(path);
        var damaged = new UiLayoutStore(path);
        Check(!damaged.Load() && !damaged.SetHohmann(new HohmannSettings(), 2)
            && damaged.Flush(2, true) == UiLayoutSaveResult.Unchanged && saved.SequenceEqual(File.ReadAllBytes(path)), "Damaged preferences overwritten");
    }
}
