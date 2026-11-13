using System;
using System.Collections.Generic;
using KSA;

namespace KITN
{
    /// <summary>Reads only the parts removed by each radial connector and queues KSA's native decouple input.</summary>
    internal sealed class BoosterMonitor
    {
        private sealed class Candidate
        {
            public Decoupler Decoupler;
            public readonly BoosterSeparation Confirmation = new BoosterSeparation();
            public double RequestedAt = double.NaN;
            public int Attempts;
            public double LastLogTime = double.NegativeInfinity;
        }

        private readonly Dictionary<ulong, Candidate> _candidates = new Dictionary<ulong, Candidate>();
        private readonly HashSet<ulong> _ignited = new HashSet<ulong>();
        private readonly List<Part> _branchParts = new List<Part>();
        private readonly HashSet<Part> _branchSet = new HashSet<Part>();
        private readonly List<Tank> _tanks = new List<Tank>();
        private readonly List<EngineController> _engines = new List<EngineController>();
        private readonly List<ThrusterController> _thrusters = new List<ThrusterController>();
        private readonly List<ulong> _removed = new List<ulong>();
        private readonly HashSet<ulong> _fuelStoreIds = new HashSet<ulong>();
        private readonly List<SolidGrainSegment> _grains = new List<SolidGrainSegment>();
        private readonly LaunchProfile _profile;
        private Vehicle _vehicle;
        private double _lastSimulationTime = double.NaN;
        public string Status { get; private set; } = "no_next_radial_booster";
        public int CandidateCount { get; private set; }
        public int PendingCount { get; private set; }
        public int DropRequestCount { get; private set; }
        public int ConfirmedDropCount { get; private set; }
        public double ConfirmationElapsed { get; private set; }
        public bool NextSequenceOnlyBoosters { get; private set; }

        public BoosterMonitor(LaunchProfile profile) { _profile = profile; }

        public void Reset()
        {
            _candidates.Clear(); _ignited.Clear(); _vehicle = null;
            _lastSimulationTime = double.NaN;
            CandidateCount = PendingCount = DropRequestCount = ConfirmedDropCount = 0;
            ConfirmationElapsed = 0;
            Status = "reset";
            NextSequenceOnlyBoosters = false;
        }

        public void Suspend()
        {
            foreach (Candidate candidate in _candidates.Values) candidate.Confirmation.Reset();
        }

        /// <returns>True if native separation was requested or is waiting to be applied.</returns>
        public bool Update(Vehicle vehicle, double simulationTime)
        {
            if (!ReferenceEquals(vehicle, _vehicle) || simulationTime < _lastSimulationTime) { Reset(); _vehicle = vehicle; }
            _lastSimulationTime = simulationTime;
            CandidateCount = PendingCount = 0;
            ConfirmationElapsed = 0;
            Status = "no_next_radial_booster";
            NextSequenceOnlyBoosters = false;
            _removed.Clear();
            foreach (var pair in _candidates)
            {
                Candidate candidate = pair.Value;
                if (double.IsNaN(candidate.RequestedAt)) continue;
                if (!vehicle.Parts.Contains(candidate.Decoupler.Parent.FullPart)
                    || candidate.Decoupler.Connector.Connection == null)
                {
                    ConfirmedDropCount++;
                    Diag.Info("BOOSTER", Diag.F($"event=separation_confirmed decoupler_id={pair.Key} requested_at_s={candidate.RequestedAt:F3} simulation_t_s={simulationTime:F3} confirmed_drops={ConfirmedDropCount}"));
                    _removed.Add(pair.Key);
                }
                else if (simulationTime < candidate.RequestedAt || simulationTime - candidate.RequestedAt >= 2)
                {
                    Diag.Warn("BOOSTER", Diag.F($"event=separation_not_applied decoupler_id={pair.Key} attempts={candidate.Attempts} simulation_t_s={simulationTime:F3}"));
                    candidate.RequestedAt = double.NaN;
                    candidate.Confirmation.Reset();
                }
                else PendingCount++;
            }
            foreach (ulong id in _removed) _candidates.Remove(id);

            var sequences = vehicle.Parts.SequenceList;
            int next = sequences.GetNextSequenceNumber();
            Sequence upcoming = null;
            foreach (Sequence sequence in sequences.Sequences)
                if (sequence.Number == next) { upcoming = sequence; break; }
            if (upcoming == null) return PendingCount > 0;
            bool onlyBoosters = true;
            bool requested = false;
            foreach (Part part in upcoming.Parts)
            {
                foreach (ISequenced module in part.GetSubtreeSequencedModules())
                {
                    if (module.Sequence != next) continue;
                    if (!(module is Decoupler decoupler) || !IsRadial(decoupler)) { onlyBoosters = false; continue; }
                    if (!decoupler.IsEnabled || decoupler.Connector.Connection == null) continue;
                    CandidateCount++;
                    if (!_candidates.TryGetValue(decoupler.InstanceId, out Candidate candidate))
                    {
                        candidate = new Candidate { Decoupler = decoupler };
                        _candidates.Add(decoupler.InstanceId, candidate);
                    }
                    if (!double.IsNaN(candidate.RequestedAt)) continue;
                    if (!TryGetRadialBranch(decoupler, out Part branch)) continue;
                    try
                    {
                        BoosterObservation observation = ReadFuel(vehicle, branch);
                        string previousReason = candidate.Confirmation.Reason;
                        bool empty = candidate.Confirmation.Update(observation, simulationTime);
                        ConfirmationElapsed = Math.Max(ConfirmationElapsed, candidate.Confirmation.ConfirmationElapsed);
                        Status = candidate.Confirmation.Reason;
                        double realTime = Environment.TickCount64 / 1000.0;
                        bool logDue = _profile.DebugLoggingEnabled && (previousReason != Status
                            || realTime - candidate.LastLogTime >= Math.Max(0.5, _profile.DebugLogIntervalSeconds));
                        if (!logDue && !empty) continue;
                        string details = Diag.F($"decoupler_id={decoupler.InstanceId} sequence={next} branch_part_id={branch.InstanceId} branch_parts={_branchParts.Count} fuel_stores={observation.FuelStores} usable_fill_fraction={observation.UsableFillFraction:F8} fuel_valid={observation.FuelDataValid} ignited_propulsion={observation.ActivatedPropulsion} future_propulsion={observation.FuturePropulsion} contains_control={observation.ContainsControl} confirmed_s={candidate.Confirmation.ConfirmationElapsed:F3} simulation_t_s={simulationTime:F3}");
                        if (logDue)
                        {
                            candidate.LastLogTime = realTime;
                            Diag.Debug("BOOSTER", "event=evaluated reason=" + Status + " " + details);
                            LogFuelStores(vehicle, decoupler.InstanceId);
                        }
                        if (!empty || candidate.Attempts >= 3) continue;
                        if (!vehicle.HasPhysicsBubble) { Status = "booster_separation_waiting_for_physics"; continue; }
                        // Do not activate the entire sequence: another booster may still have fuel,
                        // or it may also contain an upper-stage ignition or a payload decoupler.
                        decoupler.SetIsActive(vehicle, true);
                        candidate.RequestedAt = simulationTime;
                        candidate.Attempts++;
                        PendingCount++;
                        DropRequestCount++;
                        requested = true;
                        Diag.Info("BOOSTER", "event=separation_requested reason=" + Status + " " + details);
                        _profile.SetStatus("status.booster_drop");
                    }
                    catch (Exception ex)
                    {
                        candidate.Confirmation.Reset();
                        Status = "booster_read_failed";
                        Diag.EveryError("booster_" + decoupler.InstanceId, 5, "BOOSTER", ex, "Reading radial booster propellant failed; separation withheld");
                    }
                }
            }
            NextSequenceOnlyBoosters = onlyBoosters && CandidateCount > 0;
            if (PendingCount > 0) Status = "booster_separation_pending";
            return requested || PendingCount > 0;
        }

        private static bool IsRadial(Decoupler decoupler)
            => decoupler.Parent.FullPart.Template.HasEditorTag(EditorTag.Radial);

        internal static bool TryGetRadialBranch(Decoupler decoupler, out Part branch)
        {
            branch = null;
            if (!IsRadial(decoupler) || !decoupler.IsEnabled) return false;
            Part owner = decoupler.Parent.FullPart;
            Part other = decoupler.Connector.Connection?.OtherPart(owner)?.FullPart;
            if (other == null) return false;
            if (ReferenceEquals(owner.TreeParent, other)) branch = owner;
            else if (ReferenceEquals(other.TreeParent, owner)) branch = other;
            return branch != null && !branch.IsRoot;
        }

        private BoosterObservation ReadFuel(Vehicle vehicle, Part branch)
        {
            _branchParts.Clear(); _branchSet.Clear(); _tanks.Clear(); _engines.Clear(); _thrusters.Clear();
            _fuelStoreIds.Clear(); _grains.Clear();
            _branchParts.Add(branch);
            bool control = false;
            for (int i = 0; i < _branchParts.Count; i++)
            {
                Part part = _branchParts[i];
                if (!_branchSet.Add(part)) continue;
                control |= part.SubtreeModules.HasAny<Control>() || ReferenceEquals(part, vehicle.ControlPart?.FullPart);
                _branchParts.AddRange(part.TreeChildren);
                foreach (Tank tank in part.SubtreeModules.Get<Tank>()) _tanks.Add(tank);
                foreach (EngineController engine in part.SubtreeModules.Get<EngineController>()) _engines.Add(engine);
                foreach (ThrusterController thruster in part.SubtreeModules.Get<ThrusterController>()) _thrusters.Add(thruster);
            }
            vehicle.Parts.EnsureDerived(DerivedData.SolidMotorStacks);
            ReadOnlySpan<MoleState> states = vehicle.Parts.Moles.States;
            int activated = 0, future = 0, stores = 0;
            bool valid = true;
            double usableFill = 0;
            foreach (EngineController engine in _engines)
            {
                if (engine.IsActive) _ignited.Add(engine.InstanceId);
                if (!_ignited.Contains(engine.InstanceId)) { future++; continue; }
                activated++;
                foreach (RocketCore core in engine.Cores)
                    ReadCore(core, states, ref stores, ref usableFill, ref valid);
            }
            // Dedicated thruster-powered side tanks are also supported. RCS reserves on a
            // conventional booster must not mask exhaustion of its main engine propellant.
            if (_engines.Count == 0)
                foreach (ThrusterController thruster in _thrusters)
                {
                    if (thruster.IsActive) _ignited.Add(thruster.InstanceId);
                    if (!_ignited.Contains(thruster.InstanceId)) { future++; continue; }
                    activated++;
                    foreach (RocketCore core in thruster.Cores)
                        ReadCore(core, states, ref stores, ref usableFill, ref valid);
                }
            return new BoosterObservation(true, control, valid, activated, future, stores, usableFill);
        }

        private void ReadCore(RocketCore core, ReadOnlySpan<MoleState> states,
            ref int stores, ref double maximumUsableFill, ref bool valid)
        {
            if (core is SolidMotor solid)
            {
                double remaining = 0, initial = 0;
                if (!solid.Stack.IsValid) { valid = false; return; }
                foreach (SolidGrainSegment segment in solid.Stack.Segments)
                {
                    if (!_branchSet.Contains(segment.FullPart)) continue;
                    if (segment.Grain == null) { valid = false; continue; }
                    double mass = states[segment.Grain.StatesIdx].Mass;
                    if (!double.IsFinite(mass)) { valid = false; continue; }
                    remaining += Math.Max(0, mass - segment.UnburnableGrainMass);
                    initial += Math.Max(0, segment.InitialGrainMass - segment.UnburnableGrainMass);
                    if (_fuelStoreIds.Add(segment.InstanceId)) { stores++; _grains.Add(segment); }
                }
                if (initial <= 0) valid = false;
                else maximumUsableFill = Math.Max(maximumUsableFill, remaining / initial);
                return;
            }
            if (!(core is PlumbedCore liquid) || liquid.Mix == null) { valid = false; return; }
            double mixFill = double.PositiveInfinity;
            bool anyReactant = false;
            foreach (Reactant reactant in liquid.Mix.Reactants)
            {
                double fullestTank = 0;
                bool present = false;
                foreach (Tank tank in _tanks)
                {
                    if (!tank.TryGetMole(reactant.SubstancePhase, out Mole mole)) continue;
                    present = true;
                    if (_fuelStoreIds.Add(tank.InstanceId)) stores++;
                    double fraction = mole.FilledFraction(in states[mole.StatesIdx]);
                    if (!double.IsFinite(fraction) || fraction < 0) valid = false;
                    else fullestTank = Math.Max(fullestTank, fraction);
                }
                if (present) { anyReactant = true; mixFill = Math.Min(mixFill, fullestTank); }
            }
            if (!anyReactant) valid = false;
            else maximumUsableFill = Math.Max(maximumUsableFill, mixFill);
        }

        private void LogFuelStores(Vehicle vehicle, ulong decouplerId)
        {
            ReadOnlySpan<MoleState> states = vehicle.Parts.Moles.States;
            foreach (Tank tank in _tanks)
            {
                if (!_fuelStoreIds.Contains(tank.InstanceId)) continue;
                Diag.Debug("BOOSTER", Diag.F($"event=fuel_state decoupler_id={decouplerId} kind=liquid tank_id={tank.InstanceId} mass_kg={tank.ComputeSubstanceMass(states):F6} fill_fraction={tank.FilledFraction(states):F8} propellant_use_enabled={tank.PropellantUseEnabled}"));
                foreach (Mole mole in tank.Moles)
                    Diag.Debug("BOOSTER", Diag.F($"event=reactant_state decoupler_id={decouplerId} tank_id={tank.InstanceId} substance=\"{mole.SubstancePhase.Name}\" mass_kg={states[mole.StatesIdx].Mass:F6} fill_fraction={mole.FilledFraction(in states[mole.StatesIdx]):F8}"));
            }
            foreach (SolidGrainSegment grain in _grains)
                Diag.Debug("BOOSTER", Diag.F($"event=fuel_state decoupler_id={decouplerId} kind=solid segment_id={grain.InstanceId} mass_kg={states[grain.Grain.StatesIdx].Mass:F6} unburnable_mass_kg={grain.UnburnableGrainMass:F6} initial_mass_kg={grain.InitialGrainMass:F6}"));
        }
    }
}
