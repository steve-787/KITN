using KITN;

internal static class DockingClearanceTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Envelope()
    {
        const double dt = 0.05, radius = 25, standOff = 35, acceleration = 0.2, quantum = 0.04;
        var settings = new DockingSettings();
        foreach (var start in new[] { new LandingVector(90, 45, 0), new LandingVector(12, 35, 5),
            new LandingVector(-45, 8, 0), new LandingVector(36, 0.1, 0), new LandingVector(50, -35, -20) })
        {
            var position = start; var velocity = new LandingVector(-0.1, 0, 0);
            var pulses = new RcsPulseControl();
            double ack = double.NegativeInfinity; bool cleared = false, contact = false;
            for (int step = 0; step < 60000; step++)
            {
                double now = step * dt;
                double error = now < 90 ? 25 : 0.5;
                var command = DockingMath.Approach(position, error, 0.1, acceleration, standOff, settings,
                    safeDistance: radius, orientationError: error, relativeVelocity: velocity, entryCleared: cleared);
                if (!cleared && command.EntryCleared)
                    Check(position.X >= standOff - 0.25 && Math.Abs(position.Y) < settings.CorridorRadiusM
                        && error <= settings.AlignmentToleranceDeg, "Envelope entry was cleared before outside head-to-head alignment");
                cleared = command.EntryCleared;
                var signs = pulses.Update(now, ack, 0.05, command.DesiredVelocity, velocity,
                    command.Waypoint - position, new LandingVector(quantum, quantum, quantum));
                if (signs.LengthSquared > 0 && now > ack + 0.15)
                { velocity += signs * quantum; ack = now; }
                position += velocity * dt;
                if (!cleared) Check(position.Length >= radius, $"Route crosses clearance before admission: start={start}, position={position}, command={command.Phase}");
                if (position.Length < radius && cleared)
                    Check(Math.Sqrt(position.Y * position.Y + position.Z * position.Z) < settings.CorridorRadiusM,
                        "Admitted approach leaves the axial corridor");
                if (position.Length < 0.4 && velocity.Length <= 0.15) { contact = true; break; }
            }
            Check(contact, $"Clearance route never reaches contact from {start}: remaining={position.Length:F3}");
        }
    }

    public static void FinePulses()
    {
        foreach (double quantum in new[] { 0.004, 0.04, 0.1 })
        {
            const double dt = 0.02;
            var pulses = new RcsPulseControl();
            double position = 0.12, velocity = 0, ack = double.NegativeInfinity;
            int pulseCount = 0, baselineCount = 0;
            double baselinePosition = position, baselineVelocity = 0, maximumSettledError = 0;
            for (int step = 0; step < 15000; step++)
            {
                double now = step * dt;
                var requested = DockingMath.WaypointVelocity(new LandingVector(0, -position, 0), 0.5, 0.5, 1.25);
                var signs = pulses.Update(now, ack, 0.05, requested, new LandingVector(0, velocity, 0),
                    new LandingVector(0, -position, 0), new LandingVector(quantum, quantum, quantum));
                if (signs.Y != 0 && now > ack + 0.15) { velocity += signs.Y * quantum; pulseCount++; ack = now; }
                position += velocity * dt;
                if (now > 30) maximumSettledError = Math.Max(maximumSettledError, Math.Abs(position));
                if (step % 10 == 0)
                {
                    double desired = -Math.Min(0.5, Math.Sqrt(0.5 * Math.Abs(baselinePosition) / 1.25)) * Math.Sign(baselinePosition);
                    int sign = DockingMath.TranslationSign(desired - baselineVelocity, quantum);
                    baselineVelocity += sign * quantum; if (sign != 0) baselineCount++;
                }
                baselinePosition += baselineVelocity * dt;
            }
            Console.WriteLine($"  RCS quantum={quantum:F3} m/s: pulses={pulseCount}, previous={baselineCount}, settled offset={maximumSettledError:F4} m");
            Check(maximumSettledError < 0.08, "Coarse minimum impulses cannot maintain precise alignment");
            Check(pulseCount < baselineCount * 0.6, "Fine control still fires almost every native pulse interval");
        }
        var fineSpeedPulses = new RcsPulseControl();
        double travelled = 0, speed = 0, pulseTime = double.NegativeInfinity;
        int slowPulses = 0;
        for (int step = 0; step < 15000; step++)
        {
            double now = step * 0.02;
            var signs = fineSpeedPulses.Update(now, pulseTime, 0.05, new LandingVector(-0.01, 0, 0), new LandingVector(speed, 0, 0),
                default, new LandingVector(0.1, 0.1, 0.1), new LandingVector(1, 0, 0));
            if (signs.X != 0 && now > pulseTime + 0.15) { speed += signs.X * 0.1; pulseTime = now; slowPulses++; }
            travelled += speed * 0.02;
        }
        Check(Math.Abs(travelled + 3) < 0.15 && slowPulses < 100,
            $"Contact speed below one minimum impulse either stalls or fires continuously: travelled={travelled:F3}, pulses={slowPulses}");
        var scheduler = new RcsPulseControl();
        var desiredVelocity = new LandingVector(1, 0, 0); var q = new LandingVector(0.1, 0.1, 0.1);
        var first = scheduler.Update(1, 0, 0.4, desiredVelocity, default, new LandingVector(20, 0, 0), q);
        Check(first.X == 1 && scheduler.Update(1.05, 0, 0.4, desiredVelocity, default, new LandingVector(20, 0, 0), q).X == 1,
            "A request disappears before the native controller reads it");
        Check(scheduler.Update(1.1, 1.08, 0.4, desiredVelocity, default, new LandingVector(20, 0, 0), q).Length == 0
            && scheduler.Update(1.5, 1.08, 0.4, desiredVelocity, default, new LandingVector(20, 0, 0), q).Length == 0,
            "Another pulse is sent before the preceding hardware pulse has finished");
        scheduler.Reset();
        Check(scheduler.Update(0.1, 0, 0.05, desiredVelocity, default, new LandingVector(20, 0, 0), q).X == 0,
            "Restart ignores the native pulse cooldown");
    }
}
