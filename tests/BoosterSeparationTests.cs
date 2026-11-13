using KSAAutopilot;

internal static class BoosterSeparationTests
{
    private static BoosterObservation Fuel(double fraction) => new(true, false, true, 1, 0, 3, fraction);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void EmptyBoosters()
    {
        var separation = new BoosterSeparation();
        Check(!separation.Update(Fuel(1), 0), "Full booster was dropped");
        Check(!separation.Update(Fuel(0.02), 80), "Remaining booster propellant was ignored");
        Check(!separation.Update(Fuel(0), 100), "Empty booster did not wait for confirmation");
        Check(!separation.Update(Fuel(0), 100.49), "Confirmation ended too early");
        Check(separation.Update(Fuel(0), 100.5), "Empty booster was masked by the rest of the rocket");
        Check(separation.Reason == "booster_local_propellant_depleted", "Missing stable separation reason");
    }

    public static void IndependentBoosters()
    {
        var left = new BoosterSeparation();
        var right = new BoosterSeparation();
        left.Update(Fuel(0), 10);
        right.Update(Fuel(0.6), 10);
        Check(left.Update(Fuel(0), 10.5), "Empty booster waited for its full neighbour");
        Check(!right.Update(Fuel(0.6), 10.5), "Full neighbour was dropped together with the empty booster");
        Check(!right.Update(Fuel(0), 30), "Newly empty booster reused another booster's confirmation");
        Check(right.Update(Fuel(0), 30.5), "Second booster never completed its own confirmation");
    }

    public static void FuelRecovery()
    {
        var separation = new BoosterSeparation();
        separation.Update(Fuel(0), 0);
        Check(!separation.Update(Fuel(0.01), 0.4), "Fuel transfer was ignored");
        Check(!separation.Update(Fuel(0), 0.5), "Recovered fuel did not reset confirmation");
        Check(!separation.Update(Fuel(0), 0.9), "Transient depletion triggered separation");
        Check(separation.Update(Fuel(0), 1), "Continuous empty state never confirmed");
        separation.Reset();
        Check(!separation.Update(Fuel(0), 100), "Flight reset reused the previous confirmation");
    }

    public static void SafeAttachments()
    {
        var blocked = new[]
        {
            new BoosterObservation(false, false, true, 1, 0, 1, 0),
            new BoosterObservation(true, true, true, 1, 0, 1, 0),
            new BoosterObservation(true, false, true, 0, 0, 1, 0),
            new BoosterObservation(true, false, true, 1, 1, 1, 0),
            new BoosterObservation(true, false, false, 1, 0, 1, 0),
            new BoosterObservation(true, false, true, 1, 0, 0, 0),
            new BoosterObservation(true, false, true, 1, 0, 1, double.NaN),
            new BoosterObservation(true, false, true, 1, 0, 1, double.PositiveInfinity),
            new BoosterObservation(true, false, true, 1, 0, 1, -0.1)
        };
        foreach (var observation in blocked)
        {
            var separation = new BoosterSeparation();
            separation.Update(Fuel(0), 0);
            Check(!separation.Update(observation, 100), "Unsafe attachment triggered separation: " + separation.Reason);
            Check(separation.ConfirmationElapsed == 0, "Unsafe attachment kept its confirmation");
        }
    }

    public static void SimulationTime()
    {
        var separation = new BoosterSeparation();
        separation.Update(Fuel(0), 20);
        for (int i = 0; i < 100; i++) Check(!separation.Update(Fuel(0), 20), "Paused simulation dropped a booster");
        Check(!separation.Update(Fuel(0), 19), "Time revert kept a future confirmation");
        Check(!separation.Update(Fuel(0), 19.4), "Reverted confirmation ended too early");
        Check(separation.Update(Fuel(0), 19.5), "Reverted confirmation could not recover");
        Check(!separation.Update(Fuel(0), double.NaN), "Invalid time triggered separation");
        Check(!separation.Update(Fuel(0), 30), "Invalid time did not reset confirmation");
    }
}
