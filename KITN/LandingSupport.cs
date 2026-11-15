using System;
using System.Collections.Generic;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class LandingPad
    {
        public readonly string Key;
        public readonly double Latitude, Longitude;
        public readonly double3 DirectionCcf;
        public double Altitude, FootprintRadius = 50;
        public LandingPad(Celestial body, LandmarkReference location)
        {
            Key = location.Key;
            Latitude = location.Latitude.GetRadians(); Longitude = location.Longitude.GetRadians();
            // This native conversion takes degrees although the landmark references
            // and the controller's stored coordinates use radians.
            DirectionCcf = body.GetDirCcfFromLatLon(location.Latitude.ToDegrees(), location.Longitude.ToDegrees());
        }
        public void ResolveSurface(Celestial body)
        {
            Altitude = body.GetTerrainHeightFromDirCcf(DirectionCcf, true);
            StaticObject platform = body.GetLocation(Key)?.GetStaticObject();
            if (platform != null)
            {
                Altitude += platform.GroundOffset + platform.SurfaceHeight;
                FootprintRadius = Math.Max(25, platform.FootprintRadius);
            }
            if (!double.IsFinite(Altitude)) throw new InvalidOperationException("Landing pad has no valid terrain height");
        }
    }

    internal readonly struct LandingPropulsion
    {
        public readonly double Thrust, MassFlow, MinimumThrottle, MinimumPulse, UsableFuel;
        public readonly bool Restartable, FuelValid;
        public LandingPropulsion(double thrust, double flow, double minimum, double pulse, double fuel, bool restartable, bool valid)
        { Thrust = thrust; MassFlow = flow; MinimumThrottle = minimum; MinimumPulse = pulse; UsableFuel = fuel; Restartable = restartable; FuelValid = valid; }
        public double AvailableDeltaV(double mass) => LandingMath.AvailableDeltaV(mass, UsableFuel, MassFlow > 0 ? Thrust / MassFlow : 0);

        public static LandingPropulsion Read(Vehicle vehicle)
        {
            if (vehicle?.Parts == null) return default;
            var groups = new Dictionary<ReactantMix, HashSet<Tank>>();
            var allTanks = new HashSet<Tank>();
            ReadOnlySpan<MoleState> states = vehicle.Parts.Moles.States;
            bool restartable = false, valid = true;
            double minimum = Math.Max(0.001, vehicle.GetMinThrottle()), pulse = 0.001;
            foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
            {
                if (!engine.IsActive) continue;
                minimum = Math.Max(minimum, engine.MinimumThrottle);
                foreach (RocketCore core in engine.Cores)
                {
                    minimum = Math.Max(minimum, core.MinimumThrottle); pulse = Math.Max(pulse, core.MinimumPulseTime);
                    if (!(core is PlumbedCore liquid))
                    {
                        // A still-burning solid core cannot be shut down for a landing.
                        if (core.ComputePropellantAvailable(states, true)) valid = false;
                        continue;
                    }
                    restartable = true;
                    if (liquid.Mix == null) { valid = false; continue; }
                    // FeedTanks contains local feed bindings only. KSA actually consumes from
                    // the resource manager's graph, including tanks reached through connectors
                    // and the selected same-stage/cross-stage flow rule.
                    var manager = liquid.ResourceManager;
                    if (manager == null || !manager.ConsumptionOrder.IsValid) { valid = false; continue; }
                    if (!groups.TryGetValue(liquid.Mix, out HashSet<Tank> tanks))
                        groups[liquid.Mix] = tanks = new HashSet<Tank>();
                    var order = manager.ConsumptionOrder;
                    for (int level = 0; level < order.LevelCount; level++)
                        foreach (Tank tank in order[level])
                            if (tank?.PropellantUseEnabled == true) { tanks.Add(tank); allTanks.Add(tank); }
                }
            }
            double usableFuel = 0;
            foreach (var group in groups)
            {
                double mixMass = double.PositiveInfinity;
                foreach (Reactant reactant in group.Key.Reactants)
                {
                    if (!(reactant.MassFraction > 0)) { valid = false; continue; }
                    double mass = 0;
                    foreach (Tank tank in group.Value)
                    {
                        if (!tank.TryGetMole(reactant.SubstancePhase, out Mole mole)) continue;
                        if (mole.StatesIdx < 0 || mole.StatesIdx >= states.Length) { valid = false; continue; }
                        double stored = states[mole.StatesIdx].Mass;
                        if (!double.IsFinite(stored) || stored < 0) valid = false; else mass += stored;
                    }
                    mixMass = Math.Min(mixMass, mass / reactant.MassFraction);
                }
                if (!double.IsFinite(mixMass)) valid = false; else usableFuel += mixMass;
            }
            double unionMass = 0;
            foreach (Tank tank in allTanks) unionMass += tank.ComputeSubstanceMass(states);
            usableFuel = Math.Min(usableFuel, unionMass);
            if (!double.IsFinite(usableFuel) || usableFuel < 0) { usableFuel = 0; valid = false; }
            ActiveEnginePerformance performance = vehicle.FlightComputer?.ActiveEnginePerformanceMax ?? default;
            return new LandingPropulsion(performance.Thrust, performance.MassFlowRate, Math.Clamp(minimum, 0.001, 1),
                pulse, usableFuel, restartable, valid);
        }
    }

    internal sealed class LandingReturnPlan
    {
        public double BurnTime, HandoverTime, DeltaV, ReserveDeltaV;
        public LandingVector DeltaVelocity;
        public Orbit OriginalOrbit;
        public LandingPrediction Prediction;
    }

    /// <summary>Incremental window search; every game API read stays in the synchronized hook.</summary>
    internal sealed class LandingReturnPlanner
    {
        private sealed class Candidate
        {
            public double Delay, Duration, Cost;
            public LandingVector Position, Velocity, TargetVelocity;
        }
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly LandingBodyModel _body;
        private readonly LandingVector _padAtEpoch;
        private readonly double _padAltitude, _epoch, _lead, _available, _exhaustVelocity;
        private readonly Orbit _orbit;
        private int _window, _refinement;
        private const int DurationCount = 19, DelayCount = 721;
        public bool Complete { get; private set; }
        public LandingReturnPlan Plan { get; private set; }
        public double Progress => Complete ? 1 : Math.Min(0.95, (double)_window / (DurationCount * DelayCount) * 0.9);

        public LandingReturnPlanner(Vehicle vehicle, LandingBodyModel body, LandingVector pad, double padAltitude, double time,
            double lead, double available, double exhaustVelocity)
        {
            _orbit = Orbit.CreateFrom(vehicle.Orbit);
            _body = body; _padAtEpoch = pad; _padAltitude = padAltitude; _epoch = time; _lead = lead; _available = available;
            _exhaustVelocity = exhaustVelocity;
        }

        public void Step()
        {
            if (Complete) return;
            if (_window < DurationCount * DelayCount)
            {
                for (int batch = 0; batch < 96 && _window < DurationCount * DelayCount; batch++, _window++)
                {
                    double delay = _lead + (_window / DurationCount) * (_body.Settings.SearchWindowHours * 3600 / (DelayCount - 1));
                    double duration = 600 + (_window % DurationCount) * 200.0;
                    UniverseTime time = Time(_epoch + delay);
                    LandingVector position = Vector(_orbit.GetPositionCci(time)), velocity = Vector(_orbit.GetVelocityCci(time));
                    LandingVector end = LandingVector.Rotate(_padAtEpoch, _body.AngularVelocity, delay + duration).Unit
                        * (_body.Radius + _padAltitude + _body.Settings.HandoverHeightM);
                    if (!LandingMath.Lambert(_body.Mu, position, end, LandingVector.Cross(position, velocity), duration, out var target)) continue;
                    double cost = (target - velocity).Length;
                    if (cost + 350 > _available || cost < 1 || target.LengthSquared >= 2 * _body.Mu / position.Length) continue;
                    _candidates.Add(new Candidate { Delay = delay, Duration = duration, Cost = cost + delay * 0.0001,
                        Position = position, Velocity = velocity, TargetVelocity = target });
                    _candidates.Sort((a, b) => a.Cost.CompareTo(b.Cost));
                    if (_candidates.Count > 16) _candidates.RemoveAt(_candidates.Count - 1);
                }
                return;
            }
            if (_refinement < _candidates.Count)
            {
                Candidate candidate = _candidates[_refinement++];
                LandingVector pad = LandingVector.Rotate(_padAtEpoch, _body.AngularVelocity, candidate.Delay);
                if (LandingMath.TargetReturn(_body, candidate.Position, candidate.Velocity, candidate.TargetVelocity, pad, _padAltitude,
                    _exhaustVelocity, out var target, out var prediction))
                {
                    double deltaV = (target - candidate.Velocity).Length, reserve = LandingMath.LandingReserve(_body, prediction);
                    if (deltaV + reserve <= _available && (Plan == null || deltaV + reserve + candidate.Delay * 0.0001
                        < Plan.DeltaV + Plan.ReserveDeltaV + (Plan.BurnTime - _epoch) * 0.0001))
                        Plan = new LandingReturnPlan { BurnTime = _epoch + candidate.Delay, HandoverTime = _epoch + candidate.Delay + prediction.Seconds,
                            DeltaV = deltaV, ReserveDeltaV = reserve, DeltaVelocity = target - candidate.Velocity,
                            Prediction = prediction, OriginalOrbit = _orbit };
                }
                return;
            }
            Complete = true;
        }

        // The public double constructor accepts seconds; FromNanoseconds is the
        // separate API for integer nanoseconds.
        internal static UniverseTime Time(double seconds) => new UniverseTime(seconds);
        internal static LandingVector Vector(double3 vector) => new LandingVector(vector.X, vector.Y, vector.Z);
        internal static double3 Native(LandingVector vector) => new double3(vector.X, vector.Y, vector.Z);
    }
}
