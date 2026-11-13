using KITN;

internal static class LandingTests
{
    private const double Radius = 6371000, Mu = 3.986004418e14;
    private static LandingBodyModel Earth(bool atmosphere = true) => new LandingBodyModel
    {
        Radius = Radius, Mu = Mu, Mass = 80000, DragArea = 16,
        AngularVelocity = new LandingVector(0, 0, 7.2921159e-5),
        AtmosphereHeight = atmosphere ? 110000 : 0,
        DensitySamples = atmosphere ? Enumerable.Range(0, 56).Select(i => 1.225 * Math.Exp(-i * 2000 / 8500.0)).ToArray() : Array.Empty<double>()
    };

    public static void Transfers()
    {
        foreach (double direction in new[] { -1.0, 1.0 })
        {
            var body = Earth(false);
            var position = new LandingVector(Radius + 200000, 0, 0);
            var end = new LandingVector(Math.Cos(1.7) * (Radius + 5000), direction * Math.Sin(1.7) * (Radius + 5000), 0);
            Check(LandingMath.Lambert(Mu, position, end, new LandingVector(0, 0, direction), 1500, out var velocity), "Transfer solver rejected a valid descent");
            Check(Math.Sign(velocity.Y) == direction, "Transfer goes around Earth in the wrong direction");
            for (int i = 0; i < 3000; i++) LandingMath.Step(body, ref position, ref velocity, 0.5);
            Check((position - end).Length < 5, "Transfer misses the requested endpoint");
        }
        Check(!LandingMath.Lambert(0, default, default, default, 0, out _), "Invalid orbital inputs produce a return burn");
        Check(Math.Abs(LandingMath.SurfaceDistance(new LandingVector(Radius, 0, 0), new LandingVector(-Radius, 0, 0), Radius) - Math.PI * Radius) < 1,
            "The opposite side of Earth is mistaken for the selected pad");
    }

    public static void AtmosphericTarget()
    {
        var body = Earth();
        var position = new LandingVector(Radius + 200000, 0, 0);
        var pad = new LandingVector(Math.Cos(1.7), Math.Sin(1.7), 0) * Radius;
        var end = LandingVector.Rotate(pad, body.AngularVelocity, 1500).Unit * (Radius + LandingMath.HandoverHeight);
        Check(LandingMath.Lambert(Mu, position, end, new LandingVector(0, 0, 1), 1500, out var seed), "No Lambert seed for atmospheric return");
        Check(LandingMath.Target(body, position, seed, pad, 0, out var targetVelocity, out var prediction), "Atmospheric footprint targeting did not converge");
        double miss = (prediction.Position - LandingVector.Rotate(pad, body.AngularVelocity, prediction.Seconds).Unit * (Radius + LandingMath.HandoverHeight)).Length;
        Console.WriteLine($"  atmospheric return: handover T+{prediction.Seconds:F1}s, miss {miss:F1} m, reserve {LandingMath.LandingReserve(body, prediction):F0} m/s");
        Check(miss < 2500 && targetVelocity.IsFinite, "Atmospheric correction did not reach the rotating pad");
        Check(LandingMath.LandingReserve(body, prediction) >= 350, "Return plan leaves no landing reserve");
        Check(LandingMath.AvailableDeltaV(100, 10, 3000) > 300, "Usable fuel does not produce the expected delta-v budget");
        Check(LandingMath.AvailableDeltaV(100, 100, 3000) == 0, "Invalid mass budget is accepted");
    }

    public static void Descent()
    {
        var body = Earth(false);
        body.Mass = 40000;
        var pad = new LandingVector(Radius, 0, 0);
        var position = new LandingVector(Radius + 5000, 500, 0);
        var velocity = new LandingVector(-220, 20, 0) + LandingVector.Cross(body.AngularVelocity, position);
        double elapsed = 0, miss = 0, speed = 0, vertical = 0;
        bool touched = false;
        for (int frame = 0; frame < 20000; frame++)
        {
            var target = LandingVector.Rotate(pad, body.AngularVelocity, elapsed);
            double clearance = position.Length - Radius;
            var command = LandingMath.Descend(body, position, velocity, target, clearance, 25);
            LandingMath.Step(body, ref position, ref velocity, 0.02, command.Acceleration);
            elapsed += 0.02;
            var relative = velocity - LandingVector.Cross(body.AngularVelocity, position);
            miss = (target - position).Horizontal(position.Unit).Length;
            speed = relative.Length;
            vertical = LandingVector.Dot(relative, position.Unit);
            if (position.Length <= Radius) { touched = true; break; }
        }
        Console.WriteLine($"  powered landing: T+{elapsed:F1}s, miss {miss:F2} m, surface speed {speed:F2} m/s, vertical {vertical:F2} m/s");
        Check(touched, "Powered descent never reaches the surface");
        Check(miss < 25 && speed < 1.5 && Math.Abs(vertical) < 1.2, "Powered descent does not finish with a gentle landing inside the pad");
    }

    public static void Contact()
    {
        var contact = new LandingTouchdown();
        Check(!contact.Confirm(0, false, 0.1, 0, 0, 50), "Hovering is mistaken for touchdown");
        Check(!contact.Confirm(1, true, 0, 10, 0, 50), "A fast impact is mistaken for a successful landing");
        Check(!contact.Confirm(2, true, 0, 0, 100, 50), "Landing at the wrong site is accepted");
        Check(!contact.Confirm(3, true, 0, 0.2, 2, 50) && !contact.Confirm(3.5, true, 0, 0.2, 2, 50), "Contact is accepted before confirmation");
        Check(contact.Confirm(3.75, true, 0, 0.2, 2, 50), "Sustained contact on the selected pad is not accepted");
        contact.Reset();
        Check(!contact.Confirm(10, true, 0, 0, 2, 50) && !contact.Confirm(9, true, 0, 0, 2, 50), "A simulation revert confirms touchdown");
        Check(!contact.Confirm(20, true, 0, 0, double.NaN, 50), "Unavailable coordinates confirm touchdown");
    }

    public static void FullReturn()
    {
        SimulateReturn(2400000, 0.2, 6, 8, "standard booster");
        SimulateReturn(4800000, 0.35, 8, 8, "high thrust / minimum throttle");
        SimulateReturn(1600000, 0.2, 6, 8, "lower thrust");
        SimulateReturn(2400000, 0.2, 6, 8, "retrograde orbit", -1);
    }

    private static void SimulateReturn(double thrust, double minimumThrottle, int delayFrames, double slewRate, string name, double orbitDirection = 1)
    {
        var body = Earth();
        body.Thrust = thrust;
        var position = new LandingVector(Radius + 200000, 0, 0);
        var pad = new LandingVector(Math.Cos(1.7), orbitDirection * Math.Sin(1.7), 0) * Radius;
        double transferSeconds = orbitDirection > 0 ? 1500 : 1300;
        var end = LandingVector.Rotate(pad, body.AngularVelocity, transferSeconds).Unit * (Radius + LandingMath.HandoverHeight);
        Check(LandingMath.Lambert(Mu, position, end, new LandingVector(0, 0, orbitDirection), transferSeconds, out var seed), "No return seed");
        var originalVelocity = new LandingVector(0, orbitDirection * Math.Sqrt(Mu / position.Length), 0);
        Check(LandingMath.TargetReturn(body, position, originalVelocity, seed, pad, 0, 3000, out var velocity, out var prediction), "No atmospheric return target");
        double reserve = LandingMath.LandingReserve(body, prediction);
        double initialDeltaV = (velocity - originalVelocity).Length;
        const double exhaust = 3000;
        Check(initialDeltaV + reserve <= LandingMath.AvailableDeltaV(body.Mass, 40000, exhaust), $"{name}: test return window has insufficient launch reserve");
        body.Mass *= Math.Exp(-initialDeltaV / exhaust);
        double massBeforeLanding = body.Mass;
        double elapsed = 0;
        while (elapsed < 4000)
        {
            double down = Math.Max(0, -LandingVector.Dot(velocity - LandingVector.Cross(body.AngularVelocity, position), position.Unit));
            double stopping = down * down / (2 * Math.Max(0.1, thrust / body.Mass - Mu / position.LengthSquared));
            if (position.Length - Radius < Math.Max(7000, stopping * 1.5 + 8 * down + 1000)) break;
            LandingMath.Step(body, ref position, ref velocity, 0.5); elapsed += 0.5;
        }
        double landingStarted = elapsed;
        var throttle = new ThrottleActuator();
        var direction = -(velocity - LandingVector.Cross(body.AngularVelocity, position)).Unit;
        var lag = new Queue<(LandingVector Direction, double Throttle)>();
        for (int i = 0; i < delayFrames; i++) lag.Enqueue((direction, 0));
        double miss = 0, speed = 0;
        bool landed = false;
        for (int frame = 0; frame < 20000; frame++)
        {
            double maximum = thrust / body.Mass;
            var target = LandingVector.Rotate(pad, body.AngularVelocity, elapsed);
            var command = LandingMath.Descend(body, position, velocity, target, Math.Max(0, position.Length - Radius), maximum);
            var desired = command.Acceleration.Unit;
            double angle = Math.Acos(Math.Clamp(LandingVector.Dot(direction, desired), -1, 1));
            if (angle > 1e-6)
                direction = LandingVector.Rotate(direction, LandingVector.Cross(direction, desired).Unit,
                    Math.Min(angle, slewRate * Math.PI / 180 * 0.02));
            double request = command.Acceleration.Length / maximum;
            if (angle > Math.PI / 4) request = Math.Min(request, minimumThrottle);
            double fire = throttle.Command(request, minimumThrottle, 0.02, true, angle > 3 * Math.PI / 180 ? 0.12 : 0.001);
            lag.Enqueue((direction, fire));
            var actual = lag.Dequeue();
            LandingMath.Step(body, ref position, ref velocity, 0.02, actual.Direction * (actual.Throttle * maximum));
            body.Mass -= thrust / exhaust * actual.Throttle * 0.02;
            elapsed += 0.02;
            miss = LandingMath.SurfaceDistance(position, target, Radius);
            speed = (velocity - LandingVector.Cross(body.AngularVelocity, position)).Length;
            if (position.Length <= Radius) { landed = true; break; }
            if (body.Mass <= 40000) break;
        }
        double usedDeltaV = exhaust * Math.Log(massBeforeLanding / body.Mass);
        Console.WriteLine($"  return ({name}): deorbit {initialDeltaV:F0} m/s, powered {elapsed - landingStarted:F1}s, miss {miss:F1} m, touchdown {speed:F2} m/s, landing delta-v {usedDeltaV:F0}/{reserve:F0} m/s, fuel left {body.Mass - 40000:F0} kg");
        Check(landed && miss < 25 && speed < 1.5 && body.Mass > 40000, $"{name}: complete return misses the pad, exhausts fuel or touches down too quickly");
        Check(usedDeltaV * 1.1 <= reserve, $"{name}: return planner underestimates the landing fuel reserve");
    }

    public static void Telemetry()
    {
        var before = new TelemetryRecord();
        new LandingTelemetry { State = LandingState.Idle }.Append(before);
        var after = new TelemetryRecord();
        new LandingTelemetry { State = LandingState.Landed, Active = false, MissionClockRunning = true, MissionElapsed = 2500,
            Pad = "Pad, A", Clearance = 0.12, Reason = "sustained_contact_on_selected_pad", TerrainContact = true }.Append(after);
        var active = new TelemetryRecord();
        new LandingTelemetry { State = LandingState.PoweredDescent, Active = true, Pad = "Pad, A", Reason = "braking",
            Latitude = 28.6, Longitude = -80.6, PadDistance = 420, PredictedMiss = 50, TimeUntilBurn = -200,
            RemainingDeltaV = 600, AvailableDeltaV = 800, RequiredDeltaV = 1400, Clearance = 350,
            VerticalSpeed = -25, HorizontalSpeed = 12, RequestedThrottle = 0.12, CommandedThrottle = 0.2,
            PointingError = 1.2, TargetPitch = 87, StoppingHeight = 100, DesiredVerticalSpeed = -20,
            DragArea = 16, MissionElapsed = 1500, AlignmentHeight = 80, TimeToTouchdown = 30,
            CommandAcceleration = new LandingVector(1, 2, 12), MissionClockRunning = true }.Append(active);
        Check(before.CsvHeader == after.CsvHeader, "Landing changes the telemetry column schema between phases");
        Check(active.CsvHeader == after.CsvHeader && active.Sections.Single().Value.Contains("landing_command_accel_cci_z_m_s2=12.000")
            && active.Sections.Single().Value.Contains("landing_alignment_clearance_m=80.000"), "Powered landing commands or alignment clearance are missing from the stable log schema");
        Check(after.CsvRow.Contains("\"Pad, A\"") && after.CsvRow.Contains("0.12"), "Landing pad or numeric values are corrupted in CSV");
        Check(after.Sections.Single().Value.Contains("landing_active=false") && after.Sections.Single().Value.Contains("mission_clock_running=true"),
            "Completed landing is confused with a stopped mission clock");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
