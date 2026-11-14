using KITN;

internal static class DockingTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Guards()
    {
        Check(!DockingMath.HasBrakingRoom(100, 10, 0.02, 20), "Fast closing approach is accepted without braking room");
        Check(DockingMath.HasBrakingRoom(100, 0.5, 0.02, 20), "A safe rendezvous cannot start");
        Check(!DockingMath.HasBrakingRoom(100, 1, 0, 20) && !DockingMath.HasBrakingRoom(double.NaN, 0, 1, 0), "Unavailable RCS or position is accepted");
        var retreat = DockingMath.Approach(new LandingVector(10, 8, 0), 0, 0, 0.02, 35);
        Check(retreat.Phase == DockingPhase.Retreating && retreat.DesiredVelocity.X > 0 && retreat.DesiredVelocity.Y == 0,
            "Misaligned lateral approach crosses the target instead of retreating first");
        var align = DockingMath.Approach(new LandingVector(40, 0, 0), 180, 0, 0.02, 35);
        Check(align.Phase == DockingPhase.Aligning && align.DesiredVelocity.Length == 0, "A reversed spacecraft closes on the target before alignment");
        var final = DockingMath.Approach(new LandingVector(2, 0.05, 0), 0.5, 0.1, 0.02, 35);
        Check(final.Phase == DockingPhase.Docking && final.DesiredVelocity.X < 0 && final.DesiredVelocity.Length <= 0.08,
            "Final approach exceeds the contact-speed limit");
        Check(DockingMath.TranslationSign(0.001, 0.01) == 0 && DockingMath.TranslationSign(1, 0.01) == 1
            && DockingMath.TranslationSign(-1, 0.01) == -1 && DockingMath.TranslationSign(double.NaN, 0.01) == 0,
            "RCS pulses chatter inside the deadband or translate in the wrong direction");
    }

    public static void Approaches()
    {
        foreach (var scenario in new[] { (500.0, 40.0, 0.0), (10.0, 8.0, 0.0), (300.0, 15.0, 0.05), (3.0, 0.1, 0.0) })
        {
            const double dt = 0.1, quantum = 0.004, acceleration = 0.01, standOff = 35;
            var position = new LandingVector(scenario.Item1, scenario.Item2, 0);
            var velocity = default(LandingVector);
            var omega = new LandingVector(0, 0, scenario.Item3 * Math.PI / 180);
            bool contact = false, retreated = false;
            for (int step = 0; step < 30000; step++)
            {
                double time = step * dt;
                var localPosition = LandingVector.Rotate(position, -omega, time);
                var localVelocity = LandingVector.Rotate(velocity - LandingVector.Cross(omega, position), -omega, time);
                var command = DockingMath.Approach(localPosition, 0.5, 0.05, acceleration, standOff);
                retreated |= command.Phase == DockingPhase.Retreating;
                if (step % 2 == 0)
                {
                    var error = command.DesiredVelocity - localVelocity;
                    var impulse = new LandingVector(DockingMath.TranslationSign(error.X, quantum), DockingMath.TranslationSign(error.Y, quantum),
                        DockingMath.TranslationSign(error.Z, quantum)) * quantum;
                    velocity += LandingVector.Rotate(impulse, omega, time);
                }
                position += velocity * dt;
                if (localPosition.Length < 0.4 && localVelocity.Length < 0.15) { contact = true; break; }
                Check(localPosition.X > -0.5, "An approach flies through the connector plane at excessive distance");
            }
            Check(contact, $"RCS approach never reaches safe contact: start={scenario}, remaining={position.Length:F2}");
            if (scenario.Item1 == 10) Check(retreated, "Close lateral approach never establishes stand-off");
        }
    }
}
