using KITN;

internal static class TransferNestedTests
{
    private static TransferOrbit Circle(double mu, double radius, double phase = 0, double inclination = 0)
    {
        double speed = Math.Sqrt(mu / radius);
        return new TransferOrbit(mu, 0, new LandingVector(radius * Math.Cos(phase), radius * Math.Sin(phase) * Math.Cos(inclination), radius * Math.Sin(phase) * Math.Sin(inclination)),
            new LandingVector(-speed * Math.Sin(phase), speed * Math.Cos(phase) * Math.Cos(inclination), speed * Math.Cos(phase) * Math.Sin(inclination)));
    }

    public static void DirectArrival()
    {
        const double solarMu = 1.32712440018e20, earthMu = 3.986004418e14, jupiterMu = 1.26686534e17;
        double duration = Math.PI * Math.Sqrt(Math.Pow((149.6e9 + 778.6e9) / 2, 3) / solarMu);
        var jupiter = new TransferBodySnapshot("Jupiter", Circle(solarMu, 778.6e9,
            Math.PI - Math.Sqrt(solarMu / Math.Pow(778.6e9, 3)) * duration), jupiterMu, 69911000, 48.2e9, 70e6);
        var himalia = new TransferBodySnapshot("Himalia", Circle(jupiterMu, 11.46e9, 1.1, 0.48), 4.5e8, 85000, 16e6, 85000);
        var request = new TransferRequest
        {
            Parking = Circle(earthMu, 7e6), Source = Circle(solarMu, 149.6e9), Target = jupiter.Orbit,
            CentralMu = solarMu, CentralSafetyRadius = 7e8, CentralSoi = double.PositiveInfinity,
            DepartureSafetyRadius = 6500000, DepartureSoi = 924e6,
            ArrivalPath = new[] { jupiter, himalia }, TargetMu = himalia.Mu, TargetRadius = himalia.Radius, TargetSoi = himalia.Soi,
            PeriapsisAltitude = 100000, Earliest = 6000, Latest = duration,
            MinimumDuration = duration * 0.7, MaximumDuration = duration * 1.5, Width = 8, Height = 8, IncludeCapture = true
        };
        var grid = TransferMath.Search(request, CancellationToken.None);
        var route = grid.Pick(true, false, double.PositiveInfinity, 0);
        foreach (var rejection in request.Rejections) Console.WriteLine($"  {rejection.Key}={rejection.Value}");
        Check(route.Valid, "Earth -> Himalia has no direct solution");
        Check(route.Arrival?.Encounters.Length == 2, "The result has no parent/final SOI sequence");
        var parent = route.Arrival.Encounters[0]; var final = route.Arrival.Encounters[1];
        Check(parent.Body.Id == "Jupiter" && final.Body.Id == "Himalia" && parent.Entry < final.Entry,
            "Capture is targeted at the planet instead of the selected moon");
        var planetCoast = new TransferCoast(jupiterMu, parent.Position, parent.Velocity);
        Check(planetCoast.Energy > 0, "Jupiter arrival secretly inserts a parking orbit");
        Check(planetCoast.State(final.Entry - parent.Entry, out var r, out var v), "Parent coast is invalid");
        Check(himalia.Orbit.State(final.Entry, out var moonR, out var moonV), "Moon ephemeris is invalid");
        Check((r - moonR - final.Position).Length < 0.1 && (v - moonV - final.Velocity).Length < 1e-6,
            "The route changes velocity at the intermediate planet");
        Check(Math.Abs(final.Periapsis - himalia.Radius - 100000) <= 30, "Himalia capture periapsis is wrong");
        Check(Math.Abs(final.Position.Length - himalia.Soi) < 0.1, "Final arrival bypasses the moon's SOI boundary");
        var approach = new TransferCoast(himalia.Mu, final.Position, final.Velocity);
        double capture = Math.Sqrt(2 * (approach.Energy + himalia.Mu / approach.Periapsis)) - Math.Sqrt(himalia.Mu / approach.Periapsis);
        Check(Math.Abs(route.CaptureDv - capture) < 1e-6, "Capture cost includes an intermediate planet burn");
        Console.WriteLine($"  Earth -> Jupiter SOI -> Himalia: departure {route.DepartureDv:F1} m/s, final capture {route.CaptureDv:F1} m/s, Pe {final.Periapsis - himalia.Radius:F1} m");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { TransferMath.Search(request, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "Nested search ignores cancellation");
        // Inner, heavier and retrograde moons need different planetary deflections.
        foreach (var satellite in new[]
        {
            new TransferBodySnapshot("Callisto", Circle(jupiterMu, 1.883e9, 0.9, 0.02), 7.179e12, 2410300, 37e6, 2410300),
            new TransferBodySnapshot("Europa", Circle(jupiterMu, 670.9e6, 1.5, 0.01), 3.202e12, 1560800, 10e6, 1560800),
            new TransferBodySnapshot("Retrograde moon", Circle(jupiterMu, 18e9, 1.4, 2.8), 3e8, 65000, 15e6, 65000)
        })
        {
            request.ArrivalPath = new[] { jupiter, satellite };
            request.TargetMu = satellite.Mu; request.TargetRadius = satellite.Radius; request.TargetSoi = satellite.Soi;
            grid = TransferMath.Search(request, CancellationToken.None);
            route = grid.Pick(true, false, double.PositiveInfinity, 0);
            Check(route.Valid, "Direct route fails for " + satellite.Id);
            final = route.Arrival.Encounters[1];
            Check(Math.Abs(final.Periapsis - satellite.Radius - 100000) <= 30, "Arrival altitude fails for " + satellite.Id);
            Console.WriteLine($"  Earth -> {satellite.Id}: departure {route.DepartureDv:F1} m/s, capture {route.CaptureDv:F1} m/s");
        }
        var marsDuration = Math.PI * Math.Sqrt(Math.Pow((149.6e9 + 227.9e9) / 2, 3) / solarMu);
        var mars = new TransferBodySnapshot("Mars", Circle(solarMu, 227.9e9,
            Math.PI - Math.Sqrt(solarMu / Math.Pow(227.9e9, 3)) * marsDuration), 4.2828e13, 3389500, 577e6, 3.6e6);
        var phobos = new TransferBodySnapshot("Phobos", Circle(mars.Mu, 9.376e6, 1.3, 0.02), 7.1e5, 11000, 16000, 11000);
        request.Target = mars.Orbit; request.ArrivalPath = new[] { mars, phobos };
        request.TargetMu = phobos.Mu; request.TargetRadius = phobos.Radius; request.TargetSoi = phobos.Soi;
        request.PeriapsisAltitude = 1000; request.Latest = marsDuration;
        request.MinimumDuration = marsDuration * 0.7; request.MaximumDuration = marsDuration * 1.5;
        grid = TransferMath.Search(request, CancellationToken.None); route = grid.Pick(true, false, double.PositiveInfinity, 0);
        Check(route.Valid && Math.Abs(route.Arrival.Encounters[1].Periapsis - 12000) <= 30,
            "Direct arrival at a small moon with a narrow SOI fails");
        Console.WriteLine($"  Earth -> Phobos: final Pe {route.Arrival.Encounters[1].Periapsis - phobos.Radius:F1} m");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
