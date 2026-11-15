using KITN;

internal static class RendezvousTests
{
    const double Mu = 3.986004418e14;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static TransferOrbit Circle(double radius, double phase, double inclination = 0)
    {
        double speed = Math.Sqrt(Mu / radius);
        return new TransferOrbit(Mu, 0,
            new LandingVector(radius * Math.Cos(phase), radius * Math.Sin(phase) * Math.Cos(inclination), radius * Math.Sin(phase) * Math.Sin(inclination)),
            new LandingVector(-speed * Math.Sin(phase), speed * Math.Cos(phase) * Math.Cos(inclination), speed * Math.Cos(phase) * Math.Sin(inclination)));
    }
    static RendezvousRequest Request(double radius, double phase, double inclination = 0) => new()
    {
        Own = Circle(7e6, 0), Target = Circle(radius, phase, inclination), Mu = Mu,
        Now = 0, SafetyRadius = 6471800, Soi = 924e6, StandOff = 500, Budget = 20000, Close = false, PredictBurn = null
    };
    public static void Transfers()
    {
        foreach (var scenario in new[] { (7e6, 0.5, 0.0), (7.2e6, 1.0, 0.0), (7.4e6, 0.8, 0.2), (7e6, 1.5, Math.PI), (7e6, 0.0, 0.0) })
        {
            var request = Request(scenario.Item1, scenario.Item2, scenario.Item3);
            var solution = RendezvousMath.Search(request, CancellationToken.None);
            Check(solution.Valid, "No rendezvous for " + scenario);
            request.Own.State(solution.Departure, out var r, out var v);
            var coast = new TransferCoast(Mu, r, v + solution.DepartureDelta);
            Check(coast.State(solution.Arrival - solution.Departure, out var arrivedR, out var arrivedV), "Transfer propagation failed");
            RendezvousMath.Aim(request.Target, solution.Arrival, 500, out var targetR, out var targetV);
            Check((arrivedR - targetR).Length < 0.1, $"Target miss {(arrivedR - targetR).Length} m for {scenario}");
            Check((arrivedV + solution.ArrivalDelta - targetV).Length < 0.001, "Arrival does not match target velocity");
            Check(solution.Departure >= 240 && solution.Cost < request.Budget, "Unsafe departure or unavailable fuel");
            if (scenario.Item2 == 0.5) Check(solution.Cost < 500, "Same-sized orbit phasing spends excessive delta-v");
            Console.WriteLine($"  {scenario}: delta-v={solution.Cost:F2} m/s, arrival={solution.Arrival:F1} s, revolutions={solution.Revolutions}");
        }
    }
    public static void Guards()
    {
        var request = Request(7e6, 0.5); request.Budget = 0.001;
        Check(!RendezvousMath.Search(request, CancellationToken.None).Valid, "Unavailable delta-v accepted");
        Check(double.IsFinite(request.MinimumSafeCost) && request.MinimumSafeCost > request.Budget,
            "Insufficient fuel loses the safe route cost needed for a specific missing-delta-v error");
        request.Budget = 20000;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool observed = false;
        try { RendezvousMath.Search(request, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
        Check(observed, "Cancellation ignored");
        request.AlignmentLead = double.NaN; Check(!request.Valid, "Invalid alignment lead accepted");
        request = Request(7e6, 0.5);
        Check(!RendezvousMath.Correct(request, 300, CancellationToken.None).Valid, "Correction accepts an encounter before alignment and braking can complete");
        request.Budget = 0.001;
        Check(!RendezvousMath.Correct(request, 5000, CancellationToken.None).Valid, "Correction ignores the remaining fuel budget");
        observed = false;
        try { RendezvousMath.Correct(request, 5000, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
        Check(observed, "Course correction ignores cancellation");
        Check(!RendezvousMath.SafeCoast(Mu, new LandingVector(7e6, 0, 0), new LandingVector(0, 3000, 0), 5000, 6471800, 924e6), "Impact coast accepted");
        Check(!RendezvousMath.SafeCoast(Mu, new LandingVector(7e6, 0, 0), new LandingVector(0, 11000, 0), 100000, 6471800, 8e6), "SOI escape accepted");
        Check(!RendezvousMath.Arrived(100, 1, 100) && !RendezvousMath.Arrived(0, 0, 100), "Collision or unbraked arrival marked complete");
        Check(!RendezvousMath.Arrived(100, 0.05, 100), "Rendezvous completes before velocity matching finishes");
    }
    public static void Approaches()
    {
        foreach (var scenario in new[] { (500.0, 0.0), (1500.0, -1.0), (350.0, 0.5), (70.0, 0.0) })
        {
            const double dt = 0.1, quantum = 0.004, accel = 0.01;
            var targetR = new LandingVector(7e6, 0, 0);
            var targetV = new LandingVector(0, Math.Sqrt(Mu / 7e6), 0);
            var ownR = targetR + new LandingVector(scenario.Item1, 50, 0);
            var ownV = targetV + new LandingVector(scenario.Item2, 0.1, 0.05);
            int stable = 0; bool arrived = false;
            LandingVector Gravity(LandingVector r) => r * (-Mu / Math.Pow(r.Length, 3));
            for (int step = 0; step < 60000; step++)
            {
                var relative = ownR - targetR; var velocity = ownV - targetV;
                Check(relative.Length > 30, "Local rendezvous collided with target");
                var desired = RendezvousMath.ClosingVelocity(relative, accel, 100);
                if (step % 2 == 0)
                {
                    var error = desired - velocity;
                    ownV += new LandingVector(DockingMath.TranslationSign(error.X, quantum), DockingMath.TranslationSign(error.Y, quantum),
                        DockingMath.TranslationSign(error.Z, quantum)) * quantum;
                }
                // Independent velocity-Verlet orbit integration, with discrete RCS impulses.
                var ga = Gravity(ownR); var gb = Gravity(targetR);
                ownR += ownV * dt + ga * (dt * dt / 2); targetR += targetV * dt + gb * (dt * dt / 2);
                ownV += (ga + Gravity(ownR)) * (dt / 2); targetV += (gb + Gravity(targetR)) * (dt / 2);
                stable = RendezvousMath.Arrived((ownR - targetR).Length, (ownV - targetV).Length, 100) ? stable + 1 : 0;
                if (stable >= 20) { arrived = true; break; }
            }
            Check(arrived, $"Local rendezvous failed for {scenario}: distance={(ownR - targetR).Length:F2}, speed={(ownV - targetV).Length:F3}");
        }
    }
}
