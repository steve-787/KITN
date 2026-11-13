using KITN;

internal static class AdaptivePlanningTests
{
    private const double Mu = 3.986004418e14, Planet = 6371000;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static RocketFlightModel LoggedRocket(double payload = 0, double thrustScale = 1)
    {
        const double firstMass = 751043.0625, coreMass = 357040, upperMass = 77050;
        return new RocketFlightModel
        {
            Mu = Mu, BodyRadius = Planet, Altitude = 40, TangentialSpeed = 408, RotationSpeed = 408,
            SeaLevelDensity = 1.225, AtmosphereHeight = 100800, DragArea = 142, MinimumThrottle = 0.2,
            AtmosphericThrustRatio = 17770000.0 / 19293340, AttitudeAccelerationDegS2 = 8,
            Phases = new[] {
                new RouteBurnPhase(19293340 * thrustScale, 4553.8984 * thrustScale, firstMass + payload, 83 / thrustScale, true),
                new RouteBurnPhase(6431113 * thrustScale, 1517.966 * thrustScale, coreMass + payload, (coreMass - 87667) / 1517.966 / thrustScale, false),
                new RouteBurnPhase(955522 * thrustScale, 222.897 * thrustScale, upperMass + payload, (upperMass - 3839) / 222.897 / thrustScale, true) }
        };
    }
    public static void RocketVariants()
    {
        foreach (var variant in new[] { ("logged rebuilt rocket", 0.0, 1.0), ("added 30 t payload", 30000.0, 1.0),
            ("25% lower thrust", 0.0, 0.75), ("lighter rocket", -1500.0, 1.0) })
        {
            RocketFlightModel model = LoggedRocket(variant.Item2, variant.Item3);
            var requested = FlightPlanSettings.Capture(new LaunchProfile());
            AdaptiveFlightPlan plan = AdaptiveFlightPlanner.Calculate(requested, model);
            Console.WriteLine(FormattableString.Invariant($"  {variant.Item1}: success={plan.Success}, AP={plan.Route.FinalApoapsisKm:F2}/PE={plan.Route.FinalPeriapsisKm:F2} km, turn={plan.Settings?.GravityTurnEndM:F0} m, reserve={plan.Route.RemainingDeltaV:F0} m/s, seconds={plan.Route.FlightSeconds:F1}, reason={plan.MessageKey}"));
            Check(plan.Success && plan.Route.ReachedOrbit && plan.Settings.IsValid, "Adaptive planning failed for " + variant.Item1 + ": " + plan.Message);
            Check(plan.Route.Distances.Length > 10 && plan.Route.Altitudes.All(double.IsFinite)
                && plan.Route.RemainingDeltaV > 0 && requested.Matches(new LaunchProfile()), "Planner loses its feasible route, fuel reserve or requested settings");
        }
    }
    public static void FailureReasons()
    {
        var settings = FlightPlanSettings.Capture(new LaunchProfile());
        RocketFlightModel model = LoggedRocket(); model.Phases = new[] { new RouteBurnPhase(100000, 100, 100000, 50, true) };
        AdaptiveFlightPlan weak = AdaptiveFlightPlanner.Calculate(settings, model);
        Check(!weak.Success && weak.MessageKey == "planning.low_twr" && weak.LiftoffTwr < 1, "A rocket that cannot lift off has no specific thrust error");
        model = LoggedRocket(); model.Phases = new[] { new RouteBurnPhase(2e6, 1000, 100000, 10, true) };
        AdaptiveFlightPlan empty = AdaptiveFlightPlanner.Calculate(settings, model);
        Check(!empty.Success && empty.MessageKey == "planning.low_fuel" && empty.AvailableDeltaV < empty.MinimumDeltaV,
            "Insufficient fuel has no quantitative error");
        model = LoggedRocket(); model.HasSteering = false;
        Check(AdaptiveFlightPlanner.Calculate(settings, model).MessageKey == "planning.no_steering", "Missing steering is accepted as a feasible rocket");
        model = LoggedRocket(); model.HasFlightComputer = false;
        Check(AdaptiveFlightPlanner.Calculate(settings, model).MessageKey == "planning.no_computer", "Missing control module has no actionable error");
        model = LoggedRocket(); model.Phases = Array.Empty<RouteBurnPhase>();
        Check(AdaptiveFlightPlanner.Calculate(settings, model).MessageKey == "planning.no_engines", "Missing stages are accepted");
        settings.AutoStaging = false;
        Check(AdaptiveFlightPlanner.Calculate(settings, LoggedRocket()).MessageKey == "planning.manual_stage", "Manual staging is silently treated as automatic");
    }
    public static void LoggedSafeCoast()
    {
        const double altitude = 163477.15076624695, apTime = 3256.252163348, pe = 158086.1442331886;
        const double radial = -16.62038096632935;
        Check(!OrbitInsertion.ReadyToBurn(altitude, apTime, 0.772, radial, pe, 100800),
            "The logged safe 163 km orbit incorrectly fires while AP is still 54 minutes away");
        Check(OrbitInsertion.ReadyToBurn(199428, 2, 0.772, 0.2, pe, 100800), "Safe low-orbit capture never ignites at the next AP");
        Check(OrbitInsertion.ReadyToBurn(altitude, apTime, 52, radial, -5000000, 100800),
            "A genuinely suborbital descending rocket waits for a future orbit and crashes");
    }
}
