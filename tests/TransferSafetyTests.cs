using KITN;

internal static class TransferSafetyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Ignition()
    {
        const double ignition = 1000;
        foreach (double speed in new[] { 2.0, 10, 10000 })
            Check(!TransferSafety.CanIgnite(ignition, ignition, true, false, speed), "Ignition permitted during time acceleration");
        Check(!TransferSafety.CanIgnite(ignition, ignition, true, true, 1), "Ignition permitted during auto warp");
        Check(!TransferSafety.CanIgnite(999.99, ignition, true, false, 1), "Early ignition permitted");
        Check(!TransferSafety.CanIgnite(1006, ignition, true, false, 1), "Missed departure can fire late");
        Check(!TransferSafety.CanIgnite(1000, ignition, false, false, 1), "Misaligned ignition permitted");
        Check(TransferSafety.CanIgnite(1000, ignition, true, false, 1), "Aligned normal-speed burn cannot ignite");
        Check(!TransferSafety.Settled(0.1, 0.5), "Angular inertia is ignored before warp");
        Check(!TransferSafety.Settled(5, 0.01), "Incorrect pointing accepted");
        Check(!TransferSafety.Settled(double.NaN, 0) && !TransferSafety.CanIgnite(double.NaN, ignition, true, false, 1), "Invalid navigation permits thrust");
        Check(TransferSafety.AxisSettled(0.5, 0.58), "Minimum RCS pulse prevents an aligned vehicle from warping");
        Check(!TransferSafety.AxisSettled(0.8, 0.58), "Rotation above RCS pulse resolution is accepted");
        Check(!TransferSafety.AxisSettled(2, 10), "Overpowered jets permit a tumbling vehicle to warp");
        Check(!TransferSafety.AxisSettled(0.5, 0) && !TransferSafety.AxisSettled(0.5, double.NaN), "Unavailable pulse data disables rotation limits");
        Check(TransferSafety.AlignmentLeadSeconds == 60, "Final transfer alignment does not begin a minute before ignition");
    }

    public static void Arrival()
    {
        const double moon = 1737100, peak = moon + 10785;
        Check(TransferSafety.ArrivalError(11000, moon, peak, 0) == null, "Airless lunar orbit wrongly rejected");
        Check(TransferSafety.ArrivalError(5000, moon, peak, 0) == "transfer.arrival_surface", "Lunar terrain is incorrectly reported as atmosphere");
        Check(TransferSafety.ArrivalError(500, moon, moon, 0) == null, "Low airless mod-body orbit rejected");
        Check(TransferSafety.ArrivalError(500, moon, double.NaN, double.NaN) == null, "Missing optional body data rejects a valid orbit");
        Check(TransferSafety.ArrivalError(90000, 6371000, 6380000, 6471000) == "transfer.arrival_atmosphere", "Atmospheric orbit accepted");
        Check(TransferSafety.ArrivalError(101000, 6371000, 6380000, 6471000) == null, "Orbit above atmosphere rejected");
        Check(TransferSafety.ArrivalError(-1, moon, peak, 0) != null, "Negative arrival altitude accepted");
        Check(TransferSafety.NearbyAltitude(300e6, 80e6, moon, 66e6, false), "Altitude does not switch before lunar SOI");
        Check(!TransferSafety.NearbyAltitude(200000, 384e6, moon, 66e6, false), "LEO height switches to distant Moon");
        Check(!TransferSafety.NearbyAltitude(800e6, 140e6, moon, 66e6, false), "Altitude switches to distant body outside approach region");
        Check(TransferSafety.NearbyAltitude(300e6, moon - 50, moon, 66e6, true), "Height switches back to the parent when below the nearby body's mean radius");
    }

    public static void Capture()
    {
        const double mu = 4.9048695e12, radius = 1737100 + 11000, excess = 900;
        foreach (double inclination in new[] { 0.0, 0.7, 2.8 })
        {
            var r = new LandingVector(radius, 0, 0);
            var v = new LandingVector(0, Math.Cos(inclination), Math.Sin(inclination))
                * Math.Sqrt(excess * excess + 2 * mu / radius);
            Check(TransferSafety.Capture(mu, r, v, out var dv), "Capture calculation failed");
            var captured = new TransferOrbit(mu, 0, r, v + dv);
            Check(captured.Valid && Math.Abs(captured.Eccentricity) < 1e-8, "Capture does not circularize the incoming orbit");
            Check(Math.Abs(dv.Length - TransferMath.CaptureDv(mu, radius, excess)) < 1e-7, "Capture disagrees with independent energy estimate");
            Check(captured.State(captured.Period * 2, out var after, out _)
                && Math.Abs(after.Length - radius) < 0.01, "Captured orbit does not remain above the surface");
        }
        Check(!TransferSafety.Capture(mu, new LandingVector(radius, 0, 0), new LandingVector(-900, 0, 0), out _), "Radial impact is mistaken for a capture orbit");
    }
}
