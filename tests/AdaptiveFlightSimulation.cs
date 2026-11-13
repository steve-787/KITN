using KSAAutopilot;

internal static class AdaptiveFlightSimulation
{
    private const double Dt = 0.02, Deg = 180 / Math.PI;
    public static void RocketControl()
    {
        foreach (var variant in new[] { ("rebuilt", 0.0, 1.0), ("heavy payload", 30000.0, 1.0), ("lower thrust", 0.0, 0.75) })
        {
            RocketFlightModel model = AdaptivePlanningTests.LoggedRocket(variant.Item2, variant.Item3);
            AdaptiveFlightPlan plan = AdaptiveFlightPlanner.Calculate(FlightPlanSettings.Capture(new LaunchProfile()), model);
            if (!plan.Success) throw new InvalidOperationException("No plan for physical test: " + plan.Message);
            Simulate(variant.Item1, model, plan.Settings, false);
        }
    }
    public static void SafeLowOrbit()
    {
        RocketFlightModel model = AdaptivePlanningTests.LoggedRocket();
        Simulate("logged 163 km coast", model, FlightPlanSettings.Capture(new LaunchProfile()), true);
    }

    private static void Simulate(string name, RocketFlightModel model, FlightPlanSettings settings, bool lowOrbit)
    {
        var profile = new LaunchProfile(); settings.Apply(profile);
        var turn = new GravityTurnController(); var actuator = new ThrottleActuator(); var gate = new InsertionAttitudeGate();
        double radius = model.BodyRadius + (lowOrbit ? 163477.15076624695 : model.Altitude);
        double vr = lowOrbit ? -16.62038096632935 : model.RadialSpeed;
        double vt = lowOrbit ? 7819.325170153946 : model.TangentialSpeed;
        int stage = lowOrbit ? 2 : 0, state = lowOrbit ? 2 : 0;
        double mass = lowOrbit ? 58474.6640625 : model.Phases[0].StartMass;
        double spent = lowOrbit ? (model.Phases[2].StartMass - mass) / model.Phases[2].MassFlow : 0;
        double pitch = lowOrbit ? 0.0455 : 90, bodyRate = lowOrbit ? -0.06 : 0, commandPitch = pitch;
        double previousThrottle = 0, highestAp = 0, firstInsertion = double.NaN, coastImpulse = 0;
        bool complete = false;
        double time = 0;
        for (int step = 0; step < (lowOrbit ? 350000 : 100000); step++)
        {
            time = step * Dt;
            RouteBurnPhase burn = model.Phases[stage];
            if (spent >= burn.FullThrottleSeconds)
            {
                if (++stage >= model.Phases.Length) break;
                burn = model.Phases[stage]; mass = burn.StartMass; spent = 0; previousThrottle = 0;
            }
            double altitude = radius - model.BodyRadius;
            double surfaceTangent = vt - model.RotationSpeed * radius / model.BodyRadius;
            double speed = Math.Sqrt(vr * vr + surfaceTangent * surfaceTangent);
            double density = altitude >= model.AtmosphereHeight ? 0 : model.SeaLevelDensity * Math.Exp(-Math.Max(0, altitude) / (model.AtmosphereHeight / 14));
            double q = 0.5 * density * speed * speed;
            double thrust = burn.Thrust * (1 - (1 - model.AtmosphericThrustRatio) * Math.Exp(-Math.Max(0, altitude) / (model.AtmosphereHeight / 14)));
            double acceleration = thrust / mass, orbitRate = vt / radius * Deg;
            var orbit = OrbitInsertion.CalculateOrbit(model.Mu, radius, vr, vt);
            double ap = orbit.ApoapsisRadius - model.BodyRadius, pe = orbit.PeriapsisRadius - model.BodyRadius;
            highestAp = Math.Max(highestAp, ap);
            if (OrbitInsertion.OrbitAchieved(ap, pe, profile.TargetApoapsisM, profile.TargetPeriapsisM)) { complete = true; break; }
            if (!orbit.Bound || altitude < 0 || state >= 2 && (ap > 210500 || altitude < 100000 && vr < 0)) break;
            if (state == 0 && GravityTurnController.ReadyToTurn(altitude, profile.GravityTurnStartM, speed,
                profile.GravityTurnMinSpeedMps, vr, pitch)) { state = 1; turn.Reset(pitch); }
            double throttle = 0, desiredPitch = 90;
            if (state <= 1)
            {
                throttle = OrbitInsertion.AscentThrottle(model.Mu, model.BodyRadius, radius, vr, vt,
                    profile.TargetApoapsisM, acceleration, pitch, Dt, out _);
                if (state == 1)
                {
                    double target = GravityTurnController.ProfilePitch(altitude, profile.GravityTurnStartM, profile.GravityTurnEndM, profile.GravityTurnShape);
                    target = GravityTurnController.LimitPitchForApoapsis(target, altitude, ap, profile.TargetApoapsisM);
                    target = AdaptiveAscentControl.Pitch(target, model.Mu, model.BodyRadius, radius, vr, vt,
                        profile.TargetApoapsisM, acceleration, q, model.AtmosphereHeight);
                    target = GravityTurnController.LimitPitchInAtmosphere(target, Math.Atan2(vr, surfaceTangent) * Deg, speed, q);
                    desiredPitch = turn.Update(target, pitch, Math.Abs(turn.CommandedPitchDeg - pitch), Math.Abs(bodyRate),
                        GravityTurnController.RateLimit(altitude, profile.MaxPitchRateDegS), Dt);
                    if (ap >= profile.TargetApoapsisM - 600 && throttle < 0.15)
                    { state = 2; commandPitch = pitch; gate.Reset(); throttle = 0; }
                }
            }
            if (state >= 2)
            {
                double secondsToAp = TimeToAp(model.Mu, radius, vr, vt);
                double apRadius = model.BodyRadius + ap;
                double dv = Math.Max(0, OrbitInsertion.CaptureTangentialSpeed(model.Mu, apRadius,
                    model.BodyRadius + profile.TargetPeriapsisM, model.BodyRadius + profile.TargetApoapsisM) - radius * vt / apRadius);
                double duration = OrbitInsertion.BurnDuration(dv, mass, thrust, burn.MassFlow);
                if (state == 2 && OrbitInsertion.ReadyToBurn(altitude, secondsToAp, duration, vr, pe, model.AtmosphereHeight))
                { state = 3; firstInsertion = time; }
                var capture = OrbitInsertion.Capture(model.Mu, radius, vr, vt, model.BodyRadius + profile.TargetPeriapsisM,
                    model.BodyRadius + profile.TargetApoapsisM, acceleration);
                desiredPitch = state == 2 ? 0 : capture.PitchDeg;
                double error = Math.Abs(Wrap(desiredPitch - pitch));
                bool safeCoast = pe >= Math.Max(100000, model.AtmosphereHeight + 5000);
                bool window = state == 3 || altitude >= 100000 && (secondsToAp <= Math.Max(30, OrbitInsertion.BurnLead(duration) + error / 2 + 10)
                    || !safeCoast && (vr <= 0 || Math.Abs(bodyRate) > 0.3));
                bool settling = gate.Update(error, Math.Abs(bodyRate), false, window);
                throttle = state == 3 && error <= 5 ? Math.Max(capture.Throttle, settling ? 0.01 : 0) : settling ? 0.05 : 0;
                throttle = OrbitInsertion.GuardInsertionThrottle(model.Mu, model.BodyRadius, radius, vr, vt,
                    profile.TargetApoapsisM, acceleration, pitch, throttle, Dt, out _);
                commandPitch += orbitRate * Dt;
                double limit = state == 2 ? 2 : 3;
                if (Math.Abs(Wrap(commandPitch - pitch)) <= 4 && Math.Abs(bodyRate) <= limit + 0.3)
                    commandPitch += Math.Clamp(Wrap(desiredPitch - commandPitch), -limit * Dt, limit * Dt);
            }
            else commandPitch = desiredPitch;
            double minimum = stage == 2 ? 0.1 : 0.2;
            double pulse = gate.NeedsThrust && (Math.Abs(Wrap(desiredPitch - pitch)) > 5 || Math.Abs(bodyRate) > 0.3) ? 0.12 : 0.001;
            bool safePulse = throttle <= 0 || OrbitInsertion.GuardImpulse(model.Mu, model.BodyRadius, radius, vr, vt,
                state < 2 ? profile.TargetApoapsisM : OrbitInsertion.ApoapsisCeiling(profile.TargetApoapsisM),
                acceleration, pitch, minimum, Math.Max(Dt, pulse), out _) >= minimum - 1e-6;
            throttle = actuator.Command(throttle, minimum, Dt, safePulse, pulse);
            throttle = Math.Min(throttle, (burn.FullThrottleSeconds - spent) / Dt);
            double rateLimit = state < 2 ? GravityTurnController.RateLimit(altitude, profile.MaxPitchRateDegS) : state == 2 ? 2 : 3;
            double requestedRate = Math.Clamp(Wrap(commandPitch - pitch) / 0.4, -rateLimit, rateLimit) - orbitRate;
            double authority = throttle > 0 && previousThrottle > 0 ? model.AttitudeAccelerationDegS2 * 0.8 * throttle : 0;
            bodyRate += Math.Clamp((requestedRate - bodyRate) / 0.25, -authority, authority) * Dt;
            pitch = Wrap(pitch + (bodyRate + orbitRate) * Dt);
            previousThrottle = throttle;
            if (state == 2) coastImpulse += acceleration * throttle * Dt;
            double drag = q * model.DragArea * 1.15 / mass;
            double ar = vt * vt / radius - model.Mu / (radius * radius) + acceleration * throttle * Math.Sin(pitch / Deg) - (speed > 0.1 ? drag * vr / speed : 0);
            double at = -vr * vt / radius + acceleration * throttle * Math.Cos(pitch / Deg) - (speed > 0.1 ? drag * surfaceTangent / speed : 0);
            radius += vr * Dt; vr += ar * Dt; vt += at * Dt;
            mass -= burn.MassFlow * throttle * Dt; spent += throttle * Dt;
        }
        var final = OrbitInsertion.CalculateOrbit(model.Mu, radius, vr, vt);
        Console.WriteLine(FormattableString.Invariant($"  physical {name}: T+{time:F1}s, AP={(final.ApoapsisRadius-model.BodyRadius)/1000:F2}/PE={(final.PeriapsisRadius-model.BodyRadius)/1000:F2} km, max AP={highestAp/1000:F2} km, insertion={firstInsertion:F1}s, coast impulse={coastImpulse:F1} m/s, pitch={pitch:F1}, state={state}"));
        if (!complete) throw new InvalidOperationException("Physical adaptive flight failed for " + name);
        if (lowOrbit && (time < 3000 || double.IsFinite(firstInsertion) && firstInsertion < 3000))
            throw new InvalidOperationException("Safe low orbit completed or fired before the next AP");
    }
    private static double TimeToAp(double mu, double radius, double vr, double vt)
    {
        double energy = 0.5 * (vr * vr + vt * vt) - mu / radius;
        if (energy >= 0) return double.NaN;
        double a = -mu / (2 * energy), h = radius * vt, e = Math.Sqrt(Math.Max(0, 1 - h * h / (mu * a)));
        if (e < 1e-8) return 0;
        double anomaly = Math.Atan2(vr * radius / (e * Math.Sqrt(mu * a)), (1 - radius / a) / e);
        return (Math.PI - anomaly + e * Math.Sin(anomaly)) / Math.Sqrt(mu / (a * a * a));
    }
    private static double Wrap(double angle) => (angle + 540) % 360 - 180;
}
