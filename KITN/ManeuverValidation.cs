using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KSA;

namespace KITN
{
    internal sealed partial class ManeuverPlanner
    {
        private Burn _candidateNode, _validatedDeparture;
        private FlightPlan _validatedPrediction;
        private IParentBody _departureBody;
        private TransferSolution _checkingSolution;
        private bool _candidateExecute, _replanPending, _validatedBestEffort;
        private string _routeProblem;
        private string _validatedAssistId;
        private float _suggestedPeriapsisKm;
        private double _minimumTransferApoapsis, _minimumTransferPeriapsis;
        public bool HasTransferApsisAdjustment => _minimumTransferApoapsis > 0 || _minimumTransferPeriapsis > 0;
        private long _predictionStarted;
        private readonly List<TransferWindowExclusion> _excludedWindows = new List<TransferWindowExclusion>();
        public bool IsChecking => _candidateNode != null;
        public bool NeedsBetterRoute => _routeProblem != null;
        public bool CanImproveRoute => NeedsBetterRoute && !IsSearching && !IsChecking && !IsExecuting && !HasOtherNodes;
        public float SuggestedPeriapsisKm => _suggestedPeriapsisKm;
        public double MissingSoiDeltaV { get; private set; } = double.NaN;

        private void ClearRouteProblem()
        {
            _routeProblem = null; _suggestedPeriapsisKm = 0; MissingSoiDeltaV = double.NaN;
        }

        private void ResetRouteAdjustments()
        {
            ClearRouteProblem();
            _minimumTransferApoapsis = 0; _minimumTransferPeriapsis = 0;
        }

        private void OfferBetterRoute(string reason, FlightPlan prediction = null, TransferSolution solution = default)
        {
            Celestial destination = RouteTarget;
            _routeProblem = reason; Message = reason; _suggestedPeriapsisKm = 0;
            MissingSoiDeltaV = reason == "transfer.no_encounter" ? EstimateSoiCorrection(prediction, solution, destination) : double.NaN;
            if (reason == "transfer.arrival_soi" && destination != null)
                _suggestedPeriapsisKm = (float)(DefaultArrivalAltitude(destination) / 1000);
            IParentBody central = ReferenceEquals(destination?.Parent, _vehicle?.Parent) ? _vehicle?.Parent : (_vehicle?.Parent as Celestial)?.Parent;
            if (prediction != null && destination != null && ReferenceEquals(destination.Parent, central))
            {
                foreach (var patch in prediction.Patches)
                {
                    if (!ReferenceEquals(patch.PrimaryBody, central) || patch.BurnTrajectory.HasValue) continue;
                    if (reason == "transfer.returning" || reason == "transfer.no_encounter")
                    {
                        double reach = destination.Orbit.Periapsis - destination.SphereOfInfluence;
                        if (patch.Orbit.IsBound() && patch.Orbit.Apoapsis < reach)
                            _minimumTransferApoapsis = Math.Max(_minimumTransferApoapsis, reach + (reach - patch.Orbit.Apoapsis));
                        double safeRadius = Math.Max(central.GetNearSurfaceRadius(), central.GetAtmosphereRadius());
                        double departurePeriapsis = ReferenceEquals(central, _vehicle.Parent) ? _vehicle.Orbit.Periapsis : (_snapshot?.Source?.Periapsis ?? patch.Orbit.Periapsis);
                        if (patch.Orbit.Periapsis < safeRadius)
                            _minimumTransferPeriapsis = Math.Max(_minimumTransferPeriapsis,
                                Math.Min(departurePeriapsis - 1000, safeRadius + (safeRadius - patch.Orbit.Periapsis) + 10000));
                    }
                    break;
                }
            }
            if (destination == null || (reason != "transfer.arrival_surface" && reason != "transfer.arrival_atmosphere"
                && reason != "transfer.invalid_periapsis")) return;
            double actualAltitude = double.NaN;
            if (prediction != null)
                foreach (var patch in prediction.Patches)
                    if (ReferenceEquals(patch.PrimaryBody, destination))
                    { actualAltitude = patch.Orbit.Periapsis - destination.MeanRadius; break; }
            _suggestedPeriapsisKm = (float)(TransferSafety.ImprovedArrivalAltitude(PeriapsisKm * 1000.0,
                destination.MeanRadius, destination.MaxTerrainRadius, ((IParentBody)destination).GetAtmosphereRadius(), actualAltitude) / 1000.0);
        }

        private void ImproveRoute(Vehicle vehicle)
        {
            if (!CanImproveRoute) return;
            if (_suggestedPeriapsisKm > 0)
            {
                float previous = PeriapsisKm;
                PeriapsisKm = _suggestedPeriapsisKm;
                // An altitude change gives even the old departure window a different trajectory.
                _excludedWindows.Clear();
                Diag.Info("TRANSFER", $"event=arrival_altitude_adjusted previous_km={previous:F3} next_km={PeriapsisKm:F3}");
            }
            // Vary flight time to find trajectories with the corrected apsides; do not modify the live parking orbit.
            if (HasTransferApsisAdjustment)
            {
                MinimumDuration = Math.Max(60, MinimumDuration * 0.75);
                MaximumDuration *= 1.5;
                Diag.Info("TRANSFER", $"event=transfer_apsides_adjusted minimum_ap_m={_minimumTransferApoapsis:F3} minimum_pe_m={_minimumTransferPeriapsis:F3}");
            }
            BeginSearch(vehicle, true);
        }

        private void BeginSearch(Vehicle vehicle, bool replan)
        {
            string unavailable = ValidateRoute(vehicle);
            if (unavailable != null) { Message = unavailable; _replanPending = false; return; }
            if (HasOtherNodes) { Message = "transfer.existing_nodes"; _replanPending = false; return; }
            string arrivalError = ArrivalError(RouteTarget, PeriapsisKm * 1000.0);
            if (arrivalError != null) { OfferBetterRoute(arrivalError); _replanPending = false; return; }
            Cancel(); Stale = Grid != null;
            var request = Snapshot(vehicle);
            request.Earliest = Now + EarliestDelay; request.Latest = request.Earliest + SearchWindow;
            request.MinimumDuration = MinimumDuration; request.MaximumDuration = MaximumDuration;
            request.Width = TransferMath.DepartureSamples(request);
            request.ExcludedWindows = _excludedWindows.ToArray();
            request.MinimumTransferApoapsis = _minimumTransferApoapsis;
            request.MinimumTransferPeriapsis = _minimumTransferPeriapsis;
            var phases = vehicle.FlightComputer.StagedBurn.ToArray();
            if (phases.Length == 0 && vehicle.FlightComputer.ActiveEnginePerformanceMax.Thrust > 0)
            {
                var engine = vehicle.FlightComputer.ActiveEnginePerformanceMax;
                phases = new[] { new FiniteBurnPhase(engine.Thrust, engine.MassFlowRate,
                    vehicle.TotalMass, double.PositiveInfinity) };
            }
            TransferBurnRefiner.Attach(request, phases);
            if (!request.Valid) { Message = "transfer.invalid_bounds"; return; }
            TransferRequest overview = request.Oberth ? request.CopyForPorkchop() : null;
            if (overview != null) TransferBurnRefiner.Attach(overview, phases);
            ClearRouteProblem();
            _snapshot = request; _progress = 0; _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _search = Task.Run(() => TransferMath.SearchWithPorkchop(request, overview, token, value => Volatile.Write(ref _progress, value)), token);
            Diag.Info("TRANSFER", Diag.F($"event=search_started target=\"{Target.Id}\" parking_mu={request.Parking.Mu:G17} target_orbit_mu={request.Target.Mu:G17} earliest_s={request.Earliest:F3} latest_s={request.Latest:F3} min_duration_s={request.MinimumDuration:F3} max_duration_s={request.MaximumDuration:F3} arrival_pe_km={PeriapsisKm:F3} phases={phases.Length} exclusions={request.ExcludedWindows.Length} gravity_assists={request.GravityAssists} assist_bodies={request.AssistBodies.Length}"));
            _replanPending = replan;
            Message = replan ? "transfer.replanning" : null;
        }

        private void QueueValidation(Vehicle vehicle, Burn burn, TransferSolution solution, bool execute)
        {
            _candidateNode = burn; _candidateExecute = execute; _checkingSolution = solution;
            _departureBody = vehicle.Parent; _predictionStarted = Environment.TickCount64;
            _validatedDeparture = null; _validatedPrediction = null;
            _validatedBestEffort = false; _validatedAssistId = null;
            Message = "transfer.checking";
            if (!execute) return;
            ClearThrustInputs(vehicle); vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            FlightAttitude.StopTracking(vehicle.FlightComputer);
            Universe.AutoWarpStop(true);
            if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
            _executingVehicle = vehicle; _executionPhase = ExecutionPhase.Validating;
        }

        private void ClearValidation()
        {
            _candidateNode = null; _validatedDeparture = null; _validatedPrediction = null;
            _candidateExecute = false;
            _validatedBestEffort = false; _validatedAssistId = null;
        }

        private void UpdateValidation(Vehicle vehicle, double now, bool controlLocked)
        {
            if (_candidateNode != null)
            {
                if (!ReferenceEquals(vehicle, _candidateNode.Vehicle) || !HasNode)
                { StopExecution(); ClearValidation(); return; }
                if (controlLocked) return;
                string error = vehicle.BurnPlan.FlightPlansOutOfDate ? "transfer.checking"
                    : InspectPrediction(_candidateNode.FlightPlan, _departureBody, _captureTarget, _checkingSolution.BestEffort);
                if (error == "transfer.checking" && Environment.TickCount64 - _predictionStarted < 15000) return;
                if (error == null) error = AssistPredictionError(_candidateNode.FlightPlan, _checkingSolution.Flyby?.Body.Id, _captureTarget?.Id);
                if (error != null) { RejectDeparture(vehicle, error); return; }
                Burn accepted = _candidateNode;
                bool execute = _candidateExecute;
                _candidateNode = null; _candidateExecute = false;
                _validatedDeparture = accepted; _validatedPrediction = accepted.FlightPlan;
                _validatedBestEffort = _checkingSolution.BestEffort;
                _validatedAssistId = _checkingSolution.Flyby?.Body.Id;
                Message = _validatedBestEffort ? "transfer.fallback_created" : "transfer.created";
                Diag.Info("TRANSFER", $"event=route_verified target=\"{_captureTarget.Id}\" best_effort={_validatedBestEffort}");
                if (execute) StartExecution(vehicle);
            }
            if (_replanPending && !IsSearching && !IsChecking && !IsExecuting && !controlLocked)
            {
                _replanPending = false;
                if (Selected.Valid) Message = Selected.BestEffort ? "transfer.fallback_selected" : "transfer.better_route_ready";
                else OfferBetterRoute("transfer.no_safe_route");
            }
        }

        private bool RecheckDeparture(Vehicle vehicle)
        {
            if (_validatedDeparture == null || !ReferenceEquals(_node, _validatedDeparture)) return true;
            if (vehicle.BurnPlan.FlightPlansOutOfDate) return false;
            if (ReferenceEquals(_validatedPrediction, _node.FlightPlan)) return true;
            string error = InspectPrediction(_node.FlightPlan, _departureBody, _captureTarget, _validatedBestEffort);
            if (error == null) error = AssistPredictionError(_node.FlightPlan, _validatedAssistId, _captureTarget?.Id);
            if (error == "transfer.checking") return false;
            if (error != null) { RejectDeparture(vehicle, error); return false; }
            _validatedPrediction = _node.FlightPlan;
            return true;
        }

        private void RejectDeparture(Vehicle vehicle, string reason)
        {
            Burn rejected = _candidateNode ?? _validatedDeparture;
            var prediction = rejected?.FlightPlan;
            var solution = _checkingSolution;
            LogPrediction(prediction, reason, solution);
            var request = Grid?.Request ?? _snapshot;
            double departureMargin = request == null ? 1 : Math.Max(1, (request.Latest - request.Earliest) / (request.Width - 1));
            double durationMargin = request == null ? 1 : Math.Max(1, (request.MaximumDuration - request.MinimumDuration) / (request.Height - 1));
            if (IsExecuting) EndExecution(reason);
            ClearCapture();
            if (rejected != null && vehicle.BurnPlan.TryGetBurn(rejected)) vehicle.FlightComputer.RemoveBurn(rejected);
            if (ReferenceEquals(_node, rejected)) _node = null;
            _excludedWindows.Add(new TransferWindowExclusion(solution.Departure, solution.Duration, departureMargin, durationMargin,
                reason == "transfer.returning" || reason == "transfer.no_encounter"));
            SelectedIndex = -1; _replanPending = false;
            OfferBetterRoute(reason, prediction, solution);
            Diag.Warn("TRANSFER", $"event=route_rejected reason={reason} departure_s={solution.Departure:F3} awaiting=user_route_search");
        }

        internal static double EstimateSoiCorrection(FlightPlan prediction, TransferSolution solution, Celestial target)
        {
            if (prediction == null || prediction.ImpactSearchUnresolved || !solution.Valid || target?.Orbit == null || target.Parent == null) return double.NaN;
            double arrival = solution.Departure + solution.Duration;
            bool postBurn = false;
            double cutoff = double.NegativeInfinity;
            PatchedConic coast = null;
            double correctionTime = double.NaN;
            foreach (var patch in prediction.Patches)
            {
                if (patch.BurnTrajectory.HasValue)
                {
                    postBurn = true;
                    cutoff = Math.Max(cutoff, patch.BurnTrajectory.Value.Cutoff.Seconds());
                    continue;
                }
                postBurn |= patch.StartTransition == PatchTransition.Burn;
                if (!postBurn || !ReferenceEquals(patch.PrimaryBody, target.Parent)) continue;
                double time = Math.Max(cutoff, patch.StartTime.Seconds());
                if (!double.IsFinite(time) || time >= arrival - 1 || patch.EndTime.Seconds() < arrival) continue;
                // The final central-body coast also handles predictions after a gravity assist.
                coast = patch; correctionTime = time;
            }
            if (coast == null) return double.NaN;
            try
            {
                var state = coast.Orbit.GetStateVectorsAt(new UniverseTime(correctionTime));
                var predictedArrival = coast.Orbit.GetStateVectorsAt(new UniverseTime(arrival));
                var targetArrival = target.Orbit.GetStateVectorsAt(new UniverseTime(arrival));
                return TransferSafety.SoiCorrectionDeltaV(coast.Orbit.Mu, Vector(state.PositionCci), Vector(state.VelocityCci),
                    Vector(predictedArrival.PositionCci), Vector(targetArrival.PositionCci), target.SphereOfInfluence, arrival - correctionTime);
            }
            catch (Exception) { return double.NaN; }
        }

        private static void LogPrediction(FlightPlan plan, string reason, TransferSolution solution)
        {
            Diag.Warn("TRANSFER", Diag.F($"event=prediction_rejected reason={reason} departure_s={solution.Departure:F3} duration_s={solution.Duration:F3} dv_mps={solution.DepartureDv:F3} best_effort={solution.BestEffort} unresolved={plan?.ImpactSearchUnresolved}"));
            if (plan == null) return;
            foreach (var patch in plan.Patches)
                Diag.Info("TRANSFER", Diag.F($"event=prediction_patch body=\"{patch.PrimaryBody?.Id}\" start_s={patch.StartTime.Seconds():F3} end_s={patch.EndTime.Seconds():F3} start={patch.StartTransition} end={patch.EndTransition} pe_m={patch.Orbit.Periapsis:F3} ap_m={patch.Orbit.Apoapsis:F3} ignition_s={patch.BurnTrajectory?.Ignition.Seconds():F3} cutoff_s={patch.BurnTrajectory?.Cutoff.Seconds():F3}"));
        }

        // Read the game's complete post-burn prediction, including the finite engine burn.
        // A bound ellipse is safe only if it enters the destination before returning to its origin.
        internal static string AssistPredictionError(FlightPlan plan, string assistId, string targetId)
        {
            if (assistId == null) return null;
            if (plan != null)
                foreach (var patch in plan.Patches)
                {
                    if (patch.PrimaryBody?.Id == targetId) break;
                    if (patch.PrimaryBody?.Id == assistId && patch.StartTransition == PatchTransition.Encounter) return null;
                }
            return "transfer.assist_missed";
        }

        internal static string PredictionError(FlightPlan plan, IParentBody origin, Celestial target)
            => InspectPrediction(plan, origin, target, false);

        internal static string InspectPrediction(FlightPlan plan, IParentBody origin, Celestial target, bool bestEffort)
        {
            if (plan == null || plan.Patches.Count == 0 || target == null || plan.ImpactSearchUnresolved) return "transfer.checking";
            // Distant-moon routes may never bypass the native final-encounter check.
            if (!ReferenceEquals(ResolveRouteTarget(origin, target), target)) bestEffort = false;
            bool computedBurn = false;
            foreach (PatchedConic patch in plan.Patches)
                computedBurn |= patch.StartTransition == PatchTransition.Burn || patch.BurnTrajectory.HasValue;
            if (!computedBurn) return "transfer.checking";
            PatchedConic encounter = null;
            foreach (PatchedConic patch in plan.Patches)
                if (ReferenceEquals(patch.PrimaryBody, target)) { encounter = patch; break; }
            foreach (PatchedConic patch in plan.Patches)
            {
                if (encounter != null && patch.StartTime >= encounter.StartTime) break;
                if (patch.EndTransition == PatchTransition.Impact) return "transfer.impact";
                if (!ReferenceEquals(patch.PrimaryBody, origin)
                    && patch.PrimaryBody is Celestial intermediate
                    && ArrivalError(intermediate, patch.Orbit.Periapsis - intermediate.MeanRadius) != null)
                {
                    UniverseTime? intermediatePe = patch.Orbit.GetNextPeriapsisTime(patch.StartTime);
                    if (intermediatePe.HasValue && !intermediatePe.Value.IsSaturated() && intermediatePe.Value <= patch.EndTime)
                        return "transfer.assist_unsafe";
                }
                if (!ReferenceEquals(patch.PrimaryBody, origin)) continue;
                UniverseTime coastStart = patch.BurnTrajectory?.Cutoff ?? patch.StartTime;
                UniverseTime? pe = patch.Orbit.GetNextPeriapsisTime(coastStart + 0.001);
                if (!bestEffort && pe.HasValue && !pe.Value.IsSaturated() && pe.Value > coastStart + 0.001 && pe.Value <= patch.EndTime
                    && (encounter == null || pe.Value < encounter.StartTime - 1)) return "transfer.returning";
            }
            if (encounter == null) return bestEffort ? null : "transfer.no_encounter";
            string arrivalError = ArrivalError(target, encounter.Orbit.Periapsis - target.MeanRadius);
            if (arrivalError != null) return arrivalError;
            UniverseTime? arrivalPe = encounter.Orbit.GetNextPeriapsisTime(encounter.StartTime);
            if (!arrivalPe.HasValue || arrivalPe.Value.IsSaturated() || arrivalPe.Value <= encounter.StartTime || arrivalPe.Value > encounter.EndTime)
                return "transfer.capture_unsafe";
            return null;
        }
    }
}
