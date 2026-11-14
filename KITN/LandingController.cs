using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class LandingController
    {
        public LandingState State { get; private set; }
        public bool IsActive => State >= LandingState.Planning && State <= LandingState.PoweredDescent;
        public readonly List<LandingPad> Pads = new List<LandingPad>();
        public int SelectedPadIndex;
        public bool AutoWarp = true;
        public bool CanStart { get; private set; }
        public string UnavailableReason { get; private set; } = "landing.require_vehicle";
        public double AvailableDeltaV { get; private set; }
        public double RequiredDeltaV => _plan == null || TargetName != _target?.Key ? double.NaN : _plan.DeltaV + _plan.ReserveDeltaV;
        public double TimeUntilBurn { get; private set; } = double.NaN;
        public double PadDistance { get; private set; } = double.NaN;
        public double PredictedMiss { get; private set; } = double.NaN;
        public double Clearance { get; private set; } = double.NaN;
        public double VerticalSpeed { get; private set; } = double.NaN;
        public double HorizontalSpeed { get; private set; } = double.NaN;
        public double RequestedThrottle { get; private set; }
        public double CommandedThrottle { get; private set; }
        public double RemainingDeltaV { get; private set; } = double.NaN;
        public double PointingError { get; private set; } = double.NaN;
        public double TargetPitch { get; private set; } = double.NaN;
        public double StoppingHeight { get; private set; } = double.NaN;
        public double DesiredVerticalSpeed { get; private set; } = double.NaN;
        public double AlignmentHeight { get; private set; } = double.NaN;
        public double TimeToTouchdown { get; private set; } = double.NaN;
        private LandingVector _commandAcceleration;
        public bool TerrainContact { get; private set; }
        public double PlanProgress => _planner?.Progress ?? 0;
        public string TargetName => IsActive && _target != null ? _target.Key
            : Pads.Count > 0 ? Pads[Math.Clamp(SelectedPadIndex, 0, Pads.Count - 1)].Key : "—";
        public string Reason { get; private set; } = "idle";
        private LocalizedMessage _message = new LocalizedMessage("landing.ready", Array.Empty<object>());
        public string Message => _message.ToString();

        private Vehicle _controlled, _activeVehicle;
        private Celestial _body;
        private LandingPad _target;
        private LandingBodyModel _model;
        private LandingReturnPlanner _planner;
        private LandingReturnPlan _plan;
        private LandingPropulsion _propulsion;
        private int _performanceStage = -1;
        private readonly ThrottleActuator _throttle = new ThrottleActuator();
        private readonly InsertionAttitudeGate _attitude = new InsertionAttitudeGate();
        private readonly LandingTouchdown _touchdown = new LandingTouchdown();
        private FlightComputer _savedComputer;
        private float _savedRate, _savedDeadband;
        private FlightComputerRollMode _savedRoll;
        private double _lastTime = double.NaN, _stateSince, _lastPrediction = double.NegativeInfinity;
        private double _lead, _started, _lastVelocityTime = double.NaN;
        private LandingVector _lastVelocity, _correctionVelocity;
        private LandingPrediction _prediction;
        private bool _warpStarted;
        private static readonly FieldInfo ManualInputs = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo BoundingBox = typeof(Vehicle).GetMethod("ComputeBoundingBoxAsmb", BindingFlags.Instance | BindingFlags.NonPublic);

        public void Refresh(Vehicle vehicle)
        {
            if (!ReferenceEquals(vehicle, _controlled))
            {
                if (IsActive) Abort(_activeVehicle, "controlled_vehicle_changed", "landing.vehicle_changed");
                _controlled = vehicle;
            }
            Celestial body = vehicle?.Parent as Celestial;
            if (!ReferenceEquals(body, _body))
            {
                if (IsActive) Abort(_activeVehicle, "parent_body_changed", "landing.vehicle_changed");
                _body = body; Pads.Clear(); SelectedPadIndex = 0;
                if (IsEarth(body))
                    foreach (LocationReference location in body.BodyTemplate.Locations)
                        if (location is LandmarkReference pad && pad.IsLaunchPad) Pads.Add(new LandingPad(body, pad));
            }
            CanStart = false;
            AvailableDeltaV = 0;
            if (vehicle == null) { UnavailableReason = "landing.require_vehicle"; return; }
            if (!IsEarth(body)) { UnavailableReason = "landing.require_earth"; return; }
            if (vehicle.FlightComputer == null || !vehicle.HasControlModule || ManualInputs == null)
            { UnavailableReason = "landing.require_control"; return; }
            if (!IsActive && vehicle.FlightComputer.BurnMode == FlightComputerBurnMode.Auto)
            { UnavailableReason = "landing.require_manual_burn"; return; }
            LandingPropulsion measured = LandingPropulsion.Read(vehicle);
            int stage = vehicle.Parts?.SequenceList.ActiveSequence ?? -1;
            if (IsActive && stage == _performanceStage && measured.Thrust <= 1 && _propulsion.Thrust > 1)
                measured = new LandingPropulsion(_propulsion.Thrust, _propulsion.MassFlow, measured.MinimumThrottle,
                    measured.MinimumPulse, measured.UsableFuel, measured.Restartable, measured.FuelValid);
            _propulsion = measured; _performanceStage = stage;
            AvailableDeltaV = _propulsion.AvailableDeltaV(vehicle.TotalMass);
            if (IsActive) return;
            if (vehicle.Orbit == null || !vehicle.Orbit.IsBound() || vehicle.Orbit.PeriapsisAltitude < Math.Max(100000, ((IParentBody)body).GetAtmosphereRadius() - body.MeanRadius))
            { UnavailableReason = "landing.require_orbit"; return; }
            if (Pads.Count == 0) { UnavailableReason = "landing.require_pad"; return; }
            if (!_propulsion.Restartable || _propulsion.Thrust <= 1 || _propulsion.MassFlow <= 0)
            { UnavailableReason = "landing.require_engines"; return; }
            if (!_propulsion.FuelValid || AvailableDeltaV < 350)
            { UnavailableReason = "landing.require_fuel"; return; }
            if (_propulsion.Thrust / Math.Max(1, vehicle.TotalMass) < ((IParentBody)body).Mu / (body.MeanRadius * body.MeanRadius) * 1.15)
            { UnavailableReason = "landing.require_thrust"; return; }
            UnavailableReason = "landing.ready"; CanStart = true;
        }

        private static bool IsEarth(Celestial body) => body != null
            && (string.Equals(body.Id, "Earth", StringComparison.OrdinalIgnoreCase)
                || string.Equals(body.BodyTemplate.Id, "Earth", StringComparison.OrdinalIgnoreCase));

        public bool Start(Vehicle vehicle, double time)
        {
            Refresh(vehicle);
            if (!CanStart || IsActive) return false;
            _target = Pads[Math.Clamp(SelectedPadIndex, 0, Pads.Count - 1)];
            _target.ResolveSurface(_body);
            _model = CreateModel(_body, vehicle, _propulsion);
            _activeVehicle = vehicle; _plan = null;
            ClearFlightData();
            _started = _lastTime = time; _lastPrediction = double.NegativeInfinity; _lastVelocityTime = double.NaN;
            _lead = Math.Max(75, vehicle.FlightComputer.ConservativeFlipTime * 3 + 20);
            _planner = new LandingReturnPlanner(vehicle, _model, PadPosition(time), _target.Altitude, time, _lead, AvailableDeltaV,
                _propulsion.Thrust / _propulsion.MassFlow);
            _warpStarted = false; _throttle.Reset(); _attitude.Reset(); _touchdown.Reset();
            SaveSettings(vehicle.FlightComputer);
            // Do the incremental search at normal speed so a pre-existing warp
            // cannot carry the vehicle past the window while it is being planned.
            RealTime();
            SetThrottle(vehicle, 0, 0, false);
            FlightAttitude.StopTracking(vehicle.FlightComputer);
            Transition(LandingState.Planning, time, "return_window_search", "landing.planning");
            Diag.Info("LANDING", Diag.F($"event=target_selected vehicle=\"{vehicle.Id}\" pad=\"{_target.Key}\" latitude_deg={_target.Latitude * FlightAttitude.RAD_TO_DEG:F8} longitude_deg={_target.Longitude * FlightAttitude.RAD_TO_DEG:F8} pad_altitude_m={_target.Altitude:F3} usable_fuel_kg={_propulsion.UsableFuel:F3} available_delta_v_m_s={AvailableDeltaV:F3} drag_area_estimate_m2={_model.DragArea:F3}"));
            return true;
        }

        private static LandingBodyModel CreateModel(Celestial body, Vehicle vehicle, LandingPropulsion propulsion)
        {
            IParentBody physics = body;
            double height = Math.Clamp(physics.GetAtmosphereRadius() - body.MeanRadius, 0, 300000);
            var model = new LandingBodyModel { Mu = physics.Mu, Radius = body.MeanRadius, Mass = vehicle.TotalMass,
                DragArea = Math.Max(0.1, vehicle.TotalMass / 5300.0), Thrust = propulsion.Thrust, AtmosphereHeight = height,
                AngularVelocity = LandingReturnPlanner.Vector(physics.GetAngularVelocityCci()) };
            var atmosphere = body.GetPhysicalAtmosphere();
            if (atmosphere != null && height > 0)
            {
                model.DensitySamples = new double[(int)Math.Ceiling(height / model.DensityStep) + 1];
                for (int i = 0; i < model.DensitySamples.Length; i++)
                    model.DensitySamples[i] = Math.Max(0, atmosphere.ComputeDensityAtAltitude(i * model.DensityStep));
            }
            return model;
        }

        public void Update(Vehicle vehicle, double time, double dtPlayer)
        {
            if (!IsActive) return;
            if (!ReferenceEquals(vehicle, _activeVehicle) || !ReferenceEquals(vehicle?.Parent, _body))
            { Abort(_activeVehicle, "vehicle_or_parent_changed", "landing.vehicle_changed"); return; }
            double dt = double.IsFinite(_lastTime) ? time - _lastTime : 0;
            if (dt < 0) { Abort(vehicle, "simulation_time_reverted", "landing.time_reverted"); return; }
            _lastTime = time;
            try
            {
                ReadMotion(vehicle, time, dt);
                if (_touchdown.Confirm(time, TerrainContact, Clearance, vehicle.GetSurfaceSpeed(), PadDistance, _target.FootprintRadius))
                {
                    Release(vehicle);
                    Transition(LandingState.Landed, time, "sustained_contact_on_selected_pad", "landing.landed");
                    Diag.Info("LANDING", Diag.F($"event=touchdown_confirmed pad=\"{_target.Key}\" miss_m={PadDistance:F3} surface_speed_m_s={vehicle.GetSurfaceSpeed():F3} controls_released=true"));
                    return;
                }
                if (TerrainContact && vehicle.GetSurfaceSpeed() < 1.5)
                {
                    // Let the vehicle settle on its real contact points during confirmation.
                    // Continuing hover thrust here can lift it off the pad again.
                    SetThrottle(vehicle, 0, dt, false);
                    if (PadDistance > Math.Max(25, _target.FootprintRadius))
                        Abort(vehicle, "touchdown_outside_selected_pad", "landing.off_target");
                    return;
                }
                if (!_propulsion.FuelValid || !_propulsion.Restartable || _propulsion.UsableFuel < 0.01)
                { Abort(vehicle, "usable_landing_propellant_exhausted", "landing.fuel_exhausted"); return; }
                if (time - _started > 93000) { Abort(vehicle, "return_timed_out", "landing.failed"); return; }
                if (State == LandingState.Planning)
                {
                    SetThrottle(vehicle, 0, dt, false);
                    _planner.Step();
                    _message = new LocalizedMessage("landing.planning_progress", new object[] { _planner.Progress * 100 });
                    if (_planner.Complete)
                    {
                        _plan = _planner.Plan;
                        if (_plan == null || _plan.BurnTime < time + 15)
                        { Abort(vehicle, "no_reachable_return_window_with_fuel_reserve", "landing.no_window"); return; }
                        Transition(LandingState.WaitingForReturn, time, "return_plan_ready", "landing.waiting");
                        Diag.Info("LANDING", Diag.F($"event=return_plan_ready burn_at_sim_s={_plan.BurnTime:F3} handover_at_sim_s={_plan.HandoverTime:F3} deorbit_delta_v_m_s={_plan.DeltaV:F3} landing_reserve_m_s={_plan.ReserveDeltaV:F3} available_delta_v_m_s={AvailableDeltaV:F3}"));
                    }
                    return;
                }
                TimeUntilBurn = _plan.BurnTime - time;
                if (Universe.IsPaused()) return;
                if (State == LandingState.WaitingForReturn)
                {
                    SetThrottle(vehicle, 0, dt, false);
                    if (AvailableDeltaV + 1 < RequiredDeltaV)
                    { Abort(vehicle, "landing_fuel_reserve_lost_before_deorbit", "landing.require_fuel"); return; }
                    _message = new LocalizedMessage("landing.waiting_time", new object[] { TargetName, MissionClock.Format(false, Math.Max(0, TimeUntilBurn)) });
                    if (TimeUntilBurn > _lead)
                    {
                        if (AutoWarp && !_warpStarted)
                        { Universe.AutoWarpTo(LandingReturnPlanner.Time(_plan.BurnTime - _lead)); _warpStarted = true; }
                        return;
                    }
                    RealTime();
                    Point(vehicle, _plan.DeltaVelocity, 5);
                    AlignWithTvc(vehicle, dt);
                    double burnDuration = OrbitInsertion.BurnDuration(_plan.DeltaV, vehicle.TotalMass, _propulsion.Thrust, _propulsion.MassFlow);
                    if (TimeUntilBurn <= Math.Max(1, burnDuration / 2) && PointingError < 8)
                        Transition(LandingState.DeorbitBurn, time, "finite_burn_window_reached", "landing.deorbit");
                    else if (TimeUntilBurn < -120)
                        Abort(vehicle, "return_alignment_failed", "landing.attitude_failed");
                    return;
                }
                if (!vehicle.HasPhysicsBubble)
                { RealTime(); SetThrottle(vehicle, 0, dt, false); _message = new LocalizedMessage("landing.wait_physics", Array.Empty<object>()); return; }
                RealTime();
                if (State == LandingState.DeorbitBurn)
                {
                    if (time - _lastPrediction >= 0.75)
                    {
                        LandingVector seed = LandingReturnPlanner.Vector(_plan.OriginalOrbit.GetVelocityCci(LandingReturnPlanner.Time(time))) + _plan.DeltaVelocity;
                        if (!Retarget(vehicle, time, seed))
                        { Abort(vehicle, "deorbit_targeting_failed", "landing.failed"); return; }
                    }
                    VelocityBurn(vehicle, _correctionVelocity, dt);
                    if (RemainingDeltaV < 1.5 && _prediction.Valid && PredictedMiss < 2500)
                    {
                        SetThrottle(vehicle, 0, dt, false);
                        Transition(LandingState.Entry, time, "targeted_deorbit_complete", "landing.entry");
                    }
                    else if (time - _stateSince > Math.Max(240, _plan.DeltaV / Math.Max(0.1, _propulsion.Thrust / vehicle.TotalMass) * 5))
                        Abort(vehicle, "deorbit_burn_not_converging", "landing.failed");
                    return;
                }
                double acceleration = _propulsion.Thrust / Math.Max(1, vehicle.TotalMass);
                double gravity = _model.Mu / LandingReturnPlanner.Vector(vehicle.GetPositionCci()).LengthSquared;
                if (acceleration < gravity * 1.03)
                { Abort(vehicle, "insufficient_landing_thrust", "landing.require_thrust"); return; }
                double stop = VerticalSpeed < 0 ? VerticalSpeed * VerticalSpeed / (2 * Math.Max(0.1, acceleration - gravity)) : 0;
                double alignMargin = Math.Clamp(vehicle.FlightComputer.ConservativeFlipTime * 2, 8, 30) * Math.Max(0, -VerticalSpeed);
                if (State == LandingState.Entry && Clearance < Math.Max(7000, stop * 1.5 + alignMargin + 1000))
                    Transition(LandingState.PoweredDescent, time, "braking_and_alignment_height_reached", "landing.descent");
                if (State == LandingState.Entry)
                {
                    if (time - _lastPrediction >= 1) UpdatePrediction(vehicle, time);
                    LandingVector relative = LandingReturnPlanner.Vector(vehicle.GetSurfaceVelocityCci());
                    Point(vehicle, -relative, 5);
                    double q = vehicle.ComputeDynamicPressure();
                    if (_prediction.Valid && PredictedMiss > 3000 && q < 20000 && AvailableDeltaV > LandingMath.LandingReserve(_model, _prediction) + 50
                        && Retarget(vehicle, time, LandingReturnPlanner.Vector(vehicle.GetVelocityCci())))
                        VelocityBurn(vehicle, _correctionVelocity, dt, 0.3);
                    else { SetThrottle(vehicle, 0, dt, false); AlignWithTvc(vehicle, dt); }
                    _message = new LocalizedMessage("landing.entry_target", new object[] { TargetName, PredictedMiss / 1000 });
                    return;
                }
                var command = LandingMath.Descend(_model, LandingReturnPlanner.Vector(vehicle.GetPositionCci()),
                    LandingReturnPlanner.Vector(vehicle.GetVelocityCci()), PadPosition(time), Math.Max(0, Clearance), acceleration);
                StoppingHeight = command.StoppingHeight; DesiredVerticalSpeed = command.DesiredVerticalSpeed;
                AlignmentHeight = command.AlignmentHeight; TimeToTouchdown = command.TimeToGo;
                _commandAcceleration = command.Acceleration;
                Point(vehicle, command.Acceleration, 8);
                double requested = command.Acceleration.Length / acceleration;
                if (PointingError > 45) requested = Math.Min(requested, _propulsion.MinimumThrottle);
                SetThrottle(vehicle, requested, dt, PointingError > 3);
                _message = new LocalizedMessage("landing.descent_target", new object[] { TargetName, PadDistance, Math.Max(0, Clearance) });
            }
            catch (Exception ex)
            {
                Diag.EveryError("landing_controller", 3, "LANDING", ex, $"state={State} pad=\"{TargetName}\"");
                Abort(vehicle, "landing_controller_exception", "landing.failed");
            }
        }

        private void ReadMotion(Vehicle vehicle, double time, double dt)
        {
            _model.Mass = vehicle.TotalMass;
            _model.Thrust = _propulsion.Thrust;
            LandingVector position = LandingReturnPlanner.Vector(vehicle.GetPositionCci()), velocity = LandingReturnPlanner.Vector(vehicle.GetVelocityCci());
            LandingVector relative = LandingReturnPlanner.Vector(vehicle.GetSurfaceVelocityCci()), up = position.Unit;
            VerticalSpeed = LandingVector.Dot(relative, up); HorizontalSpeed = relative.Horizontal(up).Length;
            PadDistance = LandingMath.SurfaceDistance(position, PadPosition(time), _body.MeanRadius);
            // Radar height is at the center of mass. Account for the lowest actual geometry corner.
            if (BoundingBox == null) throw new MissingMethodException("Vehicle.ComputeBoundingBoxAsmb");
            var bounds = ((double3, double3))BoundingBox.Invoke(vehicle, null);
            double bottom = 0;
            for (int corner = 0; corner < 8; corner++)
            {
                double3 point = new double3((corner & 1) == 0 ? bounds.Item1.X : bounds.Item2.X,
                    (corner & 2) == 0 ? bounds.Item1.Y : bounds.Item2.Y, (corner & 4) == 0 ? bounds.Item1.Z : bounds.Item2.Z);
                double3 offset = double3.Transform(point - vehicle.CenterOfMassAsmb, vehicle.GetAsmb2Cci());
                bottom = Math.Max(bottom, -LandingVector.Dot(LandingReturnPlanner.Vector(offset), up));
            }
            Clearance = vehicle.GetRadarAltitude() - bottom;
            if (PadDistance < _target.FootprintRadius)
                Clearance = position.Length - (_model.Radius + _target.Altitude) - bottom;
            TerrainContact = vehicle.HasPhysicsBubble && vehicle.TryGetTerrainContactNormalBody(out _);
            if (vehicle.HasPhysicsBubble && _lastVelocityTime < time && dt > 0 && dt < 0.5 && CommandedThrottle < 0.001
                && vehicle.ComputeLiveThrust() < 1 && relative.Length > 100)
            {
                double density = vehicle.PhysicsEnvironment.AtmosphericDensity;
                LandingVector gravity = position * (-_model.Mu / Math.Pow(position.Length, 3));
                double deceleration = -LandingVector.Dot((velocity - _lastVelocity) / Math.Max(1e-6, time - _lastVelocityTime) - gravity, relative.Unit);
                double area = 2 * deceleration * vehicle.TotalMass / Math.Max(1e-12, density * relative.LengthSquared);
                if (density > 1e-5 && double.IsFinite(_lastVelocityTime) && area > _model.DragArea / 5 && area < _model.DragArea * 5)
                    _model.DragArea = _model.DragArea * 0.95 + area * 0.05;
            }
            _lastVelocity = velocity; _lastVelocityTime = time;
        }

        private LandingVector PadPosition(double time) => LandingReturnPlanner.Vector(double3.Transform(_target.DirectionCcf,
            _body.GetCcf2Cci(LandingReturnPlanner.Time(time)))) * (_body.MeanRadius + _target.Altitude);

        private bool Retarget(Vehicle vehicle, double time, LandingVector seed)
        {
            _lastPrediction = time;
            if (!LandingMath.TargetReturn(_model, LandingReturnPlanner.Vector(vehicle.GetPositionCci()),
                LandingReturnPlanner.Vector(vehicle.GetVelocityCci()), seed, PadPosition(time), _target.Altitude,
                _propulsion.Thrust / _propulsion.MassFlow, out _correctionVelocity, out _prediction)) return false;
            UpdateMiss(time);
            return true;
        }

        private void UpdatePrediction(Vehicle vehicle, double time)
        {
            _lastPrediction = time;
            _prediction = LandingMath.Predict(_model, LandingReturnPlanner.Vector(vehicle.GetPositionCci()),
                LandingReturnPlanner.Vector(vehicle.GetVelocityCci()), _body.MeanRadius + _target.Altitude + LandingMath.HandoverHeight);
            UpdateMiss(time);
        }

        private void UpdateMiss(double time)
        {
            PredictedMiss = _prediction.Valid ? (_prediction.Position - PadPosition(time + _prediction.Seconds).Unit
                * (_body.MeanRadius + _target.Altitude + LandingMath.HandoverHeight)).Length : double.NaN;
        }

        private void VelocityBurn(Vehicle vehicle, LandingVector target, double dt, double maximumThrottle = 1)
        {
            LandingVector correction = target - LandingReturnPlanner.Vector(vehicle.GetVelocityCci());
            RemainingDeltaV = correction.Length;
            if (RemainingDeltaV < 0.1) { SetThrottle(vehicle, 0, dt, false); return; }
            Point(vehicle, correction, 5);
            double request = Math.Min(maximumThrottle, RemainingDeltaV / Math.Max(0.1, _propulsion.Thrust / vehicle.TotalMass) / 1.5);
            if (PointingError > 15) request = 0;
            SetThrottle(vehicle, request, dt, PointingError > 3);
            if (request == 0) AlignWithTvc(vehicle, dt);
        }

        private void Point(Vehicle vehicle, LandingVector direction, double rate)
        {
            if (direction.Length < 1e-8 || !direction.IsFinite) return;
            FlightComputer fc = vehicle.FlightComputer;
            double3 desired = LandingReturnPlanner.Native(direction.Unit);
            doubleQuat current = FlightAttitude.ControlToCci(vehicle);
            PointingError = FlightAttitude.Angle(FlightAttitude.Forward(current), desired);
            TargetPitch = FlightAttitude.Pitch(vehicle.GetPositionCci(), desired);
            fc.RateLimit = (float)(rate * FlightAttitude.DEG_TO_RAD);
            fc.AngleDeadband = (float)(0.2 * FlightAttitude.DEG_TO_RAD); fc.RollMode = FlightComputerRollMode.Decoupled;
            fc.AttitudeFrame = VehicleReferenceFrame.EnuBody;
            fc.CustomAttitudeTarget = FlightAttitude.CustomAngles(vehicle.GetPositionCci(), FlightAttitude.AlignForward(current, desired));
            fc.TrackTarget(FlightComputerAttitudeTrackTarget.Custom);
        }

        private void AlignWithTvc(Vehicle vehicle, double dt)
        {
            double3 rates = vehicle.GetBodyRates();
            double transverse = Math.Sqrt(rates.Y * rates.Y + rates.Z * rates.Z) * FlightAttitude.RAD_TO_DEG;
            FlightComputer fc = vehicle.FlightComputer;
            bool hasRcs = fc.RCSMode == FlightComputerRCSMode.Enabled && fc.RcsTorqueAuthority.LengthSquared() > 1e-10;
            if (_attitude.Update(PointingError, transverse, hasRcs, vehicle.HasPhysicsBubble, 0.25))
                SetThrottle(vehicle, _propulsion.MinimumThrottle * 0.5, dt, true);
        }

        private void SetThrottle(Vehicle vehicle, double request, double dt, bool steering)
        {
            RequestedThrottle = Math.Clamp(request, 0, 1);
            CommandedThrottle = _throttle.Command(RequestedThrottle, _propulsion.MinimumThrottle,
                Math.Clamp(dt, 0, 0.1), true, steering ? Math.Max(0.12, _propulsion.MinimumPulse) : _propulsion.MinimumPulse);
            if (vehicle == null || ManualInputs == null) return;
            var inputs = (ManualControlInputs)ManualInputs.GetValue(vehicle);
            inputs.EngineThrottle = (float)CommandedThrottle; inputs.EngineOn = CommandedThrottle > 0.001;
            inputs.ThrusterCommandFlags = 0;
            ManualInputs.SetValue(vehicle, inputs);
        }

        private void SaveSettings(FlightComputer fc)
        {
            _savedComputer = fc; _savedRate = fc.RateLimit; _savedDeadband = fc.AngleDeadband; _savedRoll = fc.RollMode;
        }
        private void Release(Vehicle vehicle)
        {
            StopWarp(); _throttle.Reset(); SetThrottle(vehicle, 0, 0, false);
            FlightAttitude.StopTracking(vehicle?.FlightComputer);
            if (_savedComputer != null)
            { _savedComputer.RateLimit = _savedRate; _savedComputer.AngleDeadband = _savedDeadband; _savedComputer.RollMode = _savedRoll; _savedComputer = null; }
        }
        private static void StopWarp() { if (Universe.IsAutoWarpActive) Universe.AutoWarpStop(true); }
        private static void RealTime() { StopWarp(); if (Universe.SimulationSpeed > 1) Universe.SetSimulationSpeed(1.0, false); }
        public void Abort(Vehicle vehicle, string reason = "user_abort", string message = "landing.cancelled")
        {
            if (IsActive) Release(vehicle);
            Transition(LandingState.Aborted, Universe.GetElapsedSeconds(), reason, message);
            _planner = null;
        }
        public void Reset(Vehicle vehicle)
        {
            if (IsActive) Release(vehicle);
            _plan = null; _planner = null; _target = null; _touchdown.Reset();
            _activeVehicle = null; ClearFlightData();
            State = LandingState.Idle; Reason = "reset"; _message = new LocalizedMessage("landing.ready", Array.Empty<object>());
        }
        private void ClearFlightData()
        {
            TimeUntilBurn = PadDistance = PredictedMiss = Clearance = VerticalSpeed = HorizontalSpeed = double.NaN;
            RemainingDeltaV = PointingError = TargetPitch = StoppingHeight = DesiredVerticalSpeed = double.NaN;
            AlignmentHeight = TimeToTouchdown = double.NaN;
            RequestedThrottle = CommandedThrottle = 0;
            TerrainContact = false; _commandAcceleration = default;
        }
        private void Transition(LandingState state, double time, string reason, string message)
        {
            LandingState previous = State; State = state; _stateSince = time; Reason = reason;
            _message = new LocalizedMessage(message, Array.Empty<object>());
            Diag.Info("LANDING", Diag.F($"event=state_changed from={previous} to={state} reason={reason} pad=\"{TargetName}\" simulation_t_s={time:F3} altitude_agl_m={Clearance:F3} distance_m={PadDistance:F3} vertical_speed_m_s={VerticalSpeed:F3} available_delta_v_m_s={AvailableDeltaV:F3}"));
        }

        public LandingTelemetry Telemetry(MissionClock clock) => new LandingTelemetry
        {
            State = State, Active = IsActive, Pad = _target?.Key, Reason = Reason,
            Latitude = _target == null ? double.NaN : _target.Latitude * FlightAttitude.RAD_TO_DEG,
            Longitude = _target == null ? double.NaN : _target.Longitude * FlightAttitude.RAD_TO_DEG,
            PadDistance = PadDistance, PredictedMiss = PredictedMiss, TimeUntilBurn = TimeUntilBurn,
            RemainingDeltaV = RemainingDeltaV, AvailableDeltaV = AvailableDeltaV, RequiredDeltaV = RequiredDeltaV,
            Clearance = Clearance, VerticalSpeed = VerticalSpeed, HorizontalSpeed = HorizontalSpeed,
            RequestedThrottle = RequestedThrottle, CommandedThrottle = CommandedThrottle, PointingError = PointingError,
            TargetPitch = TargetPitch, StoppingHeight = StoppingHeight, DesiredVerticalSpeed = DesiredVerticalSpeed,
            AlignmentHeight = AlignmentHeight, TimeToTouchdown = TimeToTouchdown, CommandAcceleration = _commandAcceleration,
            TerrainContact = TerrainContact, DragArea = _model?.DragArea ?? double.NaN,
            MissionElapsed = clock.HasLaunched ? clock.Seconds : double.NaN, MissionClockRunning = clock.Mode == MissionClock.ClockMode.Flight
        };

        public string Phase => L10n.Get(State switch
        {
            LandingState.Planning => "landing.phase.planning", LandingState.WaitingForReturn => "landing.phase.waiting",
            LandingState.DeorbitBurn => "landing.phase.deorbit", LandingState.Entry => "landing.phase.entry",
            LandingState.PoweredDescent => "landing.phase.descent", LandingState.Landed => "landing.phase.landed",
            LandingState.Aborted => "landing.phase.aborted", _ => "landing.phase.idle"
        });
    }
}
