using KITN;

internal static class ProximitySettingsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Persistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "KITN.ProximitySettings", Guid.NewGuid().ToString("N"), "layout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"version\":1,\"windows\":{},\"settings\":{\"allowRcs\":false}}");
        var store = new UiLayoutStore(path);
        Check(store.Load() && !store.AllowRcs && !store.Docking.EngineAssist && store.Docking.Matches(new DockingSettings())
            && store.Rendezvous.Matches(new RendezvousSettings()), "Old preferences do not migrate to proximity defaults");
        var rv = new RendezvousSettings { AutoWarp = false, CourseCorrections = false, FinalDistanceM = 220,
            MaximumPhasingOrbits = 3, MaximumClosingSpeedMps = 0.8f, BrakingMargin = 3, PositionToleranceM = 7,
            VelocityToleranceMps = 0.04f, SettleSeconds = 4, ReservePercent = 20, MaximumCorrections = 8, WarpLeadSeconds = 240 };
        var dock = new DockingSettings { EngineAssist = true, ShowGuidanceOverlay = true, MaximumSpeedMps = 0.4f, NearSpeedMps = 0.12f, ContactSpeedMps = 0.03f,
            AlignmentSpeedMps = 0.2f, SafetyMarginM = 35, OverrideStartDistance = true, StartDistanceM = 70,
            CorridorRadiusM = 0.15f, AlignmentToleranceDeg = 1, MaximumAngularRateDegS = 0.1f,
            MaximumTurnRateDegS = 0.7f, BrakingMargin = 2, ForceRoll = false, RollAngleDeg = 90 };
        Check(store.SetRendezvous(rv, 0) && store.SetDocking(dock, 0)
            && store.Flush(0, true) == UiLayoutSaveResult.Saved, "Settings fail to save");
        rv.FinalDistanceM = 900; dock.StartDistanceM = 300;
        var restarted = new UiLayoutStore(path);
        Check(restarted.Load() && restarted.Rendezvous.Matches(store.Rendezvous) && restarted.Docking.Matches(store.Docking)
            && restarted.Rendezvous.FinalDistanceM == 220 && restarted.Docking.StartDistanceM == 70 && restarted.Docking.ShowGuidanceOverlay
            && restarted.Docking.EngineAssist, "Settings are aliased or lost after restart");
        byte[] saved = File.ReadAllBytes(path);
        rv.MaximumClosingSpeedMps = float.NaN; dock.RollAngleDeg = float.PositiveInfinity;
        Check(!store.SetRendezvous(rv, 1) && !store.SetDocking(dock, 1) && store.Flush(1, true) == UiLayoutSaveResult.Unchanged
            && saved.SequenceEqual(File.ReadAllBytes(path)), "Invalid settings overwrite saved data");
        File.WriteAllText(path, "{\"version\":1,\"windows\":{},\"settings\":{\"docking\":{\"contactSpeedMps\":-1}}}");
        var damaged = new UiLayoutStore(path);
        Check(!damaged.Load() && !damaged.SetDocking(new DockingSettings(), 2), "Damaged preferences can be overwritten");
    }

    public static void ConfiguredControl()
    {
        var rv = new RendezvousSettings { MaximumClosingSpeedMps = 0.25f, BrakingMargin = 4 };
        var velocity = RendezvousMath.ClosingVelocity(new LandingVector(600, 0, 0), 0.02, 200,
            rv.MaximumClosingSpeedMps, rv.BrakingMargin);
        Check(velocity.X < 0 && velocity.Length <= 0.25, "Rendezvous ignores the configured approach speed");
        Check(!RendezvousMath.Arrived(205, 0.03, 200, 2, 0.04)
            && RendezvousMath.Arrived(201, 0.03, 200, 2, 0.04), "Configured arrival tolerances are ignored");
        var dock = new DockingSettings { MaximumSpeedMps = 0.15f, ContactSpeedMps = 0.025f, AlignmentSpeedMps = 0.1f };
        Check(DockingMath.Approach(new LandingVector(600, 0, 0), 0, 0, 0.02, 35, dock).DesiredVelocity.Length <= 0.150001,
            "Docking ignores maximum approach speed");
        Check(DockingMath.Approach(new LandingVector(2, 0, 0), 0, 0, 0.02, 35, dock).DesiredVelocity.Length <= 0.025001,
            "Docking ignores contact speed");
        dock.AlignmentToleranceDeg = 0.5f;
        Check(DockingMath.Approach(new LandingVector(2, 0, 0), 1, 0, 0.02, 35, dock).DesiredVelocity.Length == 0,
            "Docking ignores configured alignment tolerance");
    }

    public static void ResidualAlignment()
    {
        var settings = new DockingSettings();
        var partial = DockingMath.Approach(new LandingVector(55, 0.05, 0), 3, 0.5, 0.02, 45, settings,
            orientationError: 24);
        Check(partial.Phase == DockingPhase.Approaching && partial.DesiredVelocity.X < 0
            && partial.DesiredVelocity.Length < settings.NearSpeedMps, "Residual pointing/roll corrections block a safe slow approach");
        var contact = DockingMath.Approach(new LandingVector(2, 0, 0), 3, 0.1, 0.02, 45, settings);
        Check(contact.Phase == DockingPhase.Correcting && contact.DesiredVelocity.Length == 0,
            "Final approach accepts larger errors without evidence of native impulse limits");
        var jitter = DockingMath.Approach(new LandingVector(2, 0, 0), 2.3, 0.28, 0.02, 45, settings, closing: true);
        Check(jitter.Phase == DockingPhase.Docking && jitter.DesiredVelocity.X < 0, "Threshold jitter stops an established contact approach");
        Check(DockingMath.Approach(new LandingVector(2, 0, 0), 8, 0.2, 0.02, 45, settings, closing: true)
            .DesiredVelocity.Length == 0, "Hysteresis accepts a substantial final pointing error");
        Check(DockingMath.Approach(new LandingVector(2, 0, 0), 8, 0.2, 0.02, 45, settings, closing: true, nativeAngleWindow: 100)
            .DesiredVelocity.Length == 0, "Minimum-impulse allowance exceeds the safe final port-angle limit");
        Check(DockingMath.Approach(new LandingVector(2, 0, 0), 1, 4, 0.02, 45, settings, closing: true, nativeRateWindow: 100)
            .DesiredVelocity.Length == 0, "Minimum-impulse allowance accepts dangerous rotation");
        const double dt = 0.1, quantum = 0.004;
        var position = new LandingVector(55, 0.08, 0); var velocity = default(LandingVector);
        bool closing = false, reached = false;
        for (int step = 0; step < 50000; step++)
        {
            double time = step * dt;
            // Quantized native jets leave a small angular/rate cycle rather than exact zero.
            double angle = 2.8 + 0.55 * Math.Sin(time * 0.6);
            double roll = 22 * Math.Exp(-time / 20) + 0.5;
            double rate = 0.35 + 0.1 * Math.Sin(time * 0.8);
            var command = DockingMath.Approach(position, angle, rate, 0.02, 45, settings,
                orientationError: Math.Sqrt(angle * angle + roll * roll), closing: closing, nativeAngleWindow: 3.3, nativeRateWindow: 0.5);
            closing = command.Phase == DockingPhase.Approaching || command.Phase == DockingPhase.Docking;
            Check(command.Phase != DockingPhase.Retreating, "Small native angle oscillations trigger retreat to stand-off");
            if (step % 2 == 0)
            {
                var error = command.DesiredVelocity - velocity;
                velocity += new LandingVector(DockingMath.TranslationSign(error.X, quantum), DockingMath.TranslationSign(error.Y, quantum),
                    DockingMath.TranslationSign(error.Z, quantum)) * quantum;
            }
            position += velocity * dt;
            if (position.Length < 0.4 && velocity.Length < 0.15) { reached = true; break; }
        }
        Check(reached, "RCS approach stalls indefinitely with a few degrees of residual error");
    }

    public static void InPlaceRecovery()
    {
        var settings = new DockingSettings();
        var logged = DockingMath.Approach(new LandingVector(37.6, 0.38, 0), 0.31, 0.475, 0.02, 53, settings,
            orientationError: 2.64, closing: true, nativeAngleWindow: 8.68, nativeRateWindow: 1.172,
            relativeVelocity: new LandingVector(-0.3, 0, 0));
        Check(logged.Phase == DockingPhase.Correcting && logged.Issue == DockingIssue.Lateral
            && logged.DesiredVelocity.X < 0 && logged.DesiredVelocity.Y < -0.02,
            "Logged 38 m approach resets instead of correcting a 38 cm lateral offset while closing");
        var near = DockingMath.Approach(new LandingVector(2, 0.45, 0), 0, 0, 0.02, 53, settings);
        Check(near.Phase == DockingPhase.Correcting && near.DesiredVelocity.X == 0 && near.DesiredVelocity.Y < 0,
            "Correctable near-port offset retreats to the full alignment distance");
        var monitor = new DockingRecoveryMonitor();
        Check(!monitor.Observe(0, DockingIssue.Lateral, 1, 30)
            && !monitor.Observe(29, DockingIssue.Lateral, 1, 30), "Retry starts on a transient correction");
        Check(!monitor.Observe(29.5, DockingIssue.Lateral, 0.8, 30)
            && !monitor.Observe(55, DockingIssue.Lateral, 0.79, 30), "Improving correction is retried too early");
        Check(monitor.Observe(60, DockingIssue.Lateral, 0.8, 30), "An uncorrectable approach never retries after sustained stagnation");
        Check(!monitor.Observe(10, DockingIssue.Lateral, 0.8, 30), "Reverted simulation time immediately retries");
        monitor.Reset();
        Check(!monitor.Observe(100, DockingIssue.Orientation, 100, 430)
            && !monitor.Observe(200, DockingIssue.Orientation, 99, 430), "Slow large orientation corrections ignore their required turn time");
        const double dt = 0.1, quantum = 0.004;
        var position = new LandingVector(45, 0.1, 0); var velocity = default(LandingVector);
        bool injectedFar = false, injectedNear = false, contact = false;
        for (int step = 0; step < 30000; step++)
        {
            if (!injectedFar && position.X < 38) { position += new LandingVector(0, 0.4, 0); injectedFar = true; }
            if (!injectedNear && position.X < 5) { position += new LandingVector(0, 0.4, 0); injectedNear = true; }
            var command = DockingMath.Approach(position, 0.5, 0.3, 0.02, 53, settings,
                closing: true, nativeRateWindow: 0.5, relativeVelocity: velocity);
            Check(command.Phase != DockingPhase.Retreating, "Correctable drift resets the complete approach");
            if (step % 2 == 0)
            {
                var error = command.DesiredVelocity - velocity;
                velocity += new LandingVector(DockingMath.TranslationSign(error.X, quantum), DockingMath.TranslationSign(error.Y, quantum),
                    DockingMath.TranslationSign(error.Z, quantum)) * quantum;
            }
            position += velocity * dt;
            if (position.Length < 0.4 && velocity.Length < 0.15) { contact = true; break; }
        }
        Check(injectedFar && injectedNear && contact, "In-place lateral corrections do not resume and reach contact");
    }

    public static void BehindTarget()
    {
        const double dt = 0.1, quantum = 0.004, acceleration = 0.02, safe = 35, standOff = 45;
        foreach (var start in new[] { new LandingVector(-100, 0, 0), new LandingVector(-60, -8, 5), new LandingVector(-5, 80, 0) })
        {
            var position = start; var velocity = default(LandingVector); bool routing = true, reachedFront = false, contact = false, entryCleared = false;
            for (int step = 0; step < 40000; step++)
            {
                if (routing && position.X >= standOff - 0.5) { routing = false; reachedFront = true; }
                var command = DockingMath.Approach(position, 0, 0, acceleration, standOff, new DockingSettings(), routing, safe,
                    relativeVelocity: velocity, entryCleared: entryCleared);
                entryCleared = command.EntryCleared;
                if (step % 2 == 0)
                {
                    var error = command.DesiredVelocity - velocity;
                    velocity += new LandingVector(DockingMath.TranslationSign(error.X, quantum), DockingMath.TranslationSign(error.Y, quantum),
                        DockingMath.TranslationSign(error.Z, quantum)) * quantum;
                }
                position += velocity * dt;
                if (!reachedFront) Check(position.Length > safe - 0.5, "Rear-side approach crosses the target's clearance sphere");
                if (position.Length < 0.4 && velocity.Length < 0.15) { contact = true; break; }
            }
            Check(reachedFront && contact, "Rear-side docking cannot route to safe contact from " + start.Length);
        }
    }
}
