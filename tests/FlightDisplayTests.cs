using KSAAutopilot;

internal static class FlightDisplayTests
{
    private const double Planet = 6371000, Mu = 3.9860187717e14;

    public static void SurfaceDistance()
    {
        var trace = new FlightTrack();
        trace.Begin(10, Planet, 0, 0, 0, Planet);
        trace.Sample(11, Planet + 10000, 0, 0, 10000, Planet);
        Near(trace.DistanceKm, 0, 1e-9);
        trace.Sample(12, 0, Planet + 20000, 0, 20000, Planet);
        Near(trace.DistanceKm, Math.PI * Planet / 2000, 1e-7);
        trace.Sample(13, -Planet, 0, 0, 0, Planet);
        Near(trace.DistanceKm, Math.PI * Planet / 1000, 1e-7);
        // Longitude wrapping and tiny motion remain measurable with atan2(cross, dot).
        var tiny = new FlightTrack();
        tiny.Begin(0, -Planet, 0, 0, 0, Planet);
        tiny.Sample(1, -Planet * Math.Cos(1e-8), -Planet * Math.Sin(1e-8), 0, 1, Planet);
        Near(tiny.DistanceKm, Planet * 1e-8 / 1000, 1e-12);
    }

    public static void TraceLifecycle()
    {
        var trace = new FlightTrack();
        trace.Begin(100, Planet, 0, 0, 40, Planet);
        trace.Sample(100, 0, Planet, 0, 500, Planet);
        Check(trace.Count == 1 && trace.DistanceKm == 0, "Paused physics added motion");
        trace.Sample(100.1, Planet, 1, 0, 100, Planet);
        Check(trace.Count == 1, "Sampling ignores simulation interval");
        trace.Sample(100.3, Planet, 2, 0, 200, Planet);
        Check(trace.Count == 2, "Sample at 0.25 seconds is missing");
        trace.Sample(110, Planet, 1000, 0, 300, Planet, true);
        Near(trace.ElapsedSeconds, 10, 1e-8);
        double distance = trace.DistanceKm;
        trace.Stop();
        trace.Sample(120, 0, Planet, 0, 0, Planet);
        Near(trace.DistanceKm, distance, 1e-9);
        Check(trace.Count == 3, "Stopped flight changed");
        trace.Begin(200, Planet, 0, 0, 40, Planet);
        trace.Sample(201, Planet, 100, 0, 1000, Planet);
        trace.Sample(199, 0, Planet, 0, 40, Planet);
        Check(trace.Count == 1 && trace.DistanceKm == 0 && trace.ElapsedSeconds == 0, "Reverted flight joined old positions");
        trace.Clear();
        Check(trace.Count == 0 && !trace.IsRecording, "Reset did not clear flight");
    }

    public static void BoundedTrace()
    {
        var trace = new FlightTrack(16);
        trace.Begin(0, Planet, 0, 0, 40, Planet);
        for (int i = 1; i <= 200; i++)
            trace.Sample(i, Planet * Math.Cos(i * 0.01), Planet * Math.Sin(i * 0.01), 0, i * 1000, Planet);
        Check(trace.Count <= 16 && trace.Count > 2, "Trace is unbounded");
        Near(trace.Distances[0], 0, 1e-9);
        Near(trace.Altitudes[0], 0.04, 1e-9);
        Near(trace.Distances[^1], Planet * 2 / 1000, 1e-7);
        for (int i = 1; i < trace.Count; i++) Check(trace.Distances[i] >= trace.Distances[i - 1], "Distance is not accumulated");
    }

    public static void InvalidSamples()
    {
        var trace = new FlightTrack();
        trace.Begin(0, Planet, 0, 0, 40, Planet);
        trace.Sample(1, double.NaN, 0, 0, 0, Planet);
        trace.Sample(1, 0, 0, 0, 0, Planet);
        trace.Sample(1, Planet, 10, 0, double.PositiveInfinity, Planet);
        trace.Sample(double.NaN, Planet, 10, 0, 10, Planet);
        trace.Sample(1, Planet, 10, 0, 10, double.PositiveInfinity);
        Check(trace.Count == 1 && trace.DistanceKm == 0, "Invalid sample changed the graph");
        trace.Sample(2, Planet, 100, 0, 1000, Planet);
        Check(double.IsFinite(trace.DistanceKm) && trace.Count == 2, "Valid sample cannot recover");
    }

    public static void PreviewProfile()
    {
        var profile = new LaunchProfile();
        var first = Preview(profile);
        Check(first.Distances.Length > 100 && first.Distances.Length <= 1802, "Prediction is missing or unbounded");
        Check(first.ReachedOrbit, $"Default rocket preview failed: {first.Message} AP {first.FinalApoapsisKm:F1} / PE {first.FinalPeriapsisKm:F1}");
        Check(first.Altitudes.All(double.IsFinite), "Prediction contains invalid altitude");
        for (int i = 1; i < first.Distances.Length; i++) Check(first.Distances[i] >= first.Distances[i - 1], "Preview distance is not accumulated");
        profile.TargetApoapsisKm = profile.TargetPeriapsisKm = 250;
        var higher = Preview(profile);
        Check(higher.Altitudes.Max() > first.Altitudes.Max() + 20, "Orbit target does not affect the route");
        profile = new LaunchProfile { GravityTurnEndM = 85000 };
        var later = Preview(profile);
        Check(Math.Abs(AtHeight(first, 40) - AtHeight(later, 40)) > 0.5, "Gravity turn does not affect the route");
        Console.WriteLine($"  preview: AP {first.FinalApoapsisKm:F2} / PE {first.FinalPeriapsisKm:F2} km; {first.Distances[^1]:F0} km distance; {first.Distances.Length} points");
    }

    public static void PreviewFuel()
    {
        var manual = RoutePrediction.Calculate(new LaunchProfile { AutoStaging = false },
            new[] { new RouteBurnPhase(19293340, 4553.8984, 908000, 5, true),
                new RouteBurnPhase(955522, 222.897, 46900, 100, true) },
            Mu, Planet, 40, 0, 408, 408, 1.225, 100800, 170, 0.2);
        Check(!manual.ReachedOrbit && manual.MessageKey == "preview.manual_stage", "Preview ignored manual staging");
        var weak = RoutePrediction.Calculate(new LaunchProfile(), new[] { new RouteBurnPhase(1e5, 1, 1e5, 10, true) },
            Mu, Planet, 40, 0, 408, 408, 1.225, 100800, 10, 0.2);
        Check(!weak.ReachedOrbit && weak.Distances.Length < 100, "Unflyable rocket produced an invented route");
        var invalid = Preview(new LaunchProfile { TargetApoapsisKm = float.NaN });
        Check(invalid.Distances.Length == 0, "Invalid target produced a prediction");
        var noFuel = RoutePrediction.Calculate(new LaunchProfile(), new[] { new RouteBurnPhase(1e6, 100, 1000, 20, true) },
            Mu, Planet, 40, 0, 408, 408, 1.225, 100800, 10, 0.2);
        Check(noFuel.Distances.Length == 0, "Invalid burn produced negative mass");
    }

    private static RoutePrediction Preview(LaunchProfile profile) => RoutePrediction.Calculate(profile,
        new[] { new RouteBurnPhase(19293340, 4553.8984, 908000, (908000 - 59000) / 4553.8984, true),
            new RouteBurnPhase(955522, 222.897, 46900, (46900 - 12000) / 222.897, true) },
        Mu, Planet, 40, 0, 408, 408, 1.225, 100800, 170, 0.2);
    private static double AtHeight(RoutePrediction p, double height)
    {
        for (int i = 0; i < p.Altitudes.Length; i++) if (p.Altitudes[i] >= height) return p.Distances[i];
        return 0;
    }
    private static void Near(double actual, double expected, double tolerance)
        => Check(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}");
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
