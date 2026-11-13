using System.Globalization;
using KITN;

internal static class MissionClockTests
{
    public static void Launch()
    {
        var clock = new MissionClock();
        clock.BeginCountdown(10, 100);
        Check(clock.IsCountingDown && clock.Display == "T-00:00:10", "Countdown did not start at ten seconds");
        Check(!clock.Advance(109.9) && clock.Display == "T-00:00:01", "Countdown announced launch too early");
        Check(clock.Advance(110) && clock.Display == "T-00:00:00", "Countdown did not request launch at zero");
        Check(!clock.Advance(110) && !clock.Advance(111), "Countdown requested launch more than once");
        clock.MarkLaunched(111);
        Check(clock.HasLaunched && !clock.IsCountingDown && clock.Display == "T+00:00:00", "T plus did not start at actual launch");
        clock.Advance(111.9);
        Check(clock.Display == "T+00:00:00", "Flight time rounds forward prematurely");
        clock.Advance(172);
        Check(clock.Display == "T+00:01:01", "Mission time did not count forward");
        clock.Stop();
        clock.Advance(1000);
        Check(clock.Display == "T+00:01:01", "Abort did not freeze mission time");
    }

    public static void Display()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "de-DE", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Check(MissionClock.Format(false, 3661) == "T-01:01:01", "Countdown format is incorrect");
                Check(MissionClock.Format(true, 3661) == "T+01:01:01", "Flight format is incorrect");
                Check(MissionClock.Format(true, 90061) == "T+25:01:01", "Flight time wrapped after 24 hours");
                Check(MissionClock.Format(false, 59.1) == "T-00:01:00", "Countdown should round remaining seconds upward");
                Check(MissionClock.Format(true, double.NaN) == "T+00:00:00", "Missing time broke formatting");
                Check(MissionClock.Format(false, double.PositiveInfinity) == "T-00:00:00", "Infinite time broke formatting");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    public static void Cancel()
    {
        var clock = new MissionClock();
        clock.BeginCountdown(10, 0);
        clock.Advance(9);
        clock.CancelCountdown();
        Check(!clock.Advance(10) && !clock.Advance(100), "Cancellation launched the rocket");
        Check(clock.Mode == MissionClock.ClockMode.Cancelled && clock.Display == "T-00:00:01", "Cancelled countdown was not retained");
        clock.BeginCountdown(5, 100);
        clock.Reset();
        Check(!clock.Advance(105) && clock.Mode == MissionClock.ClockMode.Ready && !clock.HasLaunched,
            "Reset preserved a launch request");
        foreach (int duration in new[] { -1, 0, 3601 })
        {
            bool invalid = false;
            try { clock.BeginCountdown(duration, 100); }
            catch (ArgumentOutOfRangeException) { invalid = true; }
            Check(invalid, "Invalid countdown duration was accepted");
        }
    }

    public static void SimulationTime()
    {
        var clock = new MissionClock();
        clock.BeginCountdown(10, 1000);
        for (int frame = 0; frame < 100; frame++) Check(!clock.Advance(1000), "Paused countdown launched");
        Check(clock.Display == "T-00:00:10", "Countdown used wall time during a simulation pause");
        Check(clock.Advance(1050), "A skipped countdown interval did not reach launch");
        clock.MarkLaunched(1050);
        clock.Advance(4651);
        Check(clock.Display == "T+01:00:01", "Warp did not advance mission time");
        Check(!clock.Advance(1000) && clock.Mode == MissionClock.ClockMode.Ready, "Reverted flight retained its clock");
        clock.BeginCountdown(10, 1000);
        Check(!clock.Advance(900) && !clock.Advance(2000), "Reverted countdown later launched");
    }

    public static void VehicleFocus()
    {
        var clock = new MissionClock();
        clock.BeginCountdown(10, 100);
        clock.OnVehicleChanged();
        Check(!clock.Advance(110) && !clock.IsCountingDown, "Changing vehicle allowed the old countdown to launch");
        clock.MarkLaunched(111);
        clock.Advance(411);
        clock.OnVehicleChanged();
        clock.Advance(1011);
        Check(clock.HasLaunched && clock.Display == "T+00:15:00", "Switching to a booster lost mission elapsed time");
        clock.OnVehicleChanged();
        clock.Advance(3711);
        Check(clock.Mode == MissionClock.ClockMode.Flight && clock.Display == "T+01:00:00", "A passive mission clock stopped after guidance ended");
        clock.Reset();
        Check(!clock.HasLaunched && clock.Mode == MissionClock.ClockMode.Ready, "Explicit reset did not start a new mission");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
