using System;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed partial class ManeuverPlanner
    {
        private enum ExecutionPhase { Aligning, Warping, Waiting, Burning, Coasting, Validating }
        private ExecutionPhase _executionPhase;
        private BurnTarget _departureBurn;
        private double _settledSince = double.NaN, _captureRefresh = double.NegativeInfinity;
        private Vehicle _captureVehicle;
        private Celestial _captureTarget;
        private Burn _captureDeparture, _captureNode;
        private bool _departureFinished, _captureSuppressed;
        private bool _executingCapture, _coastWarpRequested, _burnWarpRequested;
        private static readonly FieldInfo ManualInputs = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);

        public bool IsWarping => IsExecuting && _executionPhase == ExecutionPhase.Warping;
        public bool IsCoasting => IsExecuting && (_executionPhase == ExecutionPhase.Coasting || _executionPhase == ExecutionPhase.Validating);
        public string DisplayMessage => RouteTarget != null && (Message == "transfer.arrival_surface" || Message == "transfer.arrival_atmosphere")
            ? L10n.Format(Message, RouteTarget.Id, ((Message == "transfer.arrival_surface"
                ? TransferSafety.SurfaceRadius(RouteTarget.MeanRadius, RouteTarget.MaxTerrainRadius)
                : ((IParentBody)RouteTarget).GetAtmosphereRadius()) - RouteTarget.MeanRadius).ToString("N1", L10n.Culture))
            : Message == null ? null : L10n.Get(Message);

        private static double ArrivalSafetyRadius(Celestial body)
            => Math.Max(TransferSafety.SurfaceRadius(body.MeanRadius, body.MaxTerrainRadius), ((IParentBody)body).GetAtmosphereRadius());
        private static string ArrivalError(Celestial body, double altitude)
        {
            string unsafeArrival = TransferSafety.ArrivalError(altitude, body.MeanRadius, body.MaxTerrainRadius, ((IParentBody)body).GetAtmosphereRadius());
            return unsafeArrival ?? (body.SphereOfInfluence > 0 && body.MeanRadius + altitude >= body.SphereOfInfluence
                ? "transfer.arrival_soi" : null);
        }

        private static double DefaultArrivalAltitude(Celestial body)
        {
            double safety = ArrivalSafetyRadius(body);
            double margin = body.SphereOfInfluence > safety && double.IsFinite(body.SphereOfInfluence)
                ? Math.Min(10000, (body.SphereOfInfluence - safety) * 0.1) : 10000;
            double radius = Math.Max(body.MeanRadius + 100000, safety + margin);
            if (body.SphereOfInfluence > safety && double.IsFinite(body.SphereOfInfluence))
                radius = Math.Min(radius, Math.Max(safety + margin, body.SphereOfInfluence * 0.5));
            return radius - body.MeanRadius;
        }

        internal static void ClearThrustInputs(Vehicle vehicle)
        {
            if (ManualInputs == null) throw new InvalidOperationException("Native manual input field is unavailable");
            var inputs = (ManualControlInputs)ManualInputs.GetValue(vehicle);
            inputs.EngineThrottle = 0; inputs.EngineOn = false; inputs.ThrusterCommandFlags = ThrusterMapFlags.None;
            ManualInputs.SetValue(vehicle, inputs);
        }

        private void StartExecution(Vehicle vehicle)
        {
            // Stop previous warp and stale manual thrust before allowing any alignment or new warp.
            ClearThrustInputs(vehicle);
            vehicle.SetEnum(FlightComputerBurnMode.Manual);
            Universe.AutoWarpStop(true);
            if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
            _executingVehicle = vehicle; _departureBurn = vehicle.FlightComputer.Burn;
            _executingCapture = _node != null && ReferenceEquals(_node, _captureNode);
            _burnWarpRequested = false;
            _executionPhase = ExecutionPhase.Aligning; _settledSince = double.NaN;
            if (_executingCapture && !ReferenceEquals(vehicle.Parent, _captureTarget))
            {
                _executionPhase = ExecutionPhase.Coasting;
                FlightAttitude.StopTracking(vehicle.FlightComputer);
                Message = "transfer.awaiting_soi";
                return;
            }
            vehicle.FlightComputer.TrackTarget(FlightComputerAttitudeTrackTarget.PositiveDv);
            Message = _executingCapture ? "transfer.capture_aligning" : "transfer.aligning";
        }

        public void UpdateExecution(Vehicle vehicle, double now, bool allowRcs, bool controlLocked)
        {
            try
            {
                UpdateValidation(vehicle, now, controlLocked);
                if (IsExecuting) UpdateDeparture(vehicle, now, allowRcs);
                if (!IsExecuting && !controlLocked) UpdateCapture(vehicle, now);
            }
            catch (Exception ex)
            {
                if (IsExecuting) StopExecution();
                Message = "transfer.failed";
                Diag.EveryError("transfer_execution", 5, "TRANSFER", ex, "Updating transfer execution failed");
            }
        }

        internal void ResumeAfterPhysicsPause() { _settledSince = double.NaN; }

        private void UpdateDeparture(Vehicle vehicle, double now, bool allowRcs)
        {
            if (!ReferenceEquals(vehicle, _executingVehicle) || !vehicle.IsControllable)
            { StopExecution(); return; }
            FlightComputer fc = vehicle.FlightComputer;
            ClearThrustInputs(vehicle);
            if (_executionPhase == ExecutionPhase.Validating) { fc.BurnMode = FlightComputerBurnMode.Manual; return; }
            if (_executionPhase == ExecutionPhase.Coasting) { UpdateCoast(vehicle, now); return; }
            if (_executingCapture && !ReferenceEquals(vehicle.Parent, _captureTarget))
            { EndExecution("transfer.capture_missed"); ClearCapture(); return; }
            if (_executionPhase == ExecutionPhase.Burning)
            {
                // KSA removes the completed burn or loads the next one. Never leave Auto on for that next burn.
                bool complete = fc.AutoBurnCompleted || (_departureBurn != null && _departureBurn.StartedAt.HasValue
                    && float3.Dot(_departureBurn.DeltaVToGoCci, _departureBurn.DeltaVTargetCci) <= 0);
                if (complete || !ReferenceEquals(fc.Burn, _departureBurn))
                {
                    if (complete || _departureBurn?.StartedAt.HasValue == true)
                    {
                        if (_executingCapture) { EndExecution("transfer.complete"); ClearCapture(); }
                        else if (_captureTarget != null)
                        {
                            _departureFinished = true; _departureBurn = null;
                            _executionPhase = ExecutionPhase.Coasting; _coastWarpRequested = false;
                            fc.BurnMode = FlightComputerBurnMode.Manual;
                            FlightAttitude.StopTracking(fc);
                            Message = "transfer.coasting";
                            Diag.Info("TRANSFER", "event=departure_complete awaiting=target_soi engines=off");
                        }
                        else { _departureFinished = true; EndExecution("transfer.coasting"); }
                    }
                    else StopExecution();
                    return;
                }
                if (fc.BurnMode != FlightComputerBurnMode.Auto)
                { StopExecution(); return; }
                if (Universe.IsAutoWarpActive || Universe.SimulationSpeed > 1)
                { EndExecution("transfer.warp_interrupted"); ClearCapture(); }
                return;
            }

            fc.BurnMode = FlightComputerBurnMode.Manual;
            if (!RecheckDeparture(vehicle)) return;
            if (_departureBurn == null) _departureBurn = fc.Burn;
            if (fc.Burn == null || !ReferenceEquals(fc.Burn, _departureBurn) || !HasNode)
            { StopExecution(); return; }
            double ignition = fc.Burn.IgnitionTime.Seconds();
            bool timed = !fc.Burn.IgnitionTime.IsSaturated() && double.IsFinite(ignition)
                && fc.Burn.BurnDuration > 0 && float.IsFinite(fc.Burn.BurnDuration);

            if (_executionPhase == ExecutionPhase.Warping)
            {
                if (Universe.IsAutoWarpActive || Universe.SimulationSpeed > 1) return;
                _executionPhase = ExecutionPhase.Waiting; _settledSince = double.NaN;
                fc.TrackTarget(FlightComputerAttitudeTrackTarget.PositiveDv);
                Message = _executingCapture ? "transfer.capture_aligning" : "transfer.aligning";
            }
            else if (Universe.IsAutoWarpActive || Universe.SimulationSpeed > 1)
            {
                Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
                _settledSince = double.NaN;
            }
            if (timed && now > ignition + 5)
            { EndExecution("transfer.missed_burn"); ClearCapture(); return; }

            fc.TrackTarget(FlightComputerAttitudeTrackTarget.PositiveDv);

            double error = FlightAttitude.Angle(FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)),
                FlightAttitude.Forward(fc.Burn.BurnBody2Cci));
            if (fc.RollMode != FlightComputerRollMode.Decoupled)
                error = Math.Max(error, Math.Abs(fc.ErrorAngles.X) * FlightAttitude.RAD_TO_DEG);
            double3 rates = vehicle.GetBodyRates().Transform(fc.Ctrl2Asmb.Inverse()) * FlightAttitude.RAD_TO_DEG;
            double rate = Math.Sqrt(rates.Y * rates.Y + rates.Z * rates.Z
                + (fc.RollMode == FlightComputerRollMode.Decoupled ? 0 : rates.X * rates.X));
            bool settled = TransferSafety.Settled(error, 0)
                && TransferSafety.AxisSettled(rates.Y, fc.RateBit.Y * FlightAttitude.RAD_TO_DEG)
                && TransferSafety.AxisSettled(rates.Z, fc.RateBit.Z * FlightAttitude.RAD_TO_DEG)
                && (fc.RollMode == FlightComputerRollMode.Decoupled
                    || TransferSafety.AxisSettled(rates.X, fc.RateBit.X * FlightAttitude.RAD_TO_DEG));
            if (!settled) _settledSince = double.NaN;
            else if (double.IsNaN(_settledSince)) _settledSince = now;
            if (!allowRcs && !settled)
            { EndExecution("transfer.rcs_required"); ClearCapture(); return; }
            // A fresh native estimate needs at least one physics step with the new node/plan throttle.
            // The first successful alignment latches the warp immediately; final alignment is checked separately.
            if (!timed || !settled) return;
            const double lead = TransferSafety.AlignmentLeadSeconds;
            // Each burn gets its own warp, even if its native ignition estimate settles after alignment.
            // Once requested, do not restart warp after the final alignment or a manual interruption.
            if (!_burnWarpRequested && AutoWarp && ignition - now > lead + 5)
            {
                fc.BurnMode = FlightComputerBurnMode.Manual;
                FlightAttitude.StopTracking(fc);
                _executionPhase = ExecutionPhase.Warping;
                _burnWarpRequested = true;
                Universe.AutoWarpTo(new UniverseTime(ignition - lead), 3, isUncapped: true);
                Message = "transfer.warping";
                Diag.Info("TRANSFER", $"event=warp_started type={(_executingCapture ? "capture" : "departure")} engines=off ignition_s={ignition:F3} lead_s={lead:F3}");
                return;
            }
            _executionPhase = ExecutionPhase.Waiting;
            Message = "transfer.waiting";
            bool stable = now - _settledSince >= 1;
            if (TransferSafety.CanIgnite(now, ignition, stable, Universe.IsAutoWarpActive, Universe.SimulationSpeed))
            {
                _executionPhase = ExecutionPhase.Burning;
                fc.BurnMode = FlightComputerBurnMode.Auto;
                Message = _executingCapture ? "transfer.capturing" : "transfer.executing";
                Diag.Info("TRANSFER", $"event=ignition_armed type={(_executingCapture ? "capture" : "departure")} time_s={now:F3} ignition_s={ignition:F3} error_deg={error:F3} rate_deg_s={rate:F3}");
            }
        }

        private void UpdateCoast(Vehicle vehicle, double now)
        {
            FlightComputer fc = vehicle.FlightComputer;
            fc.BurnMode = FlightComputerBurnMode.Manual;
            ClearThrustInputs(vehicle);
            FlightAttitude.StopTracking(fc);
            UpdateCapture(vehicle, now);
            if (_captureNode != null && !vehicle.BurnPlan.TryGetBurn(_captureNode))
            { StopExecution(); Message = "transfer.removed"; return; }
            if (ReferenceEquals(vehicle.Parent, _captureTarget))
            {
                Universe.AutoWarpStop(true);
                if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1, false);
                if (_captureNode == null) return;
                if (!ReferenceEquals(vehicle.BurnPlan.FindFirstExecutableBurn(), _captureNode))
                { Message = "transfer.capture_existing"; return; }
                _node = _captureNode;
                StartExecution(vehicle);
                Diag.Info("TRANSFER", $"event=capture_alignment_started target=\"{_captureTarget.Id}\" time_s={now:F3} inside_target_soi=True");
                return;
            }
            if (Message != "transfer.capture_unsafe" && Message != "transfer.capture_existing") Message = "transfer.awaiting_soi";
            if (Message == "transfer.capture_unsafe" || Message == "transfer.capture_existing"
                || !AutoWarp || _coastWarpRequested || Universe.IsAutoWarpActive || vehicle.FlightPlan == null) return;
            foreach (PatchedConic patch in vehicle.FlightPlan.Patches)
            {
                if (!ReferenceEquals(patch.PrimaryBody, _captureTarget) || patch.EndTime <= new UniverseTime(now)
                    || patch.StartTime.IsSaturated()) continue;
                double entry = patch.StartTime.Seconds() + 1;
                double ignition = fc.Burn != null && !fc.Burn.IgnitionTime.IsSaturated() && fc.Burn.BurnDuration > 0
                    ? fc.Burn.IgnitionTime.Seconds() : _captureNode?.Time.Seconds() ?? double.PositiveInfinity;
                double until = Math.Min(entry, ignition - TransferSafety.AlignmentLeadSeconds);
                if (!double.IsFinite(until) || until <= now + 5) return;
                _coastWarpRequested = true;
                Universe.AutoWarpTo(new UniverseTime(until), 0, isUncapped: true);
                Diag.Info("TRANSFER", $"event=coast_warp_started target=\"{_captureTarget.Id}\" stop_s={until:F3} engines=off attitude=off");
                return;
            }
        }

        private void EndExecution(string message)
        {
            Vehicle vehicle = _executingVehicle;
            if (vehicle == null) return;
            vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
            ClearThrustInputs(vehicle);
            FlightAttitude.StopTracking(vehicle.FlightComputer);
            Universe.AutoWarpStop(true);
            _executingVehicle = null; _departureBurn = null; Message = message;
            _executingCapture = false; _coastWarpRequested = false; _burnWarpRequested = false;
            Diag.Info("TRANSFER", $"event=execution_ended status={message} engines=off");
        }

        private void ClearCapture()
        {
            ClearValidation();
            _captureVehicle = null; _captureTarget = null; _captureDeparture = null; _captureNode = null;
            _departureFinished = false; _captureRefresh = double.NegativeInfinity; _captureSuppressed = true;
        }

        private void UpdateCapture(Vehicle vehicle, double now)
        {
            if (vehicle?.FlightPlan == null || vehicle.FlightComputer?.BurnMode == FlightComputerBurnMode.Auto
                || now - _captureRefresh < 1) return;
            _captureRefresh = now;
            if (_captureNode != null) return; // Removing a generated node must not recreate it every frame.
            Celestial target = ReferenceEquals(vehicle, _captureVehicle) ? _captureTarget : null;
            if (target != null && !_departureFinished)
            {
                _departureFinished = ReferenceEquals(vehicle.Parent, target)
                    || (_captureDeparture != null && now >= _captureDeparture.Time.Seconds()
                        && (!vehicle.BurnPlan.TryGetBurn(_captureDeparture)
                            || (vehicle.FlightComputer.Burn?.StartedAt.HasValue == true
                                && float3.Dot(vehicle.FlightComputer.Burn.DeltaVToGoCci,
                                    vehicle.FlightComputer.Burn.DeltaVTargetCci) <= 0)));
                if (!_departureFinished) return;
            }
            // Also recover an inbound encounter from a saved flight or a manually executed transfer.
            if (target == null && !_captureSuppressed && vehicle.Target is Celestial selected)
                foreach (PatchedConic encounter in vehicle.FlightPlan.Patches)
                    if (ReferenceEquals(encounter.PrimaryBody, selected) && encounter.Orbit.IsUnbound()
                        && encounter.EndTime > new UniverseTime(now))
                    { target = selected; break; }
            if (target == null) return;

            foreach (PatchedConic patch in vehicle.FlightPlan.Patches)
            {
                if (!ReferenceEquals(patch.PrimaryBody, target) || patch.BurnTrajectory.HasValue || !patch.Orbit.IsUnbound()) continue;
                UniverseTime start = patch.StartTime > new UniverseTime(now) ? patch.StartTime : new UniverseTime(now);
                UniverseTime? pe = patch.Orbit.GetNextPeriapsisTime(start);
                if (!pe.HasValue || pe.Value.IsSaturated() || pe.Value <= start || pe.Value > patch.EndTime) continue;
                string unsafeArrival = ArrivalError(target, patch.Orbit.Periapsis - target.MeanRadius);
                if (unsafeArrival != null) { Message = "transfer.capture_unsafe"; return; }
                var state = patch.Orbit.GetStateVectorsAt(pe.Value);
                if (!TransferSafety.Capture(((IParentBody)target).Mu, Vector(state.PositionCci), Vector(state.VelocityCci), out var dv)) continue;
                // Preserve the user's maneuver plan. A correction node may still change this encounter.
                if (vehicle.BurnPlan.BurnCount > 0 && !(vehicle.BurnPlan.BurnCount == 1
                    && _captureDeparture != null && vehicle.BurnPlan.TryGetBurn(_captureDeparture)))
                { Message = "transfer.capture_existing"; return; }
                var vlf = OrbitalTransfers.CciToVlf(patch.Orbit, pe.Value, Native(dv));
                var burn = Burn.Create(patch.Orbit.GetPointAt(pe.Value), pe.Value.Seconds(), vlf, patch, vehicle);
                burn.PlannedThrottle = 1;
                if (_departureFinished && _captureDeparture != null && vehicle.BurnPlan.TryGetBurn(_captureDeparture))
                { vehicle.FlightComputer.RemoveBurn(_captureDeparture); _node = null; }
                vehicle.FlightComputer.AddBurn(burn); _captureNode = burn;
                _node = burn;
                _captureVehicle = vehicle; _captureTarget = target;
                Message = "transfer.capture_created";
                Diag.Info("TRANSFER", $"event=capture_created target=\"{target.Id}\" periapsis_km={(patch.Orbit.Periapsis - target.MeanRadius) / 1000:F3} time_s={pe.Value.Seconds():F3} dv_mps={dv.Length:F3}");
                return;
            }
            Message = ReferenceEquals(vehicle.Parent, target) ? "transfer.capture_missed" : "transfer.awaiting_encounter";
        }
    }
}
