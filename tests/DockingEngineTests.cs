using KITN;

internal static class DockingEngineTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Safety()
    {
        var settings = new DockingSettings { EngineAssist = true };
        var command = new DockingCommand(DockingPhase.Approaching, new LandingVector(-0.3, 0, 0), entryCleared: true);
        var engine = new DockingEngineData(new LandingVector(-0.4, 0, 0), 0, 0.1, 0.05);
        bool Plan(DockingEngineData data, DockingCommand cmd, LandingVector pos, LandingVector vel, double angle = 0.1, double rate = 0.01,
            double rcs = 0.02, double dt = 0.02)
            => DockingEngineMath.Plan(cmd, pos, vel, angle, rate, rcs, 20, dt, settings, data, out _, out _);
        var position = new LandingVector(90, 0.01, 0); var velocity = new LandingVector(-0.1, 0, 0);
        Check(Plan(engine, command, position, velocity), "Small safe aft-engine impulse is rejected");
        settings.EngineAssist = false;
        Check(!Plan(engine, command, position, velocity), "Engine assist fires by default/while disabled"); settings.EngineAssist = true;
        foreach (var bad in new[] { new DockingEngineData(new LandingVector(-10, 0, 0), 0, 0.1, 0.05),
            new DockingEngineData(new LandingVector(0.1, 0, 0), 0, 0.1, 0.05),
            new DockingEngineData(new LandingVector(-0.4, 0, 0), 0.1, 0.1, 0.05),
            new DockingEngineData(new LandingVector(-0.4, 0, 0), 0, 0.1, 1),
            new DockingEngineData(new LandingVector(-0.4, 1, 0), 0, 0.1, 0.05) })
            Check(!Plan(bad, command, position, velocity), "Strong, reversed, unbalanced, long-burn or lateral engine is allowed");
        Check(!Plan(engine, command, position, velocity, angle: 2) && !Plan(engine, command, position, velocity, rate: 1)
            && !Plan(engine, command, position, velocity, dt: 0.2) && !Plan(engine, command, position, velocity, rcs: 0.0001),
            "Engine ignites before alignment, during excessive rotation, at warp or without braking room");
        Check(!Plan(engine, command, new LandingVector(25, 0, 0), velocity)
            && !Plan(engine, new DockingCommand(DockingPhase.Routing, command.DesiredVelocity), position, velocity)
            && !Plan(engine, new DockingCommand(DockingPhase.Docking, command.DesiredVelocity, entryCleared: true), position, velocity)
            && !Plan(engine, command, position, new LandingVector(-0.29, 0, 0))
            && !Plan(engine, command, new LandingVector(90, 0.2, 0), new LandingVector(-0.1, 0.01, 0)),
            "Engine fires during routing/final contact, beyond the speed target or with unsafe lateral drift");
        var port = new LandingVector(3, 0, 0); var forward = new LandingVector(1, 0, 0);
        var balanced = new DockingEngineGeometry(port, forward);
        balanced.Add(new LandingVector(-3, 1, 0), forward, 100); balanced.Add(new LandingVector(-3, -1, 0), forward, 100);
        Check(balanced.Valid && balanced.Count == 2 && balanced.Force.X == 200 && balanced.Torque.Length < 1e-9,
            "Balanced aft cluster is not recognised from its actual nozzles");
        foreach (var layout in new[] { (new LandingVector(1, 0, 0), forward), (new LandingVector(-3, 0, 0), -forward),
            (new LandingVector(-3, 0, 0), new LandingVector(0, 1, 0)) })
        {
            var bad = new DockingEngineGeometry(port, forward); bad.Add(layout.Item1, layout.Item2, 100);
            Check(!bad.Valid, "Front/side/reversed engine is considered a safe rear engine");
        }
        var sidePort = new DockingEngineGeometry(new LandingVector(0, 3, 0), new LandingVector(0, 1, 0));
        sidePort.Add(new LandingVector(-3, 0, 0), forward, 100);
        Check(!sidePort.Valid, "Bottom engine pushes a side-mounted docking port in the wrong direction");
        // Replay native minimum-duration cutoff, including two delayed guidance frames.
        foreach (double dt in new[] { 0.01, 0.02, 0.05, 0.1 })
        {
            if (!DockingEngineMath.Plan(command, position, velocity, 0.1, 0.01, 0.02, 20, dt, settings, engine, out double bound, out _)) continue;
            double actual = engine.Acceleration.Length * (Math.Max(engine.MinimumPulse, dt) + 2 * dt);
            Check(actual <= bound && -velocity.X + actual <= 0.3 && bound <= 0.15, "Delayed cutoff exceeds the promised small impulse or approach speed");
        }
    }
}
