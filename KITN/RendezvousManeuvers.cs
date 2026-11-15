using System;
using System.Threading;
using System.Threading.Tasks;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed partial class RendezvousController
    {
        private bool _rendezvousRunning, _rendezvousLocal, _rendezvousBurning, _rendezvousWarped, _rendezvousArrivalPending;
        private Task<RendezvousSolution> _rendezvousSearch;
        private CancellationTokenSource _rendezvousCancellation;
        private RendezvousRequest _rendezvousRequest;
        private RendezvousSolution _rendezvousPlan;
        private Burn _rendezvousDeparture, _rendezvousArrival, _rendezvousNode;
        private BurnTarget _rendezvousBurn;
        private IParentBody _rendezvousParent;
        private double _rendezvousSettled = double.NaN;
        private double _rendezvousLead = 180;
        private long _rendezvousPredictionStarted;
        private int _rendezvousCorrections;
        private int _rendezvousCourseCorrections;
        private bool _rendezvousCorrecting;
        private LocalizedMessage _rendezvousMessageDetail;
        private LocalizedMessage _rendezvousArrivalFailure;
        public string MessageText => Message == null ? null : _rendezvousMessageDetail?.Key == Message
            ? _rendezvousMessageDetail.ToString() : L10n.Get(Message);
        private bool RendezvousWarpActive => _rendezvousWarped && (Universe.IsAutoWarpActive || Universe.SimulationSpeed > 1);
        public bool IsExecutingManeuver => _rendezvousRunning && _rendezvousNode != null && !RendezvousWarpActive;
        public bool IsRendezvousCoasting => _rendezvousRunning && !_rendezvousLocal
            && (_rendezvousSearch != null || RendezvousWarpActive || _rendezvousNode == null);
        public double PlannedDeltaV => _rendezvousPlan.Valid ? _rendezvousPlan.Cost : double.NaN;
        public double PlannedArrival => _rendezvousPlan.Valid ? _rendezvousPlan.Arrival : double.NaN;

        private bool HasForeignNodes(Vehicle vehicle)
        {
            int owned = 0;
            foreach (Burn node in new[] { _rendezvousDeparture, _rendezvousArrival })
                if (node != null && vehicle.BurnPlan.TryGetBurn(node)) owned++;
            return vehicle.BurnPlan.BurnCount > owned;
        }

        private string RendezvousProblem(Vehicle vehicle, bool allowRcs)
        {
            if (Target?.Orbit == null || !ReferenceEquals(vehicle.Parent, Target.Parent)) return "rendezvous.require_target";
            if (!allowRcs) return "rendezvous.rcs_disabled";
            if (HasForeignNodes(vehicle) || (vehicle.FlightComputer.BurnMode == FlightComputerBurnMode.Auto && !_rendezvousBurning))
                return "rendezvous.other_nodes";
            double safety = Math.Max(vehicle.Parent.GetNearSurfaceRadius(), vehicle.Parent.GetAtmosphereRadius());
            if (vehicle.Orbit?.IsBound() != true || !Target.Orbit.IsBound()
                || vehicle.Orbit.Periapsis <= safety || Target.Orbit.Periapsis <= safety
                || vehicle.Orbit.Apoapsis >= vehicle.Parent.SphereOfInfluence || Target.Orbit.Apoapsis >= vehicle.Parent.SphereOfInfluence)
                return "rendezvous.require_orbit";
            if (!ReadAuthority(vehicle)) return "rendezvous.require_rcs";
            if (Distance > DockingMath.MaximumRange)
            {
                ReadRendezvousPhases(vehicle, out string propulsionProblem);
                if (propulsionProblem != null) return propulsionProblem;
            }
            return null;
        }

        internal static FiniteBurnPhase[] ReadRendezvousPhases(Vehicle vehicle, out string problem)
        {
            problem = null;
            if (vehicle?.Parts == null || vehicle.FlightComputer == null) problem = "rendezvous.require_vehicle";
            else
            {
                int engines = 0, active = 0;
                foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
                { engines++; if (engine.IsActive) active++; }
                if (engines == 0) problem = "rendezvous.no_engines";
                else if (active == 0) problem = "rendezvous.engines_inactive";
                else
                {
                    // KSA's StagedBurn cache can be empty after spawning/staging a craft even
                    // while its liquid engines have fuel. Read their actual connected tanks.
                    var propulsion = LandingPropulsion.Read(vehicle);
                    if (!propulsion.Restartable) problem = "rendezvous.require_restartable";
                    else if (!propulsion.FuelValid) problem = "rendezvous.fuel_data";
                    else if (!(propulsion.UsableFuel > 0)) problem = "rendezvous.no_fuel";
                    else if (!(double.IsFinite(vehicle.TotalMass) && vehicle.TotalMass > propulsion.UsableFuel
                        && double.IsFinite(propulsion.Thrust) && propulsion.Thrust > 0
                        && double.IsFinite(propulsion.MassFlow) && propulsion.MassFlow > 0)) problem = "rendezvous.engine_data";
                    else return new[] { new FiniteBurnPhase(propulsion.Thrust, propulsion.MassFlow,
                        vehicle.TotalMass, propulsion.UsableFuel / propulsion.MassFlow) };
                }
            }
            return Array.Empty<FiniteBurnPhase>();
        }

        private void StartRendezvous(Vehicle vehicle)
        {
            _lastDockingRecovery = null; _dockingRetrying = false; _dockingRecovery.Reset();
            _activeVehicle = vehicle; _matching = true; _rendezvousRunning = true;
            _rendezvousParent = vehicle.Parent; _lastTime = Universe.GetElapsedSeconds();
            _rendezvousCorrections = _rendezvousCourseCorrections = 0; _rendezvousCorrecting = false;
            _rendezvousPlan = default; _rendezvousRequest = null;
            _standOff = Math.Max(RendezvousOptions.FinalDistanceM, vehicle.MeanRadius + Target.MeanRadius + 30);
            var fc = _savedComputer = vehicle.FlightComputer;
            _savedRate = fc.RateLimit; _savedDeadband = fc.AngleDeadband; _savedRoll = fc.RollMode;
            _savedThrust = fc.ManualThrustMode; _savedAttitudeMode = fc.AttitudeMode; _savedRcs = fc.RCSMode;
            _savedTrack = fc.AttitudeTrackTarget; _savedFrame = fc.AttitudeFrame; _savedCustom = fc.CustomAttitudeTarget;
            _rendezvousLead = Math.Max(RendezvousOptions.WarpLeadSeconds, 180 / Math.Clamp(fc.RateLimit * FlightAttitude.RAD_TO_DEG, 0.1, 1.5) + 30);
            Universe.AutoWarpStop(true); if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
            Universe.SetTarget(vehicle, Target, null);
            fc.BurnMode = FlightComputerBurnMode.Manual; FlightAttitude.StopTracking(fc);
            SetInputs(vehicle, ThrusterMapFlags.None);
            ReadMotion(vehicle);
            if (Distance <= DockingMath.MaximumRange && DockingMath.HasBrakingRoom(Distance, RelativeSpeed, _acceleration, CollisionClearance()))
                BeginLocalApproach();
            else QueueRendezvousSearch(vehicle, _lastTime);
            if (_rendezvousRunning) Diag.Info("RENDEZVOUS", $"event=rendezvous_start target=\"{Target.Id}\" distance_m={Distance:F2} local={_rendezvousLocal}");
        }

        private double CollisionClearance() => Math.Max(10, (_activeVehicle?.MeanRadius ?? 0) + (Target?.MeanRadius ?? 0) + 20);

        private RendezvousRequest SnapshotRendezvous(Vehicle vehicle, double now, FiniteBurnPhase[] phases)
        {
            var own = vehicle.Orbit.GetStateVectorsAt(new UniverseTime(now));
            var target = Target.Orbit.GetStateVectorsAt(new UniverseTime(now));
            var request = new RendezvousRequest
            {
                Now = now, Mu = vehicle.Orbit.Mu,
                Own = new TransferOrbit(vehicle.Orbit.Mu, now, Vector(own.PositionCci), Vector(own.VelocityCci)),
                Target = new TransferOrbit(Target.Orbit.Mu, now, Vector(target.PositionCci), Vector(target.VelocityCci)),
                SafetyRadius = Math.Max(vehicle.Parent.GetNearSurfaceRadius(), vehicle.Parent.GetAtmosphereRadius()),
                Soi = vehicle.Parent.SphereOfInfluence,
                StandOff = Math.Max(500, _standOff + 300),
                AlignmentLead = _rendezvousLead,
                MaximumPhasingOrbits = RendezvousOptions.MaximumPhasingOrbits,
                Close = (own.PositionCci - target.PositionCci).Length() < own.PositionCci.Length() / 25
            };
            double minimumThrottle = vehicle.Parts != null ? LandingPropulsion.Read(vehicle).MinimumThrottle : 0.1;
            request.PredictBurn = (departure, delta, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                var burnPhases = ThrottlePhases(phases, RendezvousThrottle(phases, delta.Length, minimumThrottle));
                double duration = FiniteBurnPredictor.Duration(burnPhases, delta.Length, out double delivered);
                double ignition = departure - duration / 2;
                if (delivered < delta.Length - 0.01 || ignition < now + request.AlignmentLead
                    || !request.Own.State(ignition, out var r, out var v)) return default;
                var arc = FiniteBurnPredictor.Fly(request.Mu, new UniverseTime(ignition), Native(r), Native(v),
                    Native(delta.Unit), burnPhases, delta.Length, double.PositiveInfinity);
                for (int sample = 0; sample <= 32; sample++)
                    if (arc.Sample(sample).PositionCci.Length() <= request.SafetyRadius) return default;
                return new TransferBurnState(arc.Cutoff.Seconds(), Vector(arc.PositionCci), Vector(arc.VelocityCci));
            };
            FiniteBurnPredictor.Duration(phases, 1e6, out double available);
            request.Budget = available * (1 - RendezvousOptions.ReservePercent / 100.0);
            return request;
        }

        internal static double RendezvousThrottle(ReadOnlySpan<FiniteBurnPhase> phases, double deltaV, double minimum)
        {
            // Spread short corrections over several seconds to reduce physics-step cutoff errors.
            double duration = FiniteBurnPredictor.Duration(phases, deltaV, out _);
            return Math.Clamp(duration / 4, Math.Clamp(minimum, 0.001, 1), 1);
        }

        internal static FiniteBurnPhase[] ThrottlePhases(FiniteBurnPhase[] phases, double throttle)
        {
            var result = new FiniteBurnPhase[phases.Length];
            for (int i = 0; i < phases.Length; i++)
                result[i] = new FiniteBurnPhase(phases[i].Thrust * throttle, phases[i].MassFlowRate * throttle,
                    phases[i].StartMass, phases[i].MaxSeconds / throttle, phases[i].StartsStage);
            return result;
        }

        private void QueueRendezvousSearch(Vehicle vehicle, double now) => QueueRendezvousPlan(vehicle, now, false);

        private void QueueRendezvousPlan(Vehicle vehicle, double now, bool correction)
        {
            double arrival = _rendezvousPlan.Valid ? _rendezvousPlan.Arrival : double.NaN;
            RemoveRendezvousNodes(vehicle);
            _rendezvousCorrecting = correction;
            _rendezvousLocal = false; Phase = DockingPhase.Planning;
            Message = correction ? "rendezvous.correcting" : "rendezvous.planning";
            SetInputs(vehicle, ThrusterMapFlags.None); vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            FlightAttitude.StopTracking(vehicle.FlightComputer);
            var phases = ReadRendezvousPhases(vehicle, out string propulsionProblem);
            if (phases.Length == 0) { FailRendezvous(propulsionProblem); return; }
            vehicle.FlightComputer.SetStagedBurn(phases);
            _rendezvousRequest = SnapshotRendezvous(vehicle, now, phases);
            _rendezvousCancellation = new CancellationTokenSource(); var token = _rendezvousCancellation.Token;
            var request = _rendezvousRequest;
            _rendezvousSearch = Task.Run(() =>
            {
                if (correction)
                {
                    var corrected = RendezvousMath.Correct(request, arrival, token);
                    if (corrected.Valid) return corrected;
                }
                return RendezvousMath.Search(request, token);
            }, token);
        }

        private bool CorrectArrival(Vehicle vehicle, double now)
        {
            if (_rendezvousArrivalFailure?.Key != "rendezvous.arrival_miss"
                && _rendezvousArrivalFailure?.Key != "rendezvous.target_clearance") return false;
            if (!AllowRendezvousCorrection(true)) return false;
            Diag.Info("RENDEZVOUS", $"event=course_correction attempt={_rendezvousCourseCorrections} time_s={now:F3} arrival_s={_rendezvousPlan.Arrival:F3} reason=\"{_rendezvousArrivalFailure}\"");
            QueueRendezvousPlan(vehicle, now, true);
            return true;
        }

        private bool AllowRendezvousCorrection(bool course)
        {
            if (!RendezvousOptions.CourseCorrections
                || _rendezvousCorrections + _rendezvousCourseCorrections >= RendezvousOptions.MaximumCorrections) return false;
            if (course) _rendezvousCourseCorrections++; else _rendezvousCorrections++;
            return true;
        }

        private void BeginLocalApproach()
        {
            _rendezvousLocal = true; Phase = DockingPhase.Approaching; Message = "rendezvous.approaching";
            _settledSince = double.NaN;
            _activeVehicle.FlightComputer.SetManualThrustMode(FlightComputerManualThrustMode.Pulse);
            FlightAttitude.StopTracking(_activeVehicle.FlightComputer);
        }

        private void UpdateRendezvous(Vehicle vehicle, double now, bool allowRcs)
        {
            try
            {
                if (!ReferenceEquals(vehicle, _activeVehicle) || !vehicle.IsControllable || Target == null
                    || !ReferenceEquals(vehicle.Parent, _rendezvousParent) || !ReferenceEquals(Target.Parent, _rendezvousParent) || now < _lastTime)
                { FailRendezvous("rendezvous.target_changed"); return; }
                if (!allowRcs) { FailRendezvous("rendezvous.rcs_disabled"); return; }
                if (HasForeignNodes(vehicle)) { FailRendezvous("rendezvous.other_nodes"); return; }
                if (now <= _lastTime) return;
                _lastTime = now;
                ReadMotion(vehicle);
                if (_rendezvousLocal) { UpdateLocalApproach(vehicle, now); return; }
                if (_rendezvousRequest != null && _rendezvousRequest.Target.State(now, out var plannedTargetR, out var plannedTargetV)
                    && ((Native(plannedTargetR) - Target.GetPositionCci()).Length() > 100
                        || (Native(plannedTargetV) - Target.GetVelocityCci()).Length() > 1))
                {
                    if (_rendezvousBurning || !AllowRendezvousCorrection(false)) { FailRendezvous("rendezvous.target_changed"); return; }
                    _rendezvousCancellation?.Cancel(); _rendezvousCancellation?.Dispose(); _rendezvousCancellation = null;
                    var stale = _rendezvousSearch; _rendezvousSearch = null;
                    if (stale != null) _ = stale.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    QueueRendezvousSearch(vehicle, now); return;
                }
                SetInputs(vehicle, ThrusterMapFlags.None);
                if (_rendezvousSearch != null)
                {
                    if (!_rendezvousSearch.IsCompleted) return;
                    _rendezvousPlan = _rendezvousSearch.GetAwaiter().GetResult(); _rendezvousSearch = null;
                    _rendezvousCancellation?.Dispose(); _rendezvousCancellation = null;
                    if (!_rendezvousPlan.Valid)
                    {
                        double required = _rendezvousRequest.MinimumSafeCost, available = _rendezvousRequest.Budget;
                        if (double.IsFinite(required) && required > available)
                            FailRendezvous("rendezvous.insufficient_dv", required, available, required - available);
                        else FailRendezvous("rendezvous.no_route");
                        return;
                    }
                    if (!SearchStillMatches(vehicle, now))
                    {
                        if (!AllowRendezvousCorrection(false)) { FailRendezvous("rendezvous.target_changed"); return; }
                        QueueRendezvousSearch(vehicle, now); return;
                    }
                    _rendezvousDeparture = CreateRendezvousNode(vehicle, vehicle.FlightPlan, _rendezvousPlan.Departure, Native(_rendezvousPlan.DepartureDelta));
                    if (_rendezvousDeparture == null) { FailRendezvous("rendezvous.no_route"); return; }
                    _rendezvousNode = _rendezvousDeparture; _rendezvousPredictionStarted = 0;
                    Phase = DockingPhase.Transferring; Message = "rendezvous.checking";
                    Diag.Info("RENDEZVOUS", $"event=route_planned correction={_rendezvousCorrecting} departure_s={_rendezvousPlan.Departure:F3} arrival_s={_rendezvousPlan.Arrival:F3} dv_mps={_rendezvousPlan.Cost:F3} revolutions={_rendezvousPlan.Revolutions}");
                }
                if (_rendezvousArrivalPending)
                {
                    if (vehicle.BurnPlan.FlightPlansOutOfDate) { PredictionTimeout(); return; }
                    _rendezvousArrivalPending = false;
                    if (!CreateArrivalNode(vehicle, vehicle.FlightPlan))
                    {
                        if (!CorrectArrival(vehicle, now)) FailRendezvous(_rendezvousArrivalFailure);
                        return;
                    }
                    _rendezvousNode = _rendezvousArrival; _rendezvousPredictionStarted = 0;
                    Diag.Info("RENDEZVOUS", $"event=braking_planned time_s={_rendezvousArrival.Time.Seconds():F3} throttle={_rendezvousArrival.PlannedThrottle:F3}");
                }
                if (_rendezvousNode != null) UpdateRendezvousBurn(vehicle, now);
            }
            catch (OperationCanceledException) { FailRendezvous("rendezvous.stopped"); }
            catch (Exception ex) { Diag.Error("RENDEZVOUS", ex, "Rendezvous control failed"); FailRendezvous("rendezvous.failed_detail", ex.Message); }
        }

        private bool SearchStillMatches(Vehicle vehicle, double now)
        {
            if (!_rendezvousRequest.Own.State(now, out var ownR, out var ownV)
                || !_rendezvousRequest.Target.State(now, out var targetR, out var targetV)) return false;
            return (Native(ownR) - vehicle.GetPositionCci()).Length() < 100 && (Native(ownV) - vehicle.GetVelocityCci()).Length() < 1
                && (Native(targetR) - Target.GetPositionCci()).Length() < 100 && (Native(targetV) - Target.GetVelocityCci()).Length() < 1;
        }

        private Burn CreateRendezvousNode(Vehicle vehicle, FlightPlan prediction, double time, double3 delta)
        {
            var epoch = new UniverseTime(time); var patch = prediction?.TryFindPatch(epoch);
            if (patch == null || !ReferenceEquals(patch.PrimaryBody, _rendezvousParent) || !delta.IsFinite()) return null;
            var vlf = OrbitalTransfers.CciToVlf(patch.Orbit, epoch, delta);
            var node = Burn.Create(patch.Orbit.GetPointAt(epoch), time, vlf, patch, vehicle);
            double minimum = vehicle.Parts != null ? LandingPropulsion.Read(vehicle).MinimumThrottle : 0.1;
            node.PlannedThrottle = (float)RendezvousThrottle(vehicle.FlightComputer.StagedBurn, delta.Length(), minimum);
            vehicle.FlightComputer.AddBurn(node); return node;
        }

        private bool CreateArrivalNode(Vehicle vehicle, FlightPlan prediction)
        {
            _rendezvousArrivalFailure = new LocalizedMessage("rendezvous.no_prediction");
            // Refresh native finite-burn prediction after departure consumed propellant.
            if (vehicle.Parts != null)
            {
                var phases = ReadRendezvousPhases(vehicle, out string problem);
                if (phases.Length == 0) { _rendezvousArrivalFailure = new LocalizedMessage(problem); return false; }
                vehicle.FlightComputer.SetStagedBurn(phases);
            }
            var epoch = new UniverseTime(_rendezvousPlan.Arrival);
            var patch = prediction?.TryFindPatch(epoch);
            if (patch == null) return false;
            if (!ReferenceEquals(patch.PrimaryBody, _rendezvousParent))
            { _rendezvousArrivalFailure = new LocalizedMessage("rendezvous.body_change"); return false; }
            var ship = patch.Orbit.GetStateVectorsAt(epoch); var target = Target.Orbit.GetStateVectorsAt(epoch);
            double distance = (ship.PositionCci - target.PositionCci).Length();
            if (distance > DockingMath.MaximumRange)
            { _rendezvousArrivalFailure = new LocalizedMessage("rendezvous.arrival_miss", distance, DockingMath.MaximumRange); return false; }
            if (distance < CollisionClearance())
            { _rendezvousArrivalFailure = new LocalizedMessage("rendezvous.target_clearance", distance, CollisionClearance()); return false; }
            _rendezvousArrival = CreateRendezvousNode(vehicle, prediction, _rendezvousPlan.Arrival, target.VelocityCci - ship.VelocityCci);
            return _rendezvousArrival != null;
        }

        internal static string RendezvousPredictionError(FlightPlan plan, IParentBody parent, Orbit target, double arrival, double clearance)
        {
            if (plan == null || plan.ImpactSearchUnresolved || plan.Patches.Count == 0) return "rendezvous.checking";
            foreach (var patch in plan.Patches)
            {
                if (patch.StartTime.Seconds() > arrival + 300) break;
                if (!ReferenceEquals(patch.PrimaryBody, parent)) return "rendezvous.body_change";
                if (patch.EndTransition == PatchTransition.Impact && patch.EndTime.Seconds() <= arrival + 300) return "rendezvous.impact";
                if (!patch.BurnTrajectory.HasValue) continue;
                var arc = patch.BurnTrajectory.Value;
                for (int i = 0; i <= 32; i++)
                {
                    double time = arc.Ignition.Seconds() + (arc.Cutoff.Seconds() - arc.Ignition.Seconds()) * i / 32;
                    var body = target.GetStateVectorsAt(new UniverseTime(time));
                    if ((arc.Sample(i).PositionCci - body.PositionCci).Length() < clearance) return "rendezvous.target_collision";
                }
            }
            return null;
        }

        private void UpdateRendezvousBurn(Vehicle vehicle, double now)
        {
            var fc = vehicle.FlightComputer;
            if (_rendezvousBurning)
            {
                bool complete = fc.AutoBurnCompleted || (_rendezvousBurn?.StartedAt.HasValue == true
                    && float3.Dot(_rendezvousBurn.DeltaVToGoCci, _rendezvousBurn.DeltaVTargetCci) <= 0);
                if (complete || !ReferenceEquals(fc.Burn, _rendezvousBurn))
                {
                    fc.BurnMode = FlightComputerBurnMode.Manual; FlightAttitude.StopTracking(fc);
                    if (!complete) { FailRendezvous("rendezvous.burn_interrupted"); return; }
                    bool first = ReferenceEquals(_rendezvousNode, _rendezvousDeparture);
                    if (vehicle.BurnPlan.TryGetBurn(_rendezvousNode)) fc.RemoveBurn(_rendezvousNode);
                    _rendezvousNode = null; _rendezvousBurn = null; _rendezvousBurning = _rendezvousWarped = false;
                    if (first)
                    {
                        Diag.Info("RENDEZVOUS", $"event=burn_complete type={(_rendezvousCorrecting ? "correction" : "departure")} time_s={now:F3}");
                        _rendezvousDeparture = null;
                        if (_rendezvousArrival != null && vehicle.BurnPlan.TryGetBurn(_rendezvousArrival)) fc.RemoveBurn(_rendezvousArrival);
                        _rendezvousArrival = null;
                        _rendezvousArrivalPending = true; _rendezvousPredictionStarted = 0;
                        Phase = DockingPhase.Coasting; Message = "rendezvous.coasting";
                    }
                    else
                    {
                        Diag.Info("RENDEZVOUS", $"event=burn_complete type=braking time_s={now:F3}");
                        _rendezvousArrival = null;
                        ReadMotion(vehicle);
                        if (Distance <= DockingMath.MaximumRange && DockingMath.HasBrakingRoom(Distance, RelativeSpeed, _acceleration, CollisionClearance()))
                            BeginLocalApproach();
                        else if (AllowRendezvousCorrection(false)) QueueRendezvousSearch(vehicle, now);
                        else FailRendezvous("rendezvous.no_route");
                    }
                    _rendezvousSettled = double.NaN;
                    return;
                }
                if (fc.BurnMode != FlightComputerBurnMode.Auto || Universe.IsAutoWarpActive || Universe.SimulationSpeed > 1)
                    FailRendezvous("rendezvous.burn_interrupted");
                return;
            }
            fc.BurnMode = FlightComputerBurnMode.Manual;
            if (!vehicle.BurnPlan.TryGetBurn(_rendezvousNode)) { FailRendezvous("rendezvous.burn_interrupted"); return; }
            if (vehicle.BurnPlan.FlightPlansOutOfDate) { PredictionTimeout(); return; }
            string error = RendezvousPredictionError(_rendezvousNode.FlightPlan, _rendezvousParent, Target.Orbit, _rendezvousPlan.Arrival, CollisionClearance());
            if (error != null) { if (error == "rendezvous.checking") PredictionTimeout(); else FailRendezvous(error); return; }
            if (ReferenceEquals(_rendezvousNode, _rendezvousDeparture) && _rendezvousArrival == null)
            {
                if (!CreateArrivalNode(vehicle, _rendezvousDeparture.FlightPlan)) { FailRendezvous(_rendezvousArrivalFailure); return; }
                return;
            }
            if (_rendezvousArrival != null)
            {
                error = RendezvousPredictionError(_rendezvousArrival.FlightPlan, _rendezvousParent, Target.Orbit, _rendezvousPlan.Arrival, CollisionClearance());
                if (error != null) { if (error == "rendezvous.checking") PredictionTimeout(); else FailRendezvous(error); return; }
            }
            if (!ReferenceEquals(vehicle.BurnPlan.FindFirstExecutableBurn(), _rendezvousNode)) { FailRendezvous("rendezvous.other_nodes"); return; }
            var burn = fc.Burn;
            // Native burn targets are refreshed by the next physics step after replacing nodes.
            // Never use the previous maneuver's ignition time or attitude for the new node.
            if (burn == null || burn.IgnitionTime.IsSaturated() || !(burn.BurnDuration > 0)
                || Math.Abs(burn.ImpulsiveInstant.Seconds() - _rendezvousNode.Time.Seconds()) > 0.01)
            { PredictionTimeout(); return; }
            _rendezvousPredictionStarted = 0;
            double ignition = burn.IgnitionTime.Seconds();
            if (now > ignition + 5) { FailRendezvous("rendezvous.missed_burn"); return; }
            if (_rendezvousWarped)
            {
                if (Universe.IsAutoWarpActive || Universe.SimulationSpeed > 1) return;
            }
            else if (Universe.IsAutoWarpActive || Universe.SimulationSpeed > 1)
            { Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false); }
            fc.TrackTarget(FlightComputerAttitudeTrackTarget.PositiveDv);
            double angle = FlightAttitude.Angle(FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)), FlightAttitude.Forward(burn.BurnBody2Cci));
            double3 rates = vehicle.GetBodyRates().Transform(fc.Ctrl2Asmb.Inverse()) * FlightAttitude.RAD_TO_DEG;
            bool settled = angle <= 1 && TransferSafety.AxisSettled(rates.Y, fc.RateBit.Y * FlightAttitude.RAD_TO_DEG)
                && TransferSafety.AxisSettled(rates.Z, fc.RateBit.Z * FlightAttitude.RAD_TO_DEG)
                && (fc.RollMode == FlightComputerRollMode.Decoupled || (Math.Abs(fc.ErrorAngles.X) * FlightAttitude.RAD_TO_DEG <= 1
                    && TransferSafety.AxisSettled(rates.X, fc.RateBit.X * FlightAttitude.RAD_TO_DEG)));
            if (!settled) _rendezvousSettled = double.NaN;
            else if (!double.IsFinite(_rendezvousSettled)) _rendezvousSettled = now;
            bool firstBurn = ReferenceEquals(_rendezvousNode, _rendezvousDeparture);
            Phase = firstBurn ? DockingPhase.Transferring : DockingPhase.Braking;
            Message = firstBurn ? "rendezvous.departure" : "rendezvous.braking";
            if (RendezvousOptions.AutoWarp && !_rendezvousWarped && settled && ignition - now > _rendezvousLead + 5 && Distance > DockingMath.MaximumRange)
            {
                _rendezvousWarped = true; _rendezvousSettled = double.NaN;
                FlightAttitude.StopTracking(fc);
                Universe.AutoWarpTo(new UniverseTime(ignition - _rendezvousLead), 3, isUncapped: true);
                Diag.Info("RENDEZVOUS", $"event=coast_warp type={(firstBurn ? (_rendezvousCorrecting ? "correction" : "departure") : "braking")} stop_s={ignition - _rendezvousLead:F3}");
                Phase = DockingPhase.Coasting; Message = "rendezvous.coasting"; return;
            }
            if (TransferSafety.CanIgnite(now, ignition, settled && now - _rendezvousSettled >= 1, Universe.IsAutoWarpActive, Universe.SimulationSpeed))
            {
                _rendezvousBurn = burn; _rendezvousBurning = true; fc.BurnMode = FlightComputerBurnMode.Auto;
                Diag.Info("RENDEZVOUS", $"event=ignition type={(firstBurn ? (_rendezvousCorrecting ? "correction" : "departure") : "braking")} time_s={now:F3} throttle={_rendezvousNode.PlannedThrottle:F3}");
            }
        }

        private void PredictionTimeout()
        {
            Message = "rendezvous.checking";
            if (_rendezvousPredictionStarted == 0) _rendezvousPredictionStarted = Environment.TickCount64;
            if (Environment.TickCount64 - _rendezvousPredictionStarted > 15000) FailRendezvous("rendezvous.no_prediction");
        }

        private void UpdateLocalApproach(Vehicle vehicle, double now)
        {
            PhysicsWarp.LimitForControl(vehicle);
            if (!vehicle.HasPhysicsBubble || !Target.HasPhysicsBubble || !ReadAuthority(vehicle)) { FailRendezvous("rendezvous.require_physics"); return; }
            if (Distance < CollisionClearance()) { FailRendezvous("rendezvous.too_fast"); return; }
            // Translation needs no new pointing target; damp rotation in the inertial frame.
            FlightAttitude.StopTracking(vehicle.FlightComputer);
            var displacement = vehicle.GetPositionCci() - Target.GetPositionCci();
            var desired = RendezvousMath.ClosingVelocity(Vector(displacement), _acceleration, _standOff,
                RendezvousOptions.MaximumClosingSpeedMps, RendezvousOptions.BrakingMargin);
            var relative = vehicle.GetVelocityCci() - Target.GetVelocityCci();
            var delta = (Native(desired) - relative).Transform(FlightAttitude.ControlToCci(vehicle).Inverse());
            SetInputs(vehicle, TranslationFlags(delta, _minimumDeltaV));
            Phase = DockingPhase.Approaching; Message = "rendezvous.approaching";
            if (RendezvousMath.Arrived(Distance, RelativeSpeed, _standOff, RendezvousOptions.PositionToleranceM, RendezvousOptions.VelocityToleranceMps))
            {
                if (!double.IsFinite(_settledSince)) _settledSince = now;
                if (now - _settledSince >= RendezvousOptions.SettleSeconds)
                {
                    Stop(); Phase = DockingPhase.Rendezvoused; Message = "rendezvous.complete";
                    Diag.Info("RENDEZVOUS", $"event=complete distance_m={Distance:F2} relative_speed_mps={RelativeSpeed:F3}");
                }
            }
            else _settledSince = double.NaN;
        }

        private void FailRendezvous(string message, params object[] values)
            => FailRendezvous(new LocalizedMessage(message, values));

        private void FailRendezvous(LocalizedMessage message)
        {
            Stop(); _rendezvousMessageDetail = message; Message = message.Key;
            Diag.Warn("RENDEZVOUS", $"event=failed reason={Message} detail=\"{MessageText}\"");
        }

        private void RemoveRendezvousNodes(Vehicle vehicle)
        {
            foreach (Burn node in new[] { _rendezvousDeparture, _rendezvousArrival })
                if (node != null && vehicle.BurnPlan.TryGetBurn(node)) vehicle.FlightComputer.RemoveBurn(node);
            _rendezvousDeparture = _rendezvousArrival = _rendezvousNode = null;
            _rendezvousBurn = null; _rendezvousBurning = _rendezvousWarped = false;
            _rendezvousArrivalPending = false;
        }

        private void StopRendezvous()
        {
            if (!_rendezvousRunning) return;
            _rendezvousCancellation?.Cancel(); _rendezvousCancellation?.Dispose(); _rendezvousCancellation = null;
            var worker = _rendezvousSearch; _rendezvousSearch = null;
            if (worker != null) _ = worker.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _activeVehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            Universe.AutoWarpStop(true);
            RemoveRendezvousNodes(_activeVehicle);
            _rendezvousRunning = _rendezvousLocal = false;
        }
    }
}
