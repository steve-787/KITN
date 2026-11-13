using System;
using System.Collections.Generic;
using System.Threading;

namespace KITN
{
    internal readonly struct RouteBurnPhase
    {
        public readonly double Thrust, MassFlow, StartMass, FullThrottleSeconds;
        public readonly bool StartsStage;
        public RouteBurnPhase(double thrust, double massFlow, double startMass, double seconds, bool startsStage)
        {
            Thrust = thrust; MassFlow = massFlow; StartMass = startMass;
            FullThrottleSeconds = seconds; StartsStage = startsStage;
        }
    }

    internal sealed class RoutePrediction
    {
        public double[] Distances = Array.Empty<double>();
        public double[] Altitudes = Array.Empty<double>();
        public bool ReachedOrbit;
        public double FinalApoapsisKm, FinalPeriapsisKm;
        public double FlightSeconds, RemainingDeltaV;
        public string MessageKey = "preview.select_vehicle";
        public string Message => L10n.Get(MessageKey);

        /// <summary>Planar prediction with finite staged burns and optional delayed attitude response.</summary>
        public static RoutePrediction Calculate(LaunchProfile profile, ReadOnlySpan<RouteBurnPhase> phases,
            double mu, double bodyRadius, double startAltitude, double radialSpeed, double tangentialSpeed,
            double rotationSpeed, double seaLevelDensity, double atmosphereHeight, double dragArea,
            double minimumThrottle, bool adaptive = false, double atmosphericThrustRatio = 1,
            double attitudeAccelerationDegS2 = 5, CancellationToken cancellation = default)
        {
            var result = new RoutePrediction();
            if (!double.IsFinite(mu) || mu <= 0 || !double.IsFinite(bodyRadius) || bodyRadius <= 0
                || !double.IsFinite(startAltitude) || !double.IsFinite(radialSpeed)
                || !double.IsFinite(tangentialSpeed) || !double.IsFinite(rotationSpeed)
                || phases.Length == 0)
            {
                result.MessageKey = "preview.no_stages";
                return result;
            }
            if (!float.IsFinite(profile.TargetApoapsisKm) || !float.IsFinite(profile.TargetPeriapsisKm)
                || profile.TargetPeriapsisKm < 100 || profile.TargetApoapsisKm < profile.TargetPeriapsisKm)
            {
                result.MessageKey = "preview.orbit_invalid";
                return result;
            }
            foreach (var p in phases)
                if (!double.IsFinite(p.Thrust) || p.Thrust <= 0 || !double.IsFinite(p.MassFlow) || p.MassFlow <= 0
                    || !double.IsFinite(p.StartMass) || p.StartMass <= 0
                    || !double.IsFinite(p.FullThrottleSeconds) || p.FullThrottleSeconds <= 0
                    || p.MassFlow * p.FullThrottleSeconds >= p.StartMass)
                {
                    result.MessageKey = "preview.insufficient_performance";
                    return result;
                }

            var distances = new List<double>(2048);
            var altitudes = new List<double>(2048);
            var turn = new GravityTurnController();
            var actuator = new ThrottleActuator();
            double radius = bodyRadius + Math.Max(0, startAltitude), vr = radialSpeed, vt = tangentialSpeed;
            double mass = phases[0].StartMass, pitch = 90, distance = 0, spent = 0, time = 0, bodyRate = 0;
            int stage = 0, state = 0;
            const double deg = 180 / Math.PI;
            double dt = 0.1;
            double density0 = double.IsFinite(seaLevelDensity) ? Math.Clamp(seaLevelDensity, 0, 100) : 0;
            double scaleHeight = Math.Max(1000, atmosphereHeight / 14);
            dragArea = double.IsFinite(dragArea) ? Math.Max(0, dragArea) : 0;
            result.MessageKey = "preview.ends_early";
            // Bound the work even for vehicles that cannot lift off or have very long burns.
            double maxTime = adaptive ? 14400 : 1800;
            double nextSample = 0;
            for (int step = 0; step <= (adaptive ? 100000 : 18000) && time <= maxTime; step++)
            {
                if ((step & 255) == 0) cancellation.ThrowIfCancellationRequested();
                double altitude = radius - bodyRadius;
                if (time >= nextSample)
                {
                    distances.Add(distance / 1000);
                    altitudes.Add(altitude / 1000);
                    nextSample = time + (adaptive ? 2 : 1);
                }
                var orbit = OrbitInsertion.CalculateOrbit(mu, radius, vr, vt);
                double ap = orbit.ApoapsisRadius - bodyRadius, pe = orbit.PeriapsisRadius - bodyRadius;
                result.FinalApoapsisKm = ap / 1000;
                result.FinalPeriapsisKm = pe / 1000;
                if (OrbitInsertion.OrbitAchieved(ap, pe, profile.TargetApoapsisM, profile.TargetPeriapsisM))
                {
                    result.ReachedOrbit = true;
                    result.MessageKey = "preview.complete";
                    break;
                }
                if (!orbit.Bound || altitude < -10 || !double.IsFinite(radius) || mass <= 0)
                {
                    result.MessageKey = "preview.unstable";
                    break;
                }
                var burn = phases[stage];
                if (spent >= burn.FullThrottleSeconds)
                {
                    if (stage + 1 >= phases.Length || !profile.AutoStaging && phases[stage + 1].StartsStage)
                    {
                        result.MessageKey = profile.AutoStaging ? "preview.fuel_exhausted" : "preview.manual_stage";
                        break;
                    }
                    burn = phases[++stage];
                    mass = burn.StartMass;
                    spent = 0;
                    actuator.Reset();
                }
                double surfaceTangent = vt - rotationSpeed * radius / bodyRadius;
                double speed = Math.Sqrt(vr * vr + surfaceTangent * surfaceTangent);
                double density = altitude >= atmosphereHeight ? 0 : density0 * Math.Exp(-Math.Max(0, altitude) / scaleHeight);
                double q = 0.5 * density * speed * speed;
                double thrustFactor = adaptive ? 1 - (1 - atmosphericThrustRatio) * Math.Exp(-Math.Max(0, altitude) / scaleHeight) : 1;
                double acceleration = burn.Thrust * thrustFactor / mass;
                dt = adaptive && state == 2 ? Math.Clamp(TimeToAp(mu, radius, vr, vt) / 100, 0.1, 2) : adaptive ? 0.2 : 0.1;
                if (state == 0 && GravityTurnController.ReadyToTurn(altitude, profile.GravityTurnStartM,
                    speed, profile.GravityTurnMinSpeedMps, vr, pitch))
                {
                    state = 1;
                    turn.Reset(pitch);
                }
                double throttle, desiredPitch = 90;
                if (state <= 1)
                {
                    throttle = OrbitInsertion.AscentThrottle(mu, bodyRadius, radius, vr, vt,
                        profile.TargetApoapsisM, acceleration, pitch, dt, out _);
                    if (state == 1)
                    {
                        double target = GravityTurnController.ProfilePitch(altitude, profile.GravityTurnStartM,
                            profile.GravityTurnEndM, profile.GravityTurnShape);
                        target = GravityTurnController.LimitPitchForApoapsis(target, altitude, ap, profile.TargetApoapsisM);
                        if (adaptive) target = AdaptiveAscentControl.Pitch(target, mu, bodyRadius, radius, vr, vt,
                            profile.TargetApoapsisM, acceleration, q, atmosphereHeight);
                        target = GravityTurnController.LimitPitchInAtmosphere(target,
                            Math.Atan2(vr, surfaceTangent) * deg, speed, q);
                        desiredPitch = turn.Update(target, pitch, adaptive ? Math.Abs(turn.CommandedPitchDeg - pitch) : 0,
                            adaptive ? Math.Abs(bodyRate) : 0,
                            GravityTurnController.RateLimit(altitude, profile.MaxPitchRateDegS), dt);
                    }
                    if (ap >= profile.TargetApoapsisM - 600 && throttle < 0.15)
                    {
                        state = 2;
                        throttle = 0;
                    }
                }
                else
                {
                    double apRadius = orbit.ApoapsisRadius;
                    double dv = Math.Max(0, OrbitInsertion.CaptureTangentialSpeed(mu, apRadius,
                        bodyRadius + profile.TargetPeriapsisM, bodyRadius + profile.TargetApoapsisM) - radius * vt / apRadius);
                    double duration = OrbitInsertion.BurnDuration(dv, mass, burn.Thrust, burn.MassFlow);
                    if (state == 2 && (adaptive ? OrbitInsertion.ReadyToBurn(altitude, TimeToAp(mu, radius, vr, vt), duration, vr, pe, atmosphereHeight)
                        : OrbitInsertion.ReadyToBurn(altitude, TimeToAp(mu, radius, vr, vt), duration, vr))) state = 3;
                    var command = OrbitInsertion.Capture(mu, radius, vr, vt,
                        bodyRadius + profile.TargetPeriapsisM, bodyRadius + profile.TargetApoapsisM, acceleration);
                    desiredPitch = state == 2 ? 0 : command.PitchDeg;
                    if (!adaptive) pitch += Math.Clamp(Wrap(desiredPitch - pitch), -3 * dt, 3 * dt);
                    throttle = state == 3 && Math.Abs(Wrap(desiredPitch - pitch)) <= 5 ? command.Throttle : 0;
                    throttle = OrbitInsertion.GuardInsertionThrottle(mu, bodyRadius, radius, vr, vt,
                        profile.TargetApoapsisM, acceleration, pitch, throttle, dt, out _);
                }
                if (adaptive)
                {
                    double rateLimit = state <= 1 ? GravityTurnController.RateLimit(altitude, profile.MaxPitchRateDegS) : 3;
                    double desiredRate = Math.Clamp(Wrap(desiredPitch - pitch) / 0.4, -rateLimit, rateLimit);
                    bodyRate += Math.Clamp((desiredRate - bodyRate) / 0.25, -attitudeAccelerationDegS2, attitudeAccelerationDegS2) * dt;
                    pitch = Math.Clamp(pitch + bodyRate * dt, -180, 180);
                }
                else if (state <= 1) pitch = desiredPitch;
                double minimum = double.IsFinite(minimumThrottle) ? Math.Clamp(minimumThrottle, 0, 1) : 0;
                bool safePulse = OrbitInsertion.GuardImpulse(mu, bodyRadius, radius, vr, vt,
                    state < 2 ? profile.TargetApoapsisM : OrbitInsertion.ApoapsisCeiling(profile.TargetApoapsisM),
                    acceleration, pitch, minimum, dt, out _) >= minimum - 1e-6;
                throttle = actuator.Command(throttle, minimum, dt, safePulse);
                throttle = Math.Min(throttle, Math.Max(0, (burn.FullThrottleSeconds - spent) / dt));
                double drag = q * dragArea / mass;
                double ar = vt * vt / radius - mu / (radius * radius) + acceleration * throttle * Math.Sin(pitch / deg)
                    - (speed > 0.1 ? drag * vr / speed : 0);
                double at = -vr * vt / radius + acceleration * throttle * Math.Cos(pitch / deg)
                    - (speed > 0.1 ? drag * surfaceTangent / speed : 0);
                distance += Math.Abs(vt / radius - rotationSpeed / bodyRadius) * bodyRadius * dt;
                radius += vr * dt; vr += ar * dt; vt += at * dt;
                mass -= burn.MassFlow * throttle * dt;
                spent += throttle * dt;
                time += dt;
            }
            if (double.IsFinite(radius) && double.IsFinite(distance))
            {
                distances.Add(distance / 1000);
                altitudes.Add((radius - bodyRadius) / 1000);
            }
            result.Distances = distances.ToArray();
            result.Altitudes = altitudes.ToArray();
            result.FlightSeconds = time;
            for (int i = stage; i < phases.Length; i++)
            {
                double initial = i == stage ? mass : phases[i].StartMass;
                double remainingSeconds = phases[i].FullThrottleSeconds - (i == stage ? spent : 0);
                double final = initial - phases[i].MassFlow * remainingSeconds;
                if (final > 0 && final < initial) result.RemainingDeltaV += phases[i].Thrust / phases[i].MassFlow * Math.Log(initial / final);
            }
            return result;
        }

        private static double TimeToAp(double mu, double radius, double vr, double vt)
        {
            double energy = 0.5 * (vr * vr + vt * vt) - mu / radius;
            if (energy >= 0) return double.NaN;
            double a = -mu / (2 * energy), h = radius * vt;
            double e = Math.Sqrt(Math.Max(0, 1 - h * h / (mu * a)));
            if (e < 1e-8) return 0;
            double anomaly = Math.Atan2(vr * radius / (e * Math.Sqrt(mu * a)), (1 - radius / a) / e);
            double mean = anomaly - e * Math.Sin(anomaly);
            double remaining = Math.PI - mean;
            if (remaining < 0) remaining += Math.PI * 2;
            return Math.Max(0, remaining / Math.Sqrt(mu / (a * a * a)));
        }

        private static double Wrap(double angle) => (angle + 540) % 360 - 180;
    }
}
