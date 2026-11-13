using KITN;

// Central gravity, rotation, drag, mass flow, minimum throttle and physical body rotation.
// TVC uses the previous powered state; no RCS or artificial damping exists with the engine off.
internal static class FlightSimulation
{
    private const double Mu = 3.9860187717e14, Planet = 6371000, Dt = 0.02, Deg = 180 / Math.PI;

    public static void Run() => Simulate(false);
    public static void LoggedCoast() => Simulate(true);

    private static void Simulate(bool loggedCoast)
    {
        var profile = new LaunchProfile();
        var turn = new GravityTurnController();
        var actuator = new ThrottleActuator();
        var gate = new InsertionAttitudeGate();
        double radius = loggedCoast ? 6496794.069415742 : Planet + 40;
        double vr = loggedCoast ? 789.7390022280317 : 0;
        double vt = loggedCoast ? 5805.705390999263 : 408;
        double mass = loggedCoast ? 145029.359375 : 908000;
        double pitch = loggedCoast ? -0.0013152788529655717 : 90, bodyRate = loggedCoast ? -0.04796476665762985 : 0;
        double commandPitch = pitch, coastTime = loggedCoast ? 0 : double.NaN, insertionTime = double.NaN;
        double highestAp = 0, oldThrottle = 0, coastImpulse = 0;
        int phase = loggedCoast ? 2 : 0;
        bool upper = false, finished = false;
        double pitchAtCutoff = pitch, horizontalAtCutoff = vt - 408 * radius / Planet;
        for (int step = 0; step < 45000; step++)
        {
            double time = step * Dt, altitude = radius - Planet;
            double surfaceTangent = vt - 408 * radius / Planet;
            double speed = Math.Sqrt(vr * vr + surfaceTangent * surfaceTangent);
            double density = 1.225 * Math.Exp(-altitude / 7200);
            double q = 0.5 * density * speed * speed;
            double thrust = upper ? 955522 : 19293340 - (19293340 - 17770000) * Math.Exp(-altitude / 7000);
            double flow = upper ? 222.897 : 4553.8984;
            double acceleration = thrust / mass, orbitRate = vt / radius * Deg;
            var orbit = OrbitInsertion.CalculateOrbit(Mu, radius, vr, vt);
            double ap = orbit.ApoapsisRadius - Planet, pe = orbit.PeriapsisRadius - Planet;
            highestAp = Math.Max(highestAp, ap);
            if (OrbitInsertion.OrbitAchieved(ap, pe, 200000, 200000)) { finished = true; break; }
            double throttle = 0, desiredPitch = 90;
            if (phase == 0 && GravityTurnController.ReadyToTurn(altitude, profile.GravityTurnStartM, speed,
                profile.GravityTurnMinSpeedMps, vr, pitch)) { phase = 1; turn.Reset(pitch); }
            if (phase <= 1)
            {
                throttle = OrbitInsertion.AscentThrottle(Mu, Planet, radius, vr, vt, 200000, acceleration, pitch, Dt, out _);
                if (phase == 1)
                {
                    double targetPitch = GravityTurnController.ProfilePitch(altitude, profile.GravityTurnStartM,
                        profile.GravityTurnEndM, profile.GravityTurnShape);
                    targetPitch = GravityTurnController.LimitPitchForApoapsis(targetPitch, altitude, ap, 200000);
                    targetPitch = GravityTurnController.LimitPitchInAtmosphere(targetPitch,
                        Math.Atan2(vr, surfaceTangent) * Deg, speed, q);
                    desiredPitch = turn.Update(targetPitch, pitch, Math.Abs(turn.CommandedPitchDeg - pitch),
                        Math.Abs(bodyRate), GravityTurnController.RateLimit(altitude, profile.MaxPitchRateDegS), Dt);
                    if (ap >= 199400 && throttle < 0.15)
                    {
                        phase = 2; coastTime = time; pitchAtCutoff = pitch; horizontalAtCutoff = surfaceTangent;
                        commandPitch = pitch; gate.Reset(); throttle = 0;
                    }
                }
            }
            if (phase >= 2)
            {
                double apRadius = Planet + ap;
                double deltaV = Math.Max(0, OrbitInsertion.CaptureTangentialSpeed(Mu, apRadius, Planet + 200000,
                    Planet + 200000) - radius * vt / apRadius);
                double duration = OrbitInsertion.BurnDuration(deltaV, mass, thrust, flow);
                double secondsToAp = TimeToAp(radius, vr, vt);
                if (phase == 2 && OrbitInsertion.ReadyToBurn(altitude, secondsToAp, duration, vr))
                { phase = 3; insertionTime = time; }
                var command = OrbitInsertion.Capture(Mu, radius, vr, vt, Planet + 200000, Planet + 200000, acceleration);
                desiredPitch = phase == 2 ? 0 : command.PitchDeg;
                double error = Math.Abs(Wrap(desiredPitch - pitch));
                bool window = phase == 3 || altitude >= 100000
                    && (secondsToAp <= Math.Max(30, OrbitInsertion.BurnLead(duration) + error / 2 + 10)
                        || vr <= 0 || Math.Abs(bodyRate) > 0.3);
                bool settling = gate.Update(error, Math.Abs(bodyRate), false, window);
                throttle = phase == 3 && error <= 5 ? Math.Max(command.Throttle, settling ? 0.01 : 0)
                    : settling ? 0.05 : 0;
                throttle = OrbitInsertion.GuardInsertionThrottle(Mu, Planet, radius, vr, vt, 200000,
                    acceleration, pitch, throttle, Dt, out _);
                commandPitch += orbitRate * Dt;
                double limit = phase == 2 ? 2 : 3;
                if (Math.Abs(Wrap(commandPitch - pitch)) <= 4 && Math.Abs(bodyRate) <= limit + 0.3)
                    commandPitch += Math.Clamp(Wrap(desiredPitch - commandPitch), -limit * Dt, limit * Dt);
                if (Environment.GetEnvironmentVariable("KSA_SIM_TRACE") == "1" && step % 500 == 0)
                    Console.WriteLine(FormattableString.Invariant($"t={time:F0} phase={phase} alt={altitude/1000:F2} ap={ap/1000:F2} pe={pe/1000:F2} vr={vr:F1} vt={vt:F1} m={mass:F0} pitch={pitch:F2}/{desiredPitch:F2} rate={bodyRate:F2} throttle={throttle:F4}"));
                if (ap > 210500 || altitude < 100000 && vr < 0) break;
            }
            else commandPitch = desiredPitch;
            double pulse = gate.NeedsThrust && (Math.Abs(Wrap(desiredPitch - pitch)) > 5 || Math.Abs(bodyRate) > 0.3) ? 0.12 : 0.001;
            bool pulseSafe = OrbitInsertion.GuardImpulse(Mu, Planet, radius, vr, vt, phase < 2 ? 200000 : 210000,
                acceleration, pitch, 0.2, Math.Max(Dt, pulse), out _) >= 0.2 - 1e-6;
            throttle = actuator.Command(throttle, 0.2, Dt, pulseSafe, pulse);
            double rateLimit = phase < 2 ? GravityTurnController.RateLimit(altitude, profile.MaxPitchRateDegS) : phase == 2 ? 2 : 3;
            double requestedRate = Math.Clamp(Wrap(commandPitch - pitch) / 0.4, -rateLimit, rateLimit) - orbitRate;
            double authority = throttle > 0 && oldThrottle > 0 ? 75 * acceleration / 142 * throttle : 0;
            bodyRate += Math.Clamp((requestedRate - bodyRate) / 0.25, -authority, authority) * Dt;
            pitch = Wrap(pitch + (bodyRate + orbitRate) * Dt);
            oldThrottle = throttle;
            if (phase == 2) coastImpulse += acceleration * throttle * Dt;
            double drag = q * 170 / mass;
            double ar = vt * vt / radius - Mu / (radius * radius) + acceleration * throttle * Math.Sin(pitch / Deg)
                - (speed > 0.1 ? drag * vr / speed : 0);
            double at = -vr * vt / radius + acceleration * throttle * Math.Cos(pitch / Deg)
                - (speed > 0.1 ? drag * surfaceTangent / speed : 0);
            radius += vr * Dt; vr += ar * Dt; vt += at * Dt;
            mass -= flow * throttle * Dt;
            if (!upper && mass < 59000) { mass = 46900; upper = true; }
            if (mass < 12000 || altitude < 0 || !orbit.Bound) break;
        }
        var final = OrbitInsertion.CalculateOrbit(Mu, radius, vr, vt);
        Console.WriteLine(FormattableString.Invariant($"  no-RCS {(loggedCoast ? "logged coast" : "mission")}: cutoff T+{coastTime:F1}s, pitch={pitchAtCutoff:F1}°, horizontal={horizontalAtCutoff:F0} m/s; insertion T+{insertionTime:F1}s; Ap={(final.ApoapsisRadius-Planet)/1000:F2}/Pe={(final.PeriapsisRadius-Planet)/1000:F2} km; max Ap={highestAp/1000:F2} km; coast impulse={coastImpulse:F1} m/s"));
        if (!finished) throw new InvalidOperationException("Physical no-RCS ascent/insertion did not achieve LEO");
        if (coastImpulse > 200) throw new InvalidOperationException("Coast alignment wastes insertion fuel");
    }

    private static double TimeToAp(double radius, double vr, double vt)
    {
        if (vr <= 0) return 0;
        double energy = 0.5 * (vr * vr + vt * vt) - Mu / radius;
        double a = -Mu / (2 * energy), h = radius * vt;
        double e = Math.Sqrt(Math.Max(0, 1 - h * h / (Mu * a)));
        if (e < 1e-8) return 0;
        double eccentricAnomaly = Math.Atan2(vr * radius / (e * Math.Sqrt(Mu * a)), (1 - radius / a) / e);
        double meanAnomaly = eccentricAnomaly - e * Math.Sin(eccentricAnomaly);
        return Math.Max(0, (Math.PI - meanAnomaly) / Math.Sqrt(Mu / (a * a * a)));
    }

    private static double Wrap(double angle) => (angle + 540) % 360 - 180;
}
