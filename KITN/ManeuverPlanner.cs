using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Snapshots game state at the physics boundary; workers never access mutable KSA objects.</summary>
    internal sealed partial class ManeuverPlanner : IDisposable
    {
        public readonly List<Celestial> Bodies = new List<Celestial>();
        public Celestial Target { get; private set; }
        public string OriginName { get; private set; } = "—";
        public string UnavailableReason { get; private set; } = "transfer.require_orbit";
        public string Message { get; private set; }
        public bool CanSearch => UnavailableReason == null;
        public bool IsSearching => _search != null;
        public bool IsExecuting => _executingVehicle != null;
        public bool HasNode => _node != null && ReferenceEquals(_node.Vehicle, _vehicle) && _node.Vehicle.BurnPlan.TryGetBurn(_node);
        public bool HasOtherNodes => _vehicle != null && _vehicle.BurnPlan.BurnCount > (HasNode ? 1 : 0);
        public bool Stale { get; private set; }
        public int Progress => Volatile.Read(ref _progress);
        public TransferGrid Grid { get; private set; }
        public int SelectedIndex { get; private set; } = -1;
        public TransferSolution Selected => Grid != null && SelectedIndex >= 0 && SelectedIndex < Grid.Cells.Length
            ? ReferenceEquals(Grid, _selectedGrid) && _selectedSolution.Valid ? _selectedSolution : Grid.Cells[SelectedIndex] : default;
        private TransferSolution _selectedSolution;
        private TransferGrid _selectedGrid;
        public double Now { get; private set; }
        public double EarliestDelay = 300, SearchWindow = 86400, MinimumDuration = 3600, MaximumDuration = 86400;
        public float PeriapsisKm = 100, MaximumDv = 12000;
        public bool IncludeCapture = true, AutoWarp = true;
        public bool PreferEarliest;
        public bool GravityAssists;
        public bool OberthMode;
        public bool CanSearchOberth => CanSearch && (string.Equals(_vehicle?.Parent?.Id, "Earth", StringComparison.OrdinalIgnoreCase)
            || string.Equals((_vehicle?.Parent as Celestial)?.BodyTemplate?.Id, "Earth", StringComparison.OrdinalIgnoreCase));
        private Vehicle _vehicle, _executingVehicle;
        private IParentBody _parent;
        private CelestialSystem _system;
        private Task<TransferGrid> _search;
        private CancellationTokenSource _cancellation;
        private TransferRequest _snapshot;
        private doubleQuat _parkingToCentral = doubleQuat.Identity;
        private Burn _node;
        private int _progress;
        private double _lastRefresh = double.NegativeInfinity;

        public void SelectTarget(Celestial target)
        {
            if (ReferenceEquals(Target, target)) return;
            Cancel(); Grid = null; SelectedIndex = -1; Target = target; _snapshot = null;
            Message = null; _lastRefresh = double.NegativeInfinity;
            _excludedWindows.Clear(); ResetRouteAdjustments();
        }

        public void Refresh(Vehicle vehicle, double now)
        {
            bool reverted = now < Now - 0.01;
            bool changed = !ReferenceEquals(vehicle, _vehicle) || !ReferenceEquals(vehicle?.Parent, _parent)
                || !ReferenceEquals(Universe.CurrentSystem, _system) || now < Now - 0.01;
            Now = now;
            if (changed)
            {
                if (!ReferenceEquals(vehicle, _vehicle) || reverted || !ReferenceEquals(Universe.CurrentSystem, _system))
                { StopExecution(); ClearCapture(); _captureSuppressed = reverted; }
                Cancel(); Grid = null; SelectedIndex = -1; _snapshot = null; Stale = false;
                _excludedWindows.Clear(); ResetRouteAdjustments();
                _vehicle = vehicle; _parent = vehicle?.Parent; _lastRefresh = double.NegativeInfinity;
                if (!ReferenceEquals(Universe.CurrentSystem, _system)) Target = null;
                _system = Universe.CurrentSystem;
            }
            if (now - _lastRefresh < 0.5) { Harvest(); return; }
            _lastRefresh = now;
            Bodies.Clear();
            if (_system != null)
                foreach (var body in _system.All.OfType<Celestial>())
                    if (!body.IsStar()) Bodies.Add(body);
            Bodies.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
            if (Target != null && !Bodies.Contains(Target)) SelectTarget(null);
            if (Target == null)
            {
                foreach (var body in Bodies)
                    if (!ReferenceEquals(body, _parent) && (ReferenceEquals(body.Parent, _parent)
                        || (_parent is Celestial origin && ReferenceEquals(body.Parent, origin.Parent)))) { Target = body; break; }
            }
            OriginName = _parent?.Id ?? "—";
            UnavailableReason = ValidateRoute(vehicle);
            if (CanSearch && _snapshot == null)
            {
                _snapshot = Snapshot(vehicle);
                SetDefaults(_snapshot);
            }
            if (Grid != null || IsSearching)
            {
                var request = Grid?.Request ?? _snapshot;
                if (!CanSearch || request == null || !OrbitStillMatches(vehicle, request))
                { Stale = true; Cancel(); }
            }
            Harvest();
            if (_node != null && !HasNode) _node = null;
        }

        private string ValidateRoute(Vehicle vehicle)
        {
            if (vehicle?.Orbit == null || !vehicle.Orbit.IsBound() || !vehicle.IsControllable) return "transfer.require_orbit";
            if (vehicle.Orbit.Periapsis <= Math.Max(vehicle.Parent.GetNearSurfaceRadius(), vehicle.Parent.GetAtmosphereRadius())) return "transfer.unsafe_orbit";
            if (vehicle.Orbit.Apoapsis >= vehicle.Parent.SphereOfInfluence) return "transfer.soi_orbit";
            if (Target == null) return "transfer.require_target";
            if (ReferenceEquals(Target, vehicle.Parent)) return "transfer.parent_target";
            if (ReferenceEquals(Target.Parent, vehicle.Parent)) return null;
            if (vehicle.Parent is Celestial origin && ReferenceEquals(origin.Parent, Target.Parent)) return null;
            return "transfer.route_hierarchy";
        }

        private TransferRequest Snapshot(Vehicle vehicle)
        {
            bool ejection = !ReferenceEquals(Target.Parent, vehicle.Parent);
            IParentBody central = ejection ? ((Celestial)vehicle.Parent).Parent : vehicle.Parent;
            _parkingToCentral = doubleQuat.Concatenate(vehicle.Parent.GetCci2Cce(), central.GetCce2Cci());
            var state = vehicle.Orbit.GetStateVectorsAt(new UniverseTime(Now));
            var request = new TransferRequest
            {
                Now = Now, CentralMu = central.Mu,
                Parking = new TransferOrbit(vehicle.Orbit.Mu, Now,
                    Vector(state.PositionCci.Transform(_parkingToCentral)), Vector(state.VelocityCci.Transform(_parkingToCentral))),
                Target = SnapshotOrbit(Target.Orbit), TargetMu = ((IParentBody)Target).Mu, TargetRadius = Target.MeanRadius, TargetSoi = Target.SphereOfInfluence,
                DepartureSafetyRadius = Math.Max(vehicle.Parent.GetNearSurfaceRadius(), vehicle.Parent.GetAtmosphereRadius()),
                DepartureSoi = ejection ? vehicle.Parent.SphereOfInfluence : 0,
                CentralSoi = central.SphereOfInfluence,
                CentralSafetyRadius = Math.Max(central.GetNearSurfaceRadius(), central.GetAtmosphereRadius()),
                GravityAssists = GravityAssists,
                Oberth = OberthMode,
                PeriapsisAltitude = Math.Max(0, PeriapsisKm) * 1000, IncludeCapture = IncludeCapture, UseBestEffortFallback = true
            };
            if (ejection) request.Source = SnapshotOrbit(((Celestial)vehicle.Parent).Orbit);
            if (GravityAssists && _system != null)
            {
                var assists = new List<TransferBodySnapshot>();
                foreach (var body in _system.All.OfType<Celestial>())
                {
                    if (ReferenceEquals(body, Target) || ReferenceEquals(body, vehicle.Parent)
                        || !ReferenceEquals(body.Parent, central) || body.Orbit == null || !body.Orbit.IsBound()) continue;
                    double safety = ArrivalSafetyRadius(body);
                    if (!(body.SphereOfInfluence > safety)) continue;
                    assists.Add(new TransferBodySnapshot(body.Id, SnapshotOrbit(body.Orbit), ((IParentBody)body).Mu,
                        body.MeanRadius, body.SphereOfInfluence, safety));
                }
                assists.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
                request.AssistBodies = assists.ToArray();
            }
            return request;
        }

        private TransferOrbit SnapshotOrbit(Orbit orbit)
        {
            var state = orbit.GetStateVectorsAt(new UniverseTime(Now));
            return new TransferOrbit(orbit.Mu, Now, Vector(state.PositionCci), Vector(state.VelocityCci));
        }

        private void SetDefaults(TransferRequest request)
        {
            if (request?.Parking.Valid != true || !request.Target.Valid) return;
            var source = request.Source ?? request.Parking;
            double axis = (source.A + request.Target.A) / 2;
            double hohmann = Math.PI * Math.Sqrt(axis * axis * axis / request.CentralMu);
            EarliestDelay = request.Ejection ? Math.Max(300, request.Parking.Period) : 300;
            SearchWindow = TransferMath.DefaultDepartureWindow(request);
            MinimumDuration = Math.Max(60, hohmann * 0.25); MaximumDuration = hohmann * 2;
            double minimumPeriapsis = ArrivalSafetyRadius(Target) - Target.MeanRadius + 10000;
            PeriapsisKm = (float)Math.Max(100, minimumPeriapsis / 1000);
        }

        private bool OrbitStillMatches(Vehicle vehicle, TransferRequest request)
        {
            if (vehicle?.Orbit == null || !request.Parking.State(Now, out var expectedR, out var expectedV)) return false;
            var state = vehicle.Orbit.GetStateVectorsAt(new UniverseTime(Now));
            var r = Vector(state.PositionCci.Transform(_parkingToCentral)); var v = Vector(state.VelocityCci.Transform(_parkingToCentral));
            return (r - expectedR).Length < Math.Max(1000, r.Length * 0.001)
                && (v - expectedV).Length < Math.Max(1, v.Length * 0.001);
        }

        public void SettingsChanged() { Cancel(); Grid = null; SelectedIndex = -1; Message = null; _excludedWindows.Clear(); ResetRouteAdjustments(); }
        public void Select(int index)
        {
            if (IsSearching || IsChecking || IsExecuting || Grid == null || index < 0 || index >= Grid.Cells.Length || !Grid.Cells[index].Valid) return;
            if (!Grid.Available(index, Now + 30, _excludedWindows))
            { Message = Grid.Cells[index].Departure < Now + 30 ? "transfer.stale" : "transfer.rejected_window"; return; }
            ClearRouteProblem();
            _selectedGrid = null;
            SelectedIndex = index; Message = Grid.Cells[index].BestEffort ? "transfer.fallback_selected" : Grid.Cells[index].EscapesCentral ? "transfer.escape_selected" : null;
        }
        public void SelectPorkchop(int index)
        {
            var overview = Grid?.PorkchopGrid;
            if (overview == null) { Select(index); return; }
            if (IsSearching || IsChecking || IsExecuting || !overview.Available(index, Now + 30, _excludedWindows)) return;
            // Only an explicit valid plot selection replaces the selected constrained route.
            Grid = overview; OberthMode = false;
            Select(index);
        }
        public void SelectBest(bool earliest)
        {
            if (Grid == null || IsSearching || IsChecking || IsExecuting) return;
            var best = Grid.Pick(IncludeCapture, earliest, MaximumDv, Now + 30, _excludedWindows);
            if (!best.Valid) { SelectedIndex = -1; Message = earliest ? "transfer.no_early" : "transfer.no_solution"; return; }
            int x = (int)Math.Round((best.Departure - Grid.Request.Earliest) / (Grid.Request.Latest - Grid.Request.Earliest) * (Grid.Request.Width - 1));
            int y = (int)Math.Round((best.Duration - Grid.Request.MinimumDuration) / (Grid.Request.MaximumDuration - Grid.Request.MinimumDuration) * (Grid.Request.Height - 1));
            SelectedIndex = Math.Clamp(y, 0, Grid.Request.Height - 1) * Grid.Request.Width + Math.Clamp(x, 0, Grid.Request.Width - 1);
            _selectedSolution = best; _selectedGrid = Grid; ClearRouteProblem();
            Message = best.BestEffort ? "transfer.fallback_selected" : best.EscapesCentral ? "transfer.escape_selected" : null;
        }

        public void HandleAction(string action, Vehicle vehicle, bool controlLocked)
        {
            if (action == "transfer_cancel") { Cancel(); Message = "transfer.cancelled"; return; }
            if (action == "transfer_stop") { Cancel(); StopExecution(); return; }
            if (!ReferenceEquals(vehicle, _vehicle)) return;
            if (controlLocked) { Message = "transfer.control_locked"; return; }
            try
            {
                switch (action)
                {
                    case "transfer_search":
                        if (IsChecking) return;
                        OberthMode = false;
                        _excludedWindows.Clear(); ResetRouteAdjustments();
                        BeginSearch(vehicle, false);
                        break;
                    case "transfer_oberth":
                        if (IsChecking || IsExecuting) return;
                        if (!CanSearchOberth) { Message = "transfer.oberth_requires_earth"; return; }
                        OberthMode = true; PreferEarliest = false;
                        _excludedWindows.Clear(); ResetRouteAdjustments(); BeginSearch(vehicle, false);
                        break;
                    case "transfer_improve": ImproveRoute(vehicle); break;
                    case "transfer_create": CreateNode(vehicle, false); break;
                    case "transfer_execute": CreateNode(vehicle, true); break;
                    case "transfer_capture":
                        if (!CanExecuteCapture) { Message = "transfer.capture_existing"; return; }
                        StartExecution(vehicle);
                        break;
                    case "transfer_remove":
                        if (!HasNode) return;
                        StopExecution(); ClearValidation(); vehicle.FlightComputer.RemoveBurn(_node); _node = null; Message = "transfer.removed";
                        break;
                }
            }
            catch (Exception ex)
            {
                Message = "transfer.failed";
                Diag.Error("TRANSFER", ex, "Transfer planner action failed");
            }
        }

        private void Harvest()
        {
            if (_search == null || !_search.IsCompleted) return;
            try
            {
                Grid = _search.GetAwaiter().GetResult(); SelectedIndex = -1;
                _search = null;
                Stale = false;
                SelectBest(PreferEarliest);
                if (!Selected.Valid && Grid.Request.Oberth && !double.IsFinite(TransferOberth.Departure(Grid.Request, Grid.Request.Earliest)))
                    Message = "transfer.oberth_no_window";
                int valid = 0;
                foreach (var cell in Grid.Cells) if (cell.Valid) valid++;
                Diag.Info("TRANSFER", Diag.F($"event=search_completed target=\"{Target.Id}\" valid_cells={valid} refined_candidates={Grid.Candidates.Count} selected={SelectedIndex} best_effort={Selected.BestEffort} departure_s={Selected.Departure:F3} dv_mps={Selected.DepartureDv:F3} flyby=\"{Selected.Flyby?.Body.Id ?? "none"}\""));
                foreach (var rejected in Grid.Request.Rejections)
                    Diag.Info("TRANSFER", $"event=search_rejection_summary reason={rejected.Key} count={rejected.Value}");
            }
            catch (OperationCanceledException) { Message = "transfer.cancelled"; }
            catch (Exception ex) { Message = "transfer.failed"; Diag.Error("TRANSFER", ex, "Transfer search failed"); }
            finally { _search = null; _cancellation?.Dispose(); _cancellation = null; }
        }

        public bool CanCreate => Selected.Valid && !Stale && !NeedsBetterRoute && !IsSearching && !IsChecking && !HasOtherNodes && Selected.Departure > Now + 30;
        public bool CanExecute => CanCreate && _vehicle?.IsControllable == true
            && (_vehicle.BurnPlan.BurnCount == 0 || (_vehicle.BurnPlan.BurnCount == 1 && HasNode));
        public bool CanExecuteCapture => !IsExecuting && _captureNode != null && HasNode && _vehicle?.IsControllable == true
            && ReferenceEquals(_vehicle.BurnPlan.FindFirstExecutableBurn(), _captureNode);

        private void CreateNode(Vehicle vehicle, bool execute)
        {
            if (HasOtherNodes) { Message = "transfer.existing_nodes"; return; }
            if (!CanCreate || !OrbitStillMatches(vehicle, Grid.Request)) { Message = "transfer.stale"; return; }
            if (execute && !CanExecute) { Message = "transfer.existing_nodes"; return; }
            // Existing nodes change the orbit the solver started from. Preserve them and require their removal first.
            if (vehicle.BurnPlan.BurnCount > (HasNode ? 1 : 0)) { Message = "transfer.existing_nodes"; return; }
            var solution = Selected;
            var time = new UniverseTime(solution.Departure);
            var patch = vehicle.FlightPlan.TryFindPatch(time);
            if (patch == null || !ReferenceEquals(patch.Orbit.Parent, vehicle.Parent)) { Message = "transfer.stale"; return; }
            double3 deltaCci = Native(solution.DeltaV).Transform(_parkingToCentral.Inverse());
            double3 deltaVlf = OrbitalTransfers.CciToVlf(vehicle.Orbit, time, deltaCci);
            var burn = Burn.Create(vehicle.Orbit.GetPointAt(time), solution.Departure, deltaVlf, patch, vehicle);
            // Keep native duration/ignition estimates stable when manual throttle is cleared to zero.
            burn.PlannedThrottle = 1;
            if (HasNode) { StopExecution(); vehicle.FlightComputer.RemoveBurn(_node); }
            vehicle.FlightComputer.AddBurn(burn); _node = burn;
            Universe.SetTarget(vehicle, Target, null);
            _captureVehicle = vehicle; _captureTarget = Target; _captureDeparture = burn;
            _departureFinished = false; _captureNode = null; _captureSuppressed = false;
            QueueValidation(vehicle, burn, solution, execute);
            Diag.Info("TRANSFER", $"event=node_created target=\"{Target.Id}\" departure_s={solution.Departure:F3} dv_mps={solution.DepartureDv:F3} execute={execute}");
        }

        private void StopExecution()
        {
            if (_executingVehicle == null) { ClearValidation(); return; }
            EndExecution("transfer.stopped");
            ClearCapture();
        }

        public void Cancel()
        {
            _replanPending = false;
            var previous = _search;
            _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; _search = null;
            // Observe a detached worker's eventual exception; cancellation never publishes its partial grid.
            if (previous != null) _ = previous.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        public void Dispose() { Cancel(); StopExecution(); }
        private static LandingVector Vector(double3 vector) => new LandingVector(vector.X, vector.Y, vector.Z);
        private static double3 Native(LandingVector vector) => new double3(vector.X, vector.Y, vector.Z);
    }
}
