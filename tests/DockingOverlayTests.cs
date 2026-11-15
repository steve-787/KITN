using KITN;

internal static class DockingOverlayTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Forecast()
    {
        var plan = new DockingGuidancePlan();
        var geometry = new DockingOverlayGeometry();
        var settings = new DockingSettings();
        foreach (var scenario in new[] { (new LandingVector(55, 0.08, 0), false, false),
            (new LandingVector(-100, 3, 0), true, false), (new LandingVector(37.6, 0.38, 0), false, false),
            (new LandingVector(38, 0.38, 0), false, true) })
        {
            var position = scenario.Item1;
            var command = DockingMath.Approach(position, 3, 0.5, 0.02, 45, settings, scenario.Item2, 20,
                24, true, 3, 0.6, default, scenario.Item3);
            plan.Build(position, default, 3, 24, 0.5, 0.02, 45, 20, settings, scenario.Item2, scenario.Item3, 3, 0.6, command);
            Check(plan.Count > 2 && plan.Count <= DockingGuidancePlan.MaximumPoints
                && plan.PredictionSteps <= DockingGuidancePlan.MaximumSteps && plan.Points[0].Equals(position), "Docking forecast loses its live origin or grows without bounds");
            Check(double.IsFinite(plan.EstimatedSeconds) && plan.EstimatedSeconds > 0
                && plan.Points[plan.Count - 1].Length == 0,
                $"Docking forecast never reaches the selected port: {scenario}, steps={plan.PredictionSteps}, endpoint={plan.Points[plan.Count - 1].Length:N2}");
            if (scenario.Item2)
                Check(plan.Current == DockingStep.Routing && (plan.Checkpoint - command.Waypoint).Length < 1e-8
                    && plan.Next == DockingStep.Alignment, "Rear route checkpoint differs from the actual avoidance waypoint");
            if (scenario.Item3) Check(plan.Current == DockingStep.Retreat && plan.Checkpoint.X == 45, "Retry overlay hides the commanded stand-off waypoint");
            geometry.Build(plan, position, 45, settings.CorridorRadiusM);
            Check(geometry.Count <= DockingOverlayGeometry.MaximumSegments && geometry.Count > 100
                && geometry.Segments.Take(geometry.Count).Any(s => s.Kind == DockingOverlayKind.Route && s.B.Length == 0)
                && geometry.Segments.Take(geometry.Count).Any(s => s.Kind == DockingOverlayKind.Checkpoint),
                "World geometry omits the route to the port/checkpoint or exceeds its draw budget");
            Console.WriteLine($"  {plan.Current}: ETA={plan.EstimatedSeconds:N1}s, points={plan.Count}, lines={geometry.Count}, steps={plan.PredictionSteps}");
        }
        var normalPosition = new LandingVector(55, 0.05, 0);
        var normal = DockingMath.Approach(normalPosition, 0, 0, 0.02, 45, settings);
        plan.Build(normalPosition, default, 0, 0, 0, 0.02, 45, 20, settings, false, false, 0, 0, normal);
        double fastEta = plan.EstimatedSeconds;
        settings.ContactSpeedMps = 0.01f;
        var slow = DockingMath.Approach(normalPosition, 0, 0, 0.02, 45, settings);
        plan.Build(normalPosition, default, 0, 0, 0, 0.02, 45, 20, settings, false, false, 0, 0, slow);
        Check(double.IsFinite(plan.EstimatedSeconds) && plan.EstimatedSeconds > fastEta * 2, "Docking ETA ignores slower contact settings");
        var aligningPosition = new LandingVector(45, 0, 0);
        var aligning = DockingMath.Approach(aligningPosition, 0, 0, 0.02, 45, settings, safeDistance: 20, orientationError: 60);
        plan.Build(aligningPosition, default, 0, 60, 0, 0.02, 45, 20, settings, false, false, 0, 0, aligning, 1);
        double strongTurnEta = plan.EstimatedSeconds;
        plan.Build(aligningPosition, default, 0, 60, 0, 0.02, 45, 20, settings, false, false, 0, 0, aligning, 0.005);
        Check(double.IsFinite(plan.EstimatedSeconds) && plan.EstimatedSeconds > strongTurnEta,
            "Docking ETA does not respond to weaker measured RCS turning authority");
        var points = plan.Points; var segments = geometry.Segments;
        // Warm up first so allocation measurement covers only repeat forecasting/geometry.
        geometry.Build(plan, normalPosition, 45, settings.CorridorRadiusM);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var benchmarkStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < 20; i++)
        {
            plan.Build(normalPosition, default, 0, 0, 0, 0.02, 45, 20, settings, false, false, 0, 0, slow);
            geometry.Build(plan, normalPosition, 45, settings.CorridorRadiusM);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == before && ReferenceEquals(points, plan.Points) && ReferenceEquals(segments, geometry.Segments),
            "Repeated docking preview creates new geometry buffers or garbage");
        Console.WriteLine($"  Forecast + geometry: {System.Diagnostics.Stopwatch.GetElapsedTime(benchmarkStart).TotalMilliseconds / 20:N3} ms/update; zero allocated bytes");
        plan.Holding(normalPosition); Check(double.IsNaN(plan.EstimatedSeconds) && plan.Current == DockingStep.Holding, "Control loss displays a misleading docking ETA");
        plan.Connecting(normalPosition); Check(plan.Current == DockingStep.Connecting && plan.Next == DockingStep.Complete, "Dock request does not show the confirmation step");
        plan.Clear(); geometry.Build(plan, normalPosition, 45, 0.3); Check(geometry.Count == 0, "Stopped docking leaves a stale world overlay");
    }
}
