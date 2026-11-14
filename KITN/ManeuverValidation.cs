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
        private bool _candidateExecute, _replanPending, _replanExecute;
        private int _replanAttempts;
        private long _predictionStarted;
        private readonly List<TransferWindowExclusion> _excludedWindows = new List<TransferWindowExclusion>();
        public bool IsChecking => _candidateNode != null;

        private void BeginSearch(Vehicle vehicle, bool replan)
        {
            string unavailable = ValidateRoute(vehicle);
            if (unavailable != null) { Message = unavailable; _replanPending = false; return; }
            if (HasOtherNodes) { Message = "transfer.existing_nodes"; _replanPending = false; return; }
            string arrivalError = ArrivalError(Target, PeriapsisKm * 1000.0);
            if (arrivalError != null) { Message = arrivalError; _replanPending = false; return; }
            Cancel(); Grid = null; SelectedIndex = -1; Stale = false;
            var request = Snapshot(vehicle);
            request.Earliest = Now + EarliestDelay; request.Latest = request.Earliest + SearchWindow;
            request.MinimumDuration = MinimumDuration; request.MaximumDuration = MaximumDuration;
            request.Width = TransferMath.DepartureSamples(request);
            request.ExcludedWindows = _excludedWindows.ToArray();
            if (!request.Valid) { Message = "transfer.invalid_bounds"; return; }
            _snapshot = request; _progress = 0; _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _search = Task.Run(() => TransferMath.Search(request, token, value => Volatile.Write(ref _progress, value)), token);
            _replanPending = replan;
            Message = replan ? "transfer.replanning" : null;
        }

        private void QueueValidation(Vehicle vehicle, Burn burn, TransferSolution solution, bool execute)
        {
            _candidateNode = burn; _candidateExecute = execute; _checkingSolution = solution;
            _departureBody = vehicle.Parent; _predictionStarted = Environment.TickCount64;
            _validatedDeparture = null; _validatedPrediction = null;
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
        }

        private void UpdateValidation(Vehicle vehicle, double now, bool controlLocked)
        {
            if (_candidateNode != null)
            {
                if (!ReferenceEquals(vehicle, _candidateNode.Vehicle) || !HasNode)
                { StopExecution(); ClearValidation(); return; }
                if (controlLocked) return;
                string error = vehicle.BurnPlan.FlightPlansOutOfDate ? "transfer.checking"
                    : PredictionError(_candidateNode.FlightPlan, _departureBody, _captureTarget);
                if (error == "transfer.checking" && Environment.TickCount64 - _predictionStarted < 15000) return;
                if (error != null) { RejectDeparture(vehicle, error); return; }
                Burn accepted = _candidateNode;
                bool execute = _candidateExecute;
                _candidateNode = null; _candidateExecute = false;
                _validatedDeparture = accepted; _validatedPrediction = accepted.FlightPlan;
                Message = "transfer.created";
                Diag.Info("TRANSFER", $"event=route_verified target=\"{_captureTarget.Id}\" native_encounter=True");
                if (execute) StartExecution(vehicle);
            }
            if (_replanPending && !IsSearching && !IsChecking && !IsExecuting && !controlLocked)
            {
                _replanPending = false;
                if (Selected.Valid) CreateNode(vehicle, _replanExecute);
                else Message = "transfer.no_safe_route";
            }
        }

        private bool RecheckDeparture(Vehicle vehicle)
        {
            if (_validatedDeparture == null || !ReferenceEquals(_node, _validatedDeparture)) return true;
            if (vehicle.BurnPlan.FlightPlansOutOfDate) return false;
            if (ReferenceEquals(_validatedPrediction, _node.FlightPlan)) return true;
            string error = PredictionError(_node.FlightPlan, _departureBody, _captureTarget);
            if (error == "transfer.checking") return false;
            if (error != null) { RejectDeparture(vehicle, error); return false; }
            _validatedPrediction = _node.FlightPlan;
            return true;
        }

        private void RejectDeparture(Vehicle vehicle, string reason)
        {
            Burn rejected = _candidateNode ?? _validatedDeparture;
            bool execute = _candidateExecute || IsExecuting;
            var solution = _checkingSolution;
            var request = Grid?.Request ?? _snapshot;
            double departureMargin = request == null ? 1 : Math.Max(1, (request.Latest - request.Earliest) / (request.Width - 1));
            double durationMargin = request == null ? 1 : Math.Max(1, (request.MaximumDuration - request.MinimumDuration) / (request.Height - 1));
            if (IsExecuting) EndExecution("transfer.replanning");
            ClearCapture();
            if (rejected != null && vehicle.BurnPlan.TryGetBurn(rejected)) vehicle.FlightComputer.RemoveBurn(rejected);
            if (ReferenceEquals(_node, rejected)) _node = null;
            _excludedWindows.Add(new TransferWindowExclusion(solution.Departure, solution.Duration, departureMargin, durationMargin));
            _replanAttempts++;
            Diag.Warn("TRANSFER", $"event=route_rejected reason={reason} departure_s={solution.Departure:F3} retry={_replanAttempts} engines=off");
            if (_replanAttempts > 3) { Message = "transfer.no_safe_route"; return; }
            _replanExecute = execute;
            BeginSearch(vehicle, true);
        }

        // Read the game's complete post-burn prediction, including the finite engine burn.
        // A bound ellipse is safe only if it enters the destination before returning to its origin.
        internal static string PredictionError(FlightPlan plan, IParentBody origin, Celestial target)
        {
            if (plan == null || plan.Patches.Count == 0 || target == null || plan.ImpactSearchUnresolved) return "transfer.checking";
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
                if (patch.EndTransition == PatchTransition.Impact) return "transfer.returning";
                if (!ReferenceEquals(patch.PrimaryBody, origin)) continue;
                UniverseTime coastStart = patch.BurnTrajectory?.Cutoff ?? patch.StartTime;
                UniverseTime? pe = patch.Orbit.GetNextPeriapsisTime(coastStart + 0.001);
                if (pe.HasValue && !pe.Value.IsSaturated() && pe.Value > coastStart + 0.001 && pe.Value <= patch.EndTime
                    && (encounter == null || pe.Value < encounter.StartTime - 1)) return "transfer.returning";
            }
            if (encounter == null) return "transfer.no_encounter";
            UniverseTime? arrivalPe = encounter.Orbit.GetNextPeriapsisTime(encounter.StartTime);
            if (!arrivalPe.HasValue || arrivalPe.Value.IsSaturated() || arrivalPe.Value <= encounter.StartTime || arrivalPe.Value > encounter.EndTime
                || ArrivalError(target, encounter.Orbit.Periapsis - target.MeanRadius) != null) return "transfer.capture_unsafe";
            return null;
        }
    }
}
