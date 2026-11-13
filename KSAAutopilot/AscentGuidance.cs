using System;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KSAAutopilot
{
    public class AscentGuidance
    {
        public enum AscentState
        {
            Idle,
            VerticalClimb,
            GravityTurn,
            CoastToApoapsis,
            Circularizing,
            OrbitAchieved,
            Aborted
        }

        public AscentState CurrentState { get; private set; } = AscentState.Idle;
        public bool IsActive => CurrentState == AscentState.VerticalClimb || CurrentState == AscentState.GravityTurn
            || CurrentState == AscentState.CoastToApoapsis || CurrentState == AscentState.Circularizing;
        internal LandingTelemetry LandingDebug;

        private readonly LaunchProfile _profile;
        private LaunchProfile _effectiveProfile;
        private LaunchProfile TurnProfile => _effectiveProfile ?? _profile;
        private readonly StagingController _staging;
        private readonly FlightRecorder _recorder;
        private int _flightNumber;
        private double _flightSince;
        private double _dtPlayer;
        private ulong _commandedThrusterFlags;
        private double _profilePitchDeg = double.NaN;
        private double _controllerPitchDeg = double.NaN;
        private double _pitchErrorDeg = double.NaN;
        private double _headingErrorDeg = double.NaN;
        private bool _pitchLagLimited;
        private bool _stagingEvaluated;
        private double _pointingErrorDeg = double.NaN;
        private double _flightPathPitchDeg = double.NaN;
        private double _angleOfAttackDeg = double.NaN;
        private double _attitudeRateLimitDegS = double.NaN;
        private bool _angleOfAttackLimited;

        private bool _warpTriggered = false;
        private float _commandedThrottle = 0f;
        private double _now = 0.0;
        private double _stateSince = 0.0;

        private readonly GravityTurnController _turn = new GravityTurnController();
        private bool _gtInit;
        private double _cmdPitchDeg = 90.0;
        private double _pitchRate = double.NaN;
        private double _yawRate = double.NaN;
        private Int128 _lastGuidanceSimulationNanoseconds;
        private bool _haveGuidanceSimulationTime;
        private double _guidanceSimulationDelta;
        private FlightComputer _savedFlightComputer;
        private float _savedRateLimit;
        private float _savedAngleDeadband;
        private FlightComputerRollMode _savedRollMode;
        private double _maxThrust, _maxMassFlow;
        private double _plannedDeltaV = double.NaN, _plannedBurnDuration = double.NaN, _burnLead = double.NaN;
        private double _targetTangentialSpeed = double.NaN, _radialSpeed = double.NaN, _tangentialSpeed = double.NaN;
        private double _predictedApoapsis = double.NaN;
        private string _throttleReason = "inactive";
        private double3 _orbitDirection;
        private bool _orbitDirectionInitialized;
        private double _alignmentWaitSince = double.NaN;
        private double _insertionStartSimulationSeconds;
        private double _insertionInitialDeltaV;
        private readonly ThrottleActuator _throttleActuator = new ThrottleActuator();
        private double _requestedThrottle;
        private readonly InsertionAttitudeGate _insertionAttitude = new InsertionAttitudeGate();
        private OrbitInsertion.Command _insertionCommand;
        private double _transverseRateDegS = double.NaN;
        private double _orbitalThrustPitchDeg = double.NaN;
        private double _engineMinimumThrottle = 0.1, _engineMinimumPulseSeconds = 0.001;
        private double _commandMinimumPulseSeconds = 0.001;
        private bool _minimumPulseSafe = true;
        private string _insertionMode = "inactive";
        private bool _trimPending, _trimStarted, _trimApoapsis;
        private double3 _trimDirection;

        private static FieldInfo _manualInputsField;
        private static FieldInfo _engineThrottleField;
        private static FieldInfo _engineOnField;
        private static FieldInfo _thrusterFlagsField;
        private static Type _thrusterFlagsEnumType;

        private static bool _reflectionInitialized = false;

        public AscentGuidance(LaunchProfile profile, StagingController staging)
        {
            _profile = profile;
            _staging = staging;
            _recorder = new FlightRecorder(profile, staging);
            EnsureReflection();
        }

        private static void EnsureReflection()
        {
            if (_reflectionInitialized) return;
            try
            {
                _manualInputsField = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_manualInputsField != null)
                {
                    Type inputType = _manualInputsField.FieldType;
                    _engineThrottleField = inputType.GetField("EngineThrottle", BindingFlags.Instance | BindingFlags.Public);
                    _engineOnField = inputType.GetField("EngineOn", BindingFlags.Instance | BindingFlags.Public);
                    _thrusterFlagsField = inputType.GetField("ThrusterCommandFlags", BindingFlags.Instance | BindingFlags.Public);

                    if (_thrusterFlagsField != null)
                    {
                        _thrusterFlagsEnumType = _thrusterFlagsField.FieldType;
                    }
                }
                _reflectionInitialized = true;
                Diag.Info("CONTROL_API", Diag.F($"manual_inputs={_manualInputsField != null} throttle={_engineThrottleField != null} engine_on={_engineOnField != null} thruster_flags={_thrusterFlagsField != null} attitude_control=native_custom_target"));
                if (_manualInputsField == null || _engineThrottleField == null || _engineOnField == null
                    || _thrusterFlagsField == null)
                    Diag.Warn("CONTROL_API", "Some throttle or attitude input members are missing; commands may not reach the vehicle. See ksa_api_dump.txt.");
            }
            catch (Exception ex)
            {
                Diag.Error("CONTROL_API", ex, "Initializing manual control reflection failed");
            }
        }

        private void SetState(AscentState state, string reason)
        {
            AscentState previous = CurrentState;
            double previousDuration = _now - _stateSince;
            if (!IsPoweredOrCoasting(state))
            {
                RestoreAttitudeSettings();
                _insertionAttitude.Reset();
            }
            CurrentState = state;
            _stateSince = _now;
            if (state == AscentState.GravityTurn)
            {
                _gtInit = false;
                _pitchRate = _yawRate = double.NaN;
                _profilePitchDeg = _controllerPitchDeg = _pitchErrorDeg = _headingErrorDeg = double.NaN;
                _pointingErrorDeg = _flightPathPitchDeg = _angleOfAttackDeg = _attitudeRateLimitDegS = double.NaN;
                _pitchLagLimited = _angleOfAttackLimited = false;
                _turn.Reset(double.NaN);
            }
            if (state == AscentState.CoastToApoapsis)
            {
                _orbitDirectionInitialized = false;
                _alignmentWaitSince = double.NaN;
                _insertionAttitude.Reset();
                _insertionMode = "coast_to_apoapsis";
            }
            Diag.Info("STATE", Diag.F($"flight={_flightNumber} from={previous} to={state} reason={reason} player_t_s={_now:F3} previous_state_duration_s={previousDuration:F3} altitude_m={_profile.CurrentAltitudeKm * 1000.0:F3} ap_m={_profile.CurrentApoapsisKm * 1000.0:F3} pe_m={_profile.CurrentPeriapsisKm * 1000.0:F3} commanded_throttle={_commandedThrottle:F3}"));
        }

        private static bool IsPoweredOrCoasting(AscentState state) => state == AscentState.VerticalClimb
            || state == AscentState.GravityTurn || state == AscentState.CoastToApoapsis || state == AscentState.Circularizing;

        public void Start()
        {
            _effectiveProfile = null;
            RestoreAttitudeSettings();
            _haveGuidanceSimulationTime = false;
            _flightNumber++;
            _flightSince = _now;
            SetState(AscentState.VerticalClimb, "user_start");
            _warpTriggered = false;
            _staging.Reset();
            _maxThrust = _maxMassFlow = 0.0;
            _throttleActuator.Reset();
            _insertionAttitude.Reset();
            _insertionMode = "inactive";
            _trimPending = _trimStarted = false;
            _plannedDeltaV = _plannedBurnDuration = _burnLead = _predictedApoapsis = double.NaN;
            _throttleReason = "launch";
            _orbitDirectionInitialized = false;
            _profile.SetStatus("status.launched");
        }

        internal void Start(FlightPlanSettings settings)
        {
            Start();
            _effectiveProfile = new LaunchProfile(); settings.Apply(_effectiveProfile);
            Diag.Info("FLIGHT_PLAN", Diag.F($"event=applied gt_start_m={settings.GravityTurnStartM:F3} gt_end_m={settings.GravityTurnEndM:F3} shape={settings.GravityTurnShape:F3} rate_deg_s={settings.MaxPitchRateDegS:F3} ascent_feedback=true"));
        }

        public void Abort(Vehicle vehicle, string reason = "user_abort")
        {
            SetState(AscentState.Aborted, reason);
            StopWarpSafely();
            SetThrottle(vehicle, 0f);
            SetThrusterFlags(vehicle, 0);
            FlightAttitude.StopTracking(vehicle?.FlightComputer);
            _profile.SetStatus("status.aborted");
            _stagingEvaluated = false;
            RecordTelemetry(vehicle, true, reason);
        }

        public void Reset(Vehicle vehicle)
        {
            _effectiveProfile = null;
            SetState(AscentState.Idle, "user_reset");
            StopWarpSafely();
            SetThrottle(vehicle, 0f);
            SetThrusterFlags(vehicle, 0);
            FlightAttitude.StopTracking(vehicle?.FlightComputer);
            _warpTriggered = false;
            _staging.Reset();
            _profile.SetStatus("status.ready");
            _stagingEvaluated = false;
            RecordTelemetry(vehicle, true, "user_reset");
        }

        public void TriggerManualWarpToApoapsis(Vehicle vehicle)
        {
            if (vehicle == null) return;
            try
            {
                UniverseTime nextApTime = vehicle.NextApoapsisTime;
                UniverseTime now = Universe.GetElapsedTime();
                UpdateInsertionPlan(vehicle);
                double leadSeconds = WarpLeadSeconds(vehicle);
                if (!double.IsFinite(leadSeconds)) return;
                UniverseTime lead = SecondsToUniverseTime(leadSeconds);

                if (nextApTime > now + lead)
                {
                    _warpTriggered = true;
                    Universe.AutoWarpTo(nextApTime - lead);
                    Diag.Info("WARP", Diag.F($"event=manual_warp_started lead_s={leadSeconds:F3} burn_duration_s={_plannedBurnDuration:F3}"));
                    _profile.SetStatus("status.manual_warp");
                }
            }
            catch (Exception ex)
            {
                Diag.Error("WARP", ex, "Manual warp to apoapsis failed");
            }
        }

        public void Update(Vehicle vehicle, double currentTime, double dtPlayer = 0.0)
        {
            if (vehicle == null) return;
            _now = currentTime;
            _dtPlayer = dtPlayer;
            _stagingEvaluated = false;

            try
            {
                Int128 simulationTime = Universe.GetElapsedTime().Nanoseconds;
                _guidanceSimulationDelta = _haveGuidanceSimulationTime
                    ? (double)(simulationTime - _lastGuidanceSimulationNanoseconds) / (double)UniverseTime.NANOSECONDS_PER_SECOND : 0.0;
                _lastGuidanceSimulationNanoseconds = simulationTime;
                _haveGuidanceSimulationTime = true;
                UpdateTelemetry(vehicle);

                if (!IsActive) return;
                if (IsActive && (!float.IsFinite(_profile.TargetApoapsisKm) || !float.IsFinite(_profile.TargetPeriapsisKm)
                    || _profile.TargetPeriapsisKm < 100.0f || _profile.TargetApoapsisKm < _profile.TargetPeriapsisKm))
                {
                    Abort(vehicle, "invalid_orbit_targets");
                    _profile.SetStatus("status.invalid_orbit");
                    return;
                }
                UpdateEnginePerformance(vehicle);

                double altitudeM = GetAltitude(vehicle);
                double apoapsisAltM = vehicle.Apoapsis;
                double periapsisAltM = vehicle.Periapsis;
                if (vehicle.Orbit != null)
                {
                    apoapsisAltM = vehicle.Orbit.ApoapsisAltitude;
                    periapsisAltM = vehicle.Orbit.PeriapsisAltitude;
                }

                switch (CurrentState)
                {
                    case AscentState.VerticalClimb:
                        HandleVerticalClimb(vehicle, altitudeM);
                        break;

                    case AscentState.GravityTurn:
                        HandleGravityTurn(vehicle, altitudeM, apoapsisAltM);
                        break;

                    case AscentState.CoastToApoapsis:
                        HandleCoastToApoapsis(vehicle, altitudeM, apoapsisAltM, periapsisAltM);
                        break;

                    case AscentState.Circularizing:
                        HandleCircularizing(vehicle, apoapsisAltM, periapsisAltM);
                        break;

                }
                // Evaluate the command from this step: a cutoff must also suppress staging immediately.
                if (IsActive)
                {
                    _staging.Update(vehicle, currentTime, _commandedThrottle);
                    _stagingEvaluated = true;
                }
            }
            finally { RecordTelemetry(vehicle); }
        }

        private void RecordTelemetry(Vehicle vehicle, bool force = false, string reason = "periodic")
        {
            if (vehicle == null) return;
            bool turning = CurrentState == AscentState.GravityTurn;
            _recorder.Record(vehicle, new GuidanceDebugSnapshot
            {
                Landing = LandingDebug,
                State = CurrentState, FlightNumber = _flightNumber, PlayerTime = _now,
                FlightElapsed = _flightNumber > 0 ? _now - _flightSince : double.NaN,
                StateElapsed = _now - _stateSince, CommandedThrottle = _commandedThrottle,
                CommandedThrusterFlags = _commandedThrusterFlags,
                ProfilePitch = turning ? _profilePitchDeg : double.NaN,
                ControllerPitch = turning ? _controllerPitchDeg : double.NaN,
                PitchRate = turning ? _pitchRate : double.NaN, YawRate = turning ? _yawRate : double.NaN,
                PitchError = turning ? _pitchErrorDeg : double.NaN,
                HeadingError = turning ? _headingErrorDeg : double.NaN,
                PitchLagLimited = turning && _pitchLagLimited, StagingEvaluated = _stagingEvaluated,
                PointingError = IsActive ? _pointingErrorDeg : double.NaN,
                FlightPathPitch = turning ? _flightPathPitchDeg : double.NaN,
                AngleOfAttack = turning ? _angleOfAttackDeg : double.NaN,
                RateLimit = turning ? _attitudeRateLimitDegS : double.NaN,
                TurnHoldReason = turning ? _turn.HoldReason : "not_turning",
                AngleOfAttackLimited = turning && _angleOfAttackLimited,
                SimulationDelta = _guidanceSimulationDelta,
                PlannedDeltaV = _plannedDeltaV, PlannedBurnDuration = _plannedBurnDuration, BurnLead = _burnLead,
                RadialSpeed = _radialSpeed, TangentialSpeed = _tangentialSpeed, TargetTangentialSpeed = _targetTangentialSpeed,
                PredictedApoapsis = _predictedApoapsis, ThrottleReason = _throttleReason, RequestedThrottle = _requestedThrottle,
                InsertionMode = _insertionMode, InsertionPitch = _insertionCommand.PitchDeg,
                RadialAccelerationDemand = _insertionCommand.RadialAcceleration,
                TangentialAccelerationDemand = _insertionCommand.TangentialAcceleration,
                TransverseRate = _transverseRateDegS, TvcSettling = _insertionAttitude.NeedsThrust,
                InsertionAttitudeReason = _insertionAttitude.Reason,
                OrbitalThrustPitch = _orbitalThrustPitchDeg,
                EngineMinimumThrottle = _engineMinimumThrottle, EngineMinimumPulseSeconds = _engineMinimumPulseSeconds,
                CommandMinimumPulseSeconds = _commandMinimumPulseSeconds, MinimumPulseSafe = _minimumPulseSafe
            }, _dtPlayer, force, reason);
        }

        private double GetAltitude(Vehicle vehicle)
        {
            double alt = vehicle.GetBarometricAltitude();
            if (alt <= 0) alt = vehicle.GetRadarAltitude();
            return alt;
        }

        private void HandleVerticalClimb(Vehicle vehicle, double altitudeM)
        {
            if (vehicle.FlightComputer != null)
            {
                vehicle.FlightComputer.TrackTarget(FlightComputerAttitudeTrackTarget.Up);
            }
            SetThrusterFlags(vehicle, 0);
            SetThrottle(vehicle, AscentThrottle(vehicle));
            _profile.DesiredPitchDeg = 90.0;

            double3 position = vehicle.GetPositionCci();
            double3 velocity = vehicle.GetSurfaceVelocityCci();
            double surfaceSpeed = velocity.Length();
            double verticalSpeed = double3.Dot(double3.Normalize(position), velocity);
            double measuredPitch = FlightAttitude.Pitch(position, FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)));
            _profile.CurrentPitchDeg = measuredPitch;

            if (GravityTurnController.ReadyToTurn(altitudeM, TurnProfile.GravityTurnStartAlt, surfaceSpeed,
                TurnProfile.GravityTurnMinSpeedMps, verticalSpeed, measuredPitch))
            {
                SetState(AscentState.GravityTurn, "turn_altitude_speed_and_upright_attitude_reached");
                _profile.SetStatus("status.turn_started");
            }
            else
            {
                _profile.SetStatus("status.vertical", altitudeM / 1000.0, _profile.GravityTurnStartAlt / 1000.0, surfaceSpeed, _profile.GravityTurnMinSpeedMps);
            }
        }

        private static double WrapDeg(double d)
        {
            while (d > 180.0) d -= 360.0;
            while (d < -180.0) d += 360.0;
            return d;
        }

        private void ConfigureAttitudeTracking(FlightComputer fc, double rateLimitDegS)
        {
            if (!ReferenceEquals(fc, _savedFlightComputer))
            {
                RestoreAttitudeSettings();
                _savedFlightComputer = fc;
                _savedRateLimit = fc.RateLimit;
                _savedAngleDeadband = fc.AngleDeadband;
                _savedRollMode = fc.RollMode;
                fc.ErrorAngleIntegrals = double3.Zero;
                Diag.Info("ATTITUDE", "controller=native_custom_target frame=EnuBody angles=radians roll=Decoupled");
            }
            fc.RateLimit = (float)(rateLimitDegS * FlightAttitude.DEG_TO_RAD);
            fc.AngleDeadband = (float)(0.2 * FlightAttitude.DEG_TO_RAD);
            fc.RollMode = FlightComputerRollMode.Decoupled;
        }

        private void RestoreAttitudeSettings()
        {
            if (_savedFlightComputer == null) return;
            _savedFlightComputer.RateLimit = _savedRateLimit;
            _savedFlightComputer.AngleDeadband = _savedAngleDeadband;
            _savedFlightComputer.RollMode = _savedRollMode;
            _savedFlightComputer.ErrorAngleIntegrals = double3.Zero;
            _savedFlightComputer = null;
        }

        private void SetThrusterFlags(Vehicle vehicle, ulong flags)
        {
            _commandedThrusterFlags = flags;
            try
            {
                if (_manualInputsField != null && _thrusterFlagsField != null && _thrusterFlagsEnumType != null && vehicle != null)
                {
                    object inputs = _manualInputsField.GetValue(vehicle);
                    if (inputs != null)
                    {
                        object enumVal = Enum.ToObject(_thrusterFlagsEnumType, flags);
                        _thrusterFlagsField.SetValue(inputs, enumVal);
                        _manualInputsField.SetValue(vehicle, inputs);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.EveryError("flagErr", 10.0, "CONTROL", ex, "Writing manual thruster flags failed");
            }
        }

        private void HandleGravityTurn(Vehicle vehicle, double altitudeM, double apoapsisAltM)
        {
            SetThrusterFlags(vehicle, 0);
            var fc = vehicle.FlightComputer;
            if (fc == null)
            {
                Abort(vehicle, "missing_flight_computer");
                _profile.SetStatus("status.no_computer");
                Diag.Warn("ATTITUDE", "No flight computer is available for damped gravity-turn control");
                return;
            }

            double3 position = vehicle.GetPositionCci();
            double3 velocity = vehicle.GetSurfaceVelocityCci();
            double speed = velocity.Length();
            doubleQuat currentControlToCci = FlightAttitude.ControlToCci(vehicle);
            double3 forward = FlightAttitude.Forward(currentControlToCci);
            double currentPitch = FlightAttitude.Pitch(position, forward);
            double currentHeading = FlightAttitude.Heading(position, forward);
            _profile.CurrentPitchDeg = _controllerPitchDeg = currentPitch;
            _flightPathPitchDeg = speed > 1.0 ? FlightAttitude.Pitch(position, velocity) : double.NaN;
            _angleOfAttackDeg = speed > 1.0 ? FlightAttitude.Angle(forward, velocity) : double.NaN;
            double3 controlRates = double3.Transform(vehicle.GetBodyRates(), doubleQuat.Inverse(fc.Ctrl2Asmb));
            _pitchRate = controlRates.Y * FlightAttitude.RAD_TO_DEG;
            _yawRate = controlRates.Z * FlightAttitude.RAD_TO_DEG;
            _profilePitchDeg = GravityTurnController.ProfilePitch(altitudeM, TurnProfile.GravityTurnStartAlt,
                TurnProfile.GravityTurnEndAlt, TurnProfile.GravityTurnShape);
            _profilePitchDeg = GravityTurnController.LimitPitchForApoapsis(_profilePitchDeg, altitudeM,
                apoapsisAltM, _profile.TargetApoapsisM);
            double dynamicPressure = vehicle.HasPhysicsBubble ? vehicle.ComputeDynamicPressure() : 0.0;
            if (_effectiveProfile != null)
            {
                ReadOrbitalMotion(vehicle, out double radius, out double bodyRadius, out double mu, out _);
                _profilePitchDeg = AdaptiveAscentControl.Pitch(_profilePitchDeg, mu, bodyRadius, radius, _radialSpeed,
                    _tangentialSpeed, _profile.TargetApoapsisM, _maxThrust / Math.Max(1, vehicle.TotalMass), dynamicPressure,
                    Math.Max(0, vehicle.Parent.GetAtmosphereRadius() - bodyRadius));
            }
            double targetPitchDeg = GravityTurnController.LimitPitchInAtmosphere(_profilePitchDeg,
                _flightPathPitchDeg, speed, dynamicPressure);
            _angleOfAttackLimited = targetPitchDeg > _profilePitchDeg + 1e-6;
            _attitudeRateLimitDegS = GravityTurnController.RateLimit(altitudeM, TurnProfile.MaxPitchRateDegS);

            if (!_gtInit)
            {
                _turn.Reset(currentPitch);
                _gtInit = true;
            }

            _pointingErrorDeg = FlightAttitude.Angle(forward,
                FlightAttitude.Direction(position, _turn.CommandedPitchDeg, _profile.LaunchHeadingDeg));
            _cmdPitchDeg = _turn.Update(targetPitchDeg, currentPitch, _pointingErrorDeg,
                controlRates.Length() * FlightAttitude.RAD_TO_DEG, _attitudeRateLimitDegS, _guidanceSimulationDelta);
            _pitchLagLimited = _turn.HoldReason != "none";
            _profile.DesiredPitchDeg = _cmdPitchDeg;
            _pitchErrorDeg = _cmdPitchDeg - currentPitch;
            _headingErrorDeg = double.IsFinite(currentHeading) ? WrapDeg(_profile.LaunchHeadingDeg - currentHeading) : double.NaN;

            double3 desiredForward = FlightAttitude.Direction(position, _cmdPitchDeg, _profile.LaunchHeadingDeg);
            doubleQuat targetToCci = FlightAttitude.AlignForward(currentControlToCci, desiredForward);
            ConfigureAttitudeTracking(fc, _attitudeRateLimitDegS);
            fc.AttitudeFrame = VehicleReferenceFrame.EnuBody;
            fc.CustomAttitudeTarget = FlightAttitude.CustomAngles(position, targetToCci);
            fc.TrackTarget(FlightComputerAttitudeTrackTarget.Custom);

            SetThrottle(vehicle, AscentThrottle(vehicle));

            if (apoapsisAltM >= _profile.TargetApoapsisM - Math.Max(500.0, _profile.TargetApoapsisM * 0.003)
                && _commandedThrottle < 0.15f)
            {
                SetThrottle(vehicle, 0f);
                SetThrusterFlags(vehicle, 0);
                SetState(AscentState.CoastToApoapsis, "target_apoapsis_reached");
                _warpTriggered = false;
                _profile.SetStatus("status.ap_reached");
            }
            else
            {
                _profile.SetStatus("status.turn", _cmdPitchDeg, currentPitch, apoapsisAltM / 1000.0);
            }
        }

        private void HandleCoastToApoapsis(Vehicle vehicle, double altitudeM, double apoapsisAltM, double periapsisAltM)
        {
            SetThrusterFlags(vehicle, 0);
            _throttleReason = "coasting_until_calculated_burn_start";
            UpdateInsertionPlan(vehicle);
            double secondsToAp = TimeToApoapsis(vehicle);
            AimForInsertion(vehicle, false);
            if (!IsActive) return;
            // Without RCS, repeated TVC corrections throughout the coast waste fuel and
            // move the burn window. Align only shortly before the calculated insertion.
            bool safeCoastOrbit = periapsisAltM >= Math.Max(100000.0,
                vehicle.Parent.GetAtmosphereRadius() - vehicle.Parent.MeanRadius + 5000.0);
            bool steeringWindow = !Universe.IsAutoWarpActive && altitudeM >= 100000.0
                && (secondsToAp <= Math.Max(30.0, _burnLead + _pointingErrorDeg / 2.0 + 10.0)
                    || !safeCoastOrbit && (_radialSpeed <= 0.0 || _transverseRateDegS > 0.3));
            float coastThrottle = _insertionAttitude.Update(_pointingErrorDeg, _transverseRateDegS,
                HasUsableRcs(vehicle), steeringWindow) ? SteeringThrottle(vehicle) : 0.0f;
            SetThrottle(vehicle, coastThrottle);
            if (coastThrottle > 0.0f) _throttleReason = "coast_tvc_" + _insertionAttitude.Reason;

            if (OrbitInsertion.OrbitAchieved(apoapsisAltM, periapsisAltM, _profile.TargetApoapsisM, _profile.TargetPeriapsisM))
            {
                FinishOrbit(vehicle, apoapsisAltM, periapsisAltM);
                return;
            }
            if (altitudeM < 100000.0 && apoapsisAltM < _profile.TargetApoapsisM * 0.97 && secondsToAp > 30.0)
            {
                StopWarpSafely();
                SetState(AscentState.GravityTurn, "apoapsis_lost_to_atmospheric_drag");
                return;
            }
            if (OrbitInsertion.ReadyToBurn(altitudeM, secondsToAp, _plannedBurnDuration, _radialSpeed,
                periapsisAltM, Math.Max(0, vehicle.Parent.GetAtmosphereRadius() - vehicle.Parent.MeanRadius)))
            {
                StopWarpSafely();
                SetThrottle(vehicle, 0f);
                _insertionStartSimulationSeconds = Universe.GetElapsedSeconds();
                _insertionInitialDeltaV = _plannedDeltaV;
                _insertionMode = "capture_periapsis";
                SetState(AscentState.Circularizing, "time_to_apoapsis_within_half_calculated_burn_duration");
                Diag.Info("INSERTION", Diag.F($"event=burn_window_reached time_to_ap_s={secondsToAp:F3} delta_v_m_s={_plannedDeltaV:F3} burn_duration_s={_plannedBurnDuration:F3} lead_s={_burnLead:F3} pointing_error_deg={_pointingErrorDeg:F3}"));
                _profile.SetStatus("status.insertion_ready");
                return;
            }
            double warpLead = WarpLeadSeconds(vehicle);
            if (_profile.AutoTimeskipToApoapsis && altitudeM >= 100000.0 && !_warpTriggered
                && _pointingErrorDeg <= 3.0 && _transverseRateDegS < 0.15 && !_insertionAttitude.NeedsThrust
                && double.IsFinite(warpLead) && secondsToAp > warpLead + 10.0)
            {
                try
                {
                    _warpTriggered = true;
                    Universe.AutoWarpTo(vehicle.NextApoapsisTime - SecondsToUniverseTime(warpLead));
                    Diag.Info("WARP", Diag.F($"event=auto_warp_started lead_s={warpLead:F3} burn_duration_s={_plannedBurnDuration:F3} time_to_ap_s={secondsToAp:F3}"));
                }
                catch (Exception ex) { Diag.Error("WARP", ex, "Automatic warp to calculated insertion lead failed"); }
            }
            if (altitudeM >= 100000.0 && _radialSpeed < 0.0 && !double.IsFinite(_plannedBurnDuration))
            {
                FailInsertion(vehicle, "no_insertion_engine_performance", "status.no_thrust");
                return;
            }
            _profile.SetStatus("status.coast", secondsToAp, _plannedBurnDuration, _pointingErrorDeg);
        }

        private void HandleCircularizing(Vehicle vehicle, double apoapsisAltM, double periapsisAltM)
        {
            if (!_trimPending || _trimStarted) StopWarpSafely();
            SetThrusterFlags(vehicle, 0);
            UpdateInsertionPlan(vehicle);
            if (OrbitInsertion.OrbitAchieved(apoapsisAltM, periapsisAltM, _profile.TargetApoapsisM, _profile.TargetPeriapsisM))
            {
                FinishOrbit(vehicle, apoapsisAltM, periapsisAltM);
                return;
            }
            if (!_trimPending && periapsisAltM >= _profile.TargetPeriapsisM - OrbitInsertion.OrbitTolerance(_profile.TargetPeriapsisM)
                && (apoapsisAltM > _profile.TargetApoapsisM + OrbitInsertion.OrbitTolerance(_profile.TargetApoapsisM)
                    || periapsisAltM > _profile.TargetPeriapsisM + OrbitInsertion.OrbitTolerance(_profile.TargetPeriapsisM)))
                BeginOrbitTrim(vehicle, periapsisAltM <= _profile.TargetPeriapsisM + OrbitInsertion.OrbitTolerance(_profile.TargetPeriapsisM));
            if (_trimPending)
            {
                HandleOrbitTrim(vehicle, apoapsisAltM, periapsisAltM);
                return;
            }
            AimForInsertion(vehicle, true);
            if (!IsActive) return;
            if (GetAltitude(vehicle) < 100000.0 && _radialSpeed < 0.0)
            {
                FailInsertion(vehicle, "insertion_descending_into_atmosphere", "status.reentry");
                return;
            }
            bool tvcNeeded = _insertionAttitude.Update(_pointingErrorDeg, _transverseRateDegS,
                HasUsableRcs(vehicle), true);
            if (_pointingErrorDeg > 5.0)
            {
                SetThrottle(vehicle, SteeringThrottle(vehicle));
                _throttleReason = _commandedThrottle > 0.0 ? "low_thrust_for_tvc_alignment" : "waiting_for_insertion_alignment";
                if (!double.IsFinite(_alignmentWaitSince)) _alignmentWaitSince = Universe.GetElapsedSeconds();
                if (Universe.GetElapsedSeconds() - _alignmentWaitSince > 60.0)
                    FailInsertion(vehicle, "insertion_attitude_not_following", "status.attitude_failed");
                else _profile.SetStatus("status.aligning", _pointingErrorDeg);
                return;
            }
            _alignmentWaitSince = double.NaN;
            double radius, bodyRadius, mu;
            ReadOrbitalMotion(vehicle, out radius, out bodyRadius, out mu, out double3 tangent);
            double maxAcceleration = _maxThrust / Math.Max(1.0, vehicle.TotalMass);
            double throttle = _insertionCommand.Throttle;
            if (tvcNeeded) throttle = Math.Max(throttle, 0.01);
            double pitch = ReadOrbitalThrustPitch(vehicle, tangent);
            double guarded = OrbitInsertion.GuardInsertionThrottle(mu, bodyRadius, radius, _radialSpeed,
                _tangentialSpeed, _profile.TargetApoapsisM, maxAcceleration, pitch, throttle,
                _guidanceSimulationDelta, out _predictedApoapsis);
            _throttleReason = guarded < throttle - 1e-5 ? "predicted_apoapsis_ceiling"
                : apoapsisAltM > OrbitInsertion.ApoapsisCeiling(_profile.TargetApoapsisM) ? "reducing_excess_apoapsis"
                : tvcNeeded ? "insertion_tvc_" + _insertionAttitude.Reason : "capture_periapsis_velocity_feedback";
            SetThrottle(vehicle, (float)guarded);
            if (Universe.GetElapsedSeconds() - _insertionStartSimulationSeconds > 600.0
                || (_insertionInitialDeltaV > 50.0 && Universe.GetElapsedSeconds() - _insertionStartSimulationSeconds > 90.0
                    && _plannedDeltaV >= _insertionInitialDeltaV * 0.98))
            {
                FailInsertion(vehicle, "insertion_not_converging", "status.insertion_failed");
                return;
            }
            _profile.SetStatus("status.insertion", _plannedDeltaV, apoapsisAltM / 1000.0, periapsisAltM / 1000.0);
        }

        private void UpdateEnginePerformance(Vehicle vehicle)
        {
            var performance = vehicle.FlightComputer?.ActiveEnginePerformanceMax;
            if (performance.HasValue && performance.Value.Thrust > 1.0f)
            {
                _maxThrust = performance.Value.Thrust;
                _maxMassFlow = performance.Value.MassFlowRate;
            }
            double minimum = Math.Max(0.001, performance?.MinThrottle ?? 0.1);
            double pulse = 0.001;
            if (vehicle.Parts != null)
            {
                minimum = Math.Max(minimum, vehicle.GetMinThrottle());
                // KSA reports the LOWEST core minimum inside each engine controller.
                // Every core clamps independently; use the highest active-core limit so
                // a 10% command cannot silently produce roughly 20% main-engine thrust.
                foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
                {
                    if (!engine.IsActive) continue;
                    minimum = Math.Max(minimum, engine.MinimumThrottle);
                    foreach (RocketCore core in engine.Cores)
                    {
                        minimum = Math.Max(minimum, core.MinimumThrottle);
                        pulse = Math.Max(pulse, core.MinimumPulseTime);
                    }
                }
            }
            minimum = Math.Clamp(minimum, 0.001, 1.0);
            if (Math.Abs(minimum - _engineMinimumThrottle) > 0.0001 || Math.Abs(pulse - _engineMinimumPulseSeconds) > 0.0001)
                Diag.Info("PROPULSION", Diag.F($"effective_min_throttle={minimum:F6} reported_min_throttle={performance?.MinThrottle:F6} minimum_pulse_s={pulse:F6} source=active_engine_and_core_limits"));
            _engineMinimumThrottle = minimum;
            _engineMinimumPulseSeconds = pulse;
        }

        private void ReadOrbitalMotion(Vehicle vehicle, out double radius, out double bodyRadius, out double mu,
            out double3 tangent)
        {
            double3 position = vehicle.GetPositionCci();
            double3 velocity = vehicle.GetVelocityCci();
            radius = position.Length();
            double3 up = position / radius;
            _radialSpeed = double3.Dot(velocity, up);
            double3 horizontalVelocity = velocity - up * _radialSpeed;
            _tangentialSpeed = horizontalVelocity.Length();
            tangent = _tangentialSpeed > 1.0 ? horizontalVelocity / _tangentialSpeed
                : FlightAttitude.Direction(position, 0.0, _profile.LaunchHeadingDeg);
            bodyRadius = vehicle.Parent.MeanRadius;
            mu = vehicle.Parent.Mu;
        }

        private float AscentThrottle(Vehicle vehicle)
        {
            ReadOrbitalMotion(vehicle, out double radius, out double bodyRadius, out double mu, out _);
            double pitch = FlightAttitude.Pitch(vehicle.GetPositionCci(), FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)));
            double throttle = OrbitInsertion.AscentThrottle(mu, bodyRadius, radius, _radialSpeed, _tangentialSpeed,
                _profile.TargetApoapsisM, _maxThrust / Math.Max(1.0, vehicle.TotalMass), pitch,
                _guidanceSimulationDelta, out _predictedApoapsis);
            _throttleReason = throttle < 0.999 ? "predicted_ascent_apoapsis" : "ascent_full_thrust";
            return (float)throttle;
        }

        private void UpdateInsertionPlan(Vehicle vehicle)
        {
            ReadOrbitalMotion(vehicle, out double radius, out double bodyRadius, out double mu, out _);
            _insertionCommand = _trimStarted
                ? OrbitInsertion.Trim(mu, radius, _radialSpeed, _tangentialSpeed,
                    bodyRadius + _profile.TargetPeriapsisM, bodyRadius + _profile.TargetApoapsisM,
                    _maxThrust / Math.Max(1.0, vehicle.TotalMass))
                : OrbitInsertion.Capture(mu, radius, _radialSpeed, _tangentialSpeed,
                    bodyRadius + _profile.TargetPeriapsisM, bodyRadius + _profile.TargetApoapsisM,
                    _maxThrust / Math.Max(1.0, vehicle.TotalMass));
            _targetTangentialSpeed = _insertionCommand.TargetSpeed;
            double apRadius = bodyRadius + (vehicle.Orbit?.ApoapsisAltitude ?? vehicle.Apoapsis);
            double apSpeed = OrbitInsertion.ApoapsisTangentialSpeed(mu, radius, _tangentialSpeed, apRadius);
            double apTargetSpeed = OrbitInsertion.CaptureTangentialSpeed(mu, apRadius,
                bodyRadius + _profile.TargetPeriapsisM, bodyRadius + _profile.TargetApoapsisM);
            _plannedDeltaV = CurrentState == AscentState.Circularizing ? _insertionCommand.DeltaV
                : Math.Max(0.0, apTargetSpeed - apSpeed);
            _plannedBurnDuration = OrbitInsertion.BurnDuration(_plannedDeltaV, vehicle.TotalMass, _maxThrust, _maxMassFlow);
            _burnLead = OrbitInsertion.BurnLead(_plannedBurnDuration);
        }

        private void AimForInsertion(Vehicle vehicle, bool powered)
        {
            var fc = vehicle.FlightComputer;
            if (fc == null) { Abort(vehicle, "missing_flight_computer"); return; }
            ReadOrbitalMotion(vehicle, out double radius, out double bodyRadius, out double mu, out double3 tangent);
            double3 up = vehicle.GetPositionCci() / radius;
            ReadOrbitalThrustPitch(vehicle, tangent);
            double pitch = powered ? _insertionCommand.PitchDeg : 0.0;
            double3 desired = double3.Normalize(tangent * Math.Cos(pitch * FlightAttitude.DEG_TO_RAD)
                + up * Math.Sin(pitch * FlightAttitude.DEG_TO_RAD));
            AimDirection(vehicle, desired, powered);
        }

        private void AimDirection(Vehicle vehicle, double3 desired, bool powered, bool inertial = false)
        {
            var fc = vehicle.FlightComputer;
            if (fc == null) { Abort(vehicle, "missing_flight_computer"); return; }
            doubleQuat current = FlightAttitude.ControlToCci(vehicle);
            double3 forward = FlightAttitude.Forward(current);
            double3 controlRates = double3.Transform(vehicle.GetBodyRates(), doubleQuat.Inverse(fc.Ctrl2Asmb));
            _transverseRateDegS = Math.Sqrt(controlRates.Y * controlRates.Y + controlRates.Z * controlRates.Z) * FlightAttitude.RAD_TO_DEG;
            if (!_orbitDirectionInitialized)
            {
                _orbitDirection = forward;
                _orbitDirectionInitialized = true;
            }
            double lag = FlightAttitude.Angle(forward, _orbitDirection);
            double angle = FlightAttitude.Angle(_orbitDirection, desired);
            double rate = powered ? 3.0 : 2.0;
            if (lag <= 4.0 && _transverseRateDegS <= rate + 0.3 && _guidanceSimulationDelta > 0.0 && angle > 1e-6)
            {
                _orbitDirection = FlightAttitude.SlewDirection(_orbitDirection, desired, current,
                    rate * Math.Min(_guidanceSimulationDelta, 0.25));
            }
            ConfigureAttitudeTracking(fc, rate);
            fc.AttitudeFrame = inertial ? VehicleReferenceFrame.EclBody : VehicleReferenceFrame.EnuBody;
            doubleQuat target = FlightAttitude.AlignForward(current, _orbitDirection);
            fc.CustomAttitudeTarget = inertial ? FlightAttitude.InertialCustomAngles(vehicle, target)
                : FlightAttitude.CustomAngles(vehicle.GetPositionCci(), target);
            fc.TrackTarget(FlightComputerAttitudeTrackTarget.Custom);
            _profile.DesiredPitchDeg = FlightAttitude.Pitch(vehicle.GetPositionCci(), _orbitDirection);
            // Burn gating compares with the final required direction, including slew still pending.
            _pointingErrorDeg = FlightAttitude.Angle(forward, desired);
        }

        private float SteeringThrottle(Vehicle vehicle, double requested = 0.05)
        {
            if (HasUsableRcs(vehicle)) return 0.0f;
            ReadOrbitalMotion(vehicle, out double radius, out double bodyRadius, out double mu, out double3 tangent);
            double pitch = ReadOrbitalThrustPitch(vehicle, tangent);
            return (float)OrbitInsertion.GuardInsertionThrottle(mu, bodyRadius, radius, _radialSpeed, _tangentialSpeed,
                _profile.TargetApoapsisM, _maxThrust / Math.Max(1.0, vehicle.TotalMass), pitch, requested,
                _guidanceSimulationDelta, out _predictedApoapsis);
        }

        private static bool HasUsableRcs(Vehicle vehicle)
        {
            var fc = vehicle.FlightComputer;
            return fc != null && fc.RCSMode == FlightComputerRCSMode.Enabled
                && fc.RcsTorqueAuthority.Y > 0.0f && fc.RcsTorqueAuthority.Z > 0.0f;
        }

        private double ReadOrbitalThrustPitch(Vehicle vehicle, double3 tangent)
        {
            return _orbitalThrustPitchDeg = FlightAttitude.OrbitalThrustPitch(vehicle.GetPositionCci(),
                FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)), tangent);
        }

        private void BeginOrbitTrim(Vehicle vehicle, bool lowerApoapsis)
        {
            _trimPending = true;
            _trimStarted = false;
            _trimApoapsis = lowerApoapsis;
            _warpTriggered = false;
            _insertionAttitude.Reset();
            double3 position = vehicle.GetPositionCci(), velocity = vehicle.GetVelocityCci();
            double3 normal = double3.Normalize(double3.Cross(position, velocity));
            double3 eccentricity = double3.Cross(velocity, double3.Cross(position, velocity)) / vehicle.Parent.Mu
                - double3.Normalize(position);
            double3 periapsisUp = eccentricity.LengthSquared() > 1e-12 ? double3.Normalize(eccentricity) : double3.Normalize(position);
            // Prograde at AP points almost exactly retrograde at the opposite PE. Holding
            // this inertial direction avoids an unnecessary powered 180-degree flip.
            _trimDirection = double3.Cross(normal, periapsisUp) * (lowerApoapsis ? -1.0 : 1.0);
            _insertionMode = lowerApoapsis ? "coast_to_periapsis_for_apoapsis_trim" : "coast_to_apoapsis_for_periapsis_trim";
            SetThrottle(vehicle, 0f);
            Diag.Info("INSERTION", Diag.F($"event=periapsis_captured next_mode={_insertionMode} ap_m={vehicle.Orbit?.ApoapsisAltitude:F3} pe_m={vehicle.Orbit?.PeriapsisAltitude:F3}"));
        }

        private void HandleOrbitTrim(Vehicle vehicle, double ap, double pe)
        {
            ReadOrbitalMotion(vehicle, out double radius, out double bodyRadius, out double mu, out double3 tangent);
            double acceleration = _maxThrust / Math.Max(1.0, vehicle.TotalMass);
            ReadOrbitalThrustPitch(vehicle, tangent);
            double apsisRadius = bodyRadius + (_trimApoapsis ? pe : ap);
            double speedAtApsis = radius * _tangentialSpeed / apsisRadius;
            double targetAtApsis = OrbitInsertion.TargetTangentialSpeed(mu, apsisRadius,
                bodyRadius + _profile.TargetPeriapsisM, bodyRadius + _profile.TargetApoapsisM);
            double remaining = Math.Abs(targetAtApsis - speedAtApsis);
            double duration = OrbitInsertion.BurnDuration(remaining, vehicle.TotalMass, _maxThrust, _maxMassFlow);
            double timeToApsis = _trimApoapsis
                ? (double)(vehicle.NextPeriapsisTime.Nanoseconds - Universe.GetElapsedTime().Nanoseconds) / (double)UniverseTime.NANOSECONDS_PER_SECOND
                : TimeToApoapsis(vehicle);
            if (!_trimStarted)
            {
                _plannedDeltaV = remaining;
                _plannedBurnDuration = duration;
                _burnLead = OrbitInsertion.BurnLead(duration);
                double3 desired = timeToApsis < 20.0 ? -tangent : _trimDirection;
                AimDirection(vehicle, desired, false, true);
                if (!IsActive) return;
                bool settling = _insertionAttitude.Update(_pointingErrorDeg, _transverseRateDegS,
                    HasUsableRcs(vehicle), !Universe.IsAutoWarpActive, 0.01);
                SetThrottle(vehicle, settling ? SteeringThrottle(vehicle, 0.005) : 0f);
                _throttleReason = settling ? "trim_tvc_" + _insertionAttitude.Reason : "coasting_to_trim_apsis";
                if (OrbitInsertion.ReadyToTrim(timeToApsis, duration, Math.Abs(radius - apsisRadius), _radialSpeed, _trimApoapsis))
                {
                    StopWarpSafely();
                    _trimStarted = true;
                    _insertionStartSimulationSeconds = Universe.GetElapsedSeconds();
                    _alignmentWaitSince = double.NaN;
                    _insertionMode = _trimApoapsis ? "lower_apoapsis_at_periapsis" : "lower_periapsis_at_apoapsis";
                    Diag.Info("INSERTION", Diag.F($"event=trim_burn_window mode={_insertionMode} time_to_apsis_s={timeToApsis:F3} delta_v_m_s={remaining:F3}"));
                }
                else if (_profile.AutoTimeskipToApoapsis && !_warpTriggered && !settling
                    && _pointingErrorDeg < 1.0 && _transverseRateDegS < 0.01 && timeToApsis > 45.0)
                {
                    UniverseTime nextApsis = _trimApoapsis ? vehicle.NextPeriapsisTime : vehicle.NextApoapsisTime;
                    _warpTriggered = true;
                    Universe.AutoWarpTo(nextApsis - SecondsToUniverseTime(Math.Max(30.0, _burnLead + 15.0)));
                    Diag.Info("WARP", "event=auto_warp_to_orbit_trim mode=" + _insertionMode);
                }
                _profile.SetStatus("status.trim_wait", new LocalizedMessage(_trimApoapsis ? "apsis.periapsis" : "apsis.apoapsis", Array.Empty<object>()), timeToApsis);
                return;
            }
            _insertionCommand = OrbitInsertion.Trim(mu, radius, _radialSpeed, _tangentialSpeed,
                bodyRadius + _profile.TargetPeriapsisM, bodyRadius + _profile.TargetApoapsisM, acceleration);
            _targetTangentialSpeed = _insertionCommand.TargetSpeed;
            _plannedDeltaV = _insertionCommand.DeltaV;
            _plannedBurnDuration = OrbitInsertion.BurnDuration(_plannedDeltaV, vehicle.TotalMass, _maxThrust, _maxMassFlow);
            AimForInsertion(vehicle, true);
            if (!IsActive) return;
            _insertionAttitude.Update(_pointingErrorDeg, _transverseRateDegS, HasUsableRcs(vehicle), true);
            double throttle = _pointingErrorDeg > 5.0 ? SteeringThrottle(vehicle, 0.005) : _insertionCommand.Throttle;
            double pitch = ReadOrbitalThrustPitch(vehicle, tangent);
            double guarded = OrbitInsertion.GuardInsertionThrottle(mu, bodyRadius, radius, _radialSpeed,
                _tangentialSpeed, _profile.TargetApoapsisM, acceleration, pitch, throttle, _guidanceSimulationDelta, out _predictedApoapsis);
            SetThrottle(vehicle, (float)guarded);
            _throttleReason = guarded < throttle - 1e-5 ? "trim_apoapsis_guard" : "trim_velocity_feedback";
            if (Math.Abs((_trimApoapsis ? ap - _profile.TargetApoapsisM : pe - _profile.TargetPeriapsisM))
                <= OrbitInsertion.OrbitTolerance(_trimApoapsis ? _profile.TargetApoapsisM : _profile.TargetPeriapsisM))
            {
                SetThrottle(vehicle, 0f);
                _trimPending = _trimStarted = false;
                _insertionMode = "capture_periapsis";
                _insertionInitialDeltaV = _plannedDeltaV;
            }
            if (Universe.GetElapsedSeconds() - _insertionStartSimulationSeconds > 180.0)
                FailInsertion(vehicle, "orbit_trim_not_converging", "status.trim_failed");
            else _profile.SetStatus("status.trim", ap / 1000.0, pe / 1000.0);
        }

        private double WarpLeadSeconds(Vehicle vehicle)
        {
            if (!double.IsFinite(_burnLead)) return double.NaN;
            double rate = 2.0;
            double pointing = double.IsFinite(_pointingErrorDeg) ? _pointingErrorDeg : 90.0;
            return Math.Max(30.0, _burnLead + pointing / rate + 15.0);
        }

        private static double TimeToApoapsis(Vehicle vehicle)
            => (double)(vehicle.NextApoapsisTime.Nanoseconds - Universe.GetElapsedTime().Nanoseconds)
                / (double)UniverseTime.NANOSECONDS_PER_SECOND;

        private void FinishOrbit(Vehicle vehicle, double ap, double pe)
        {
            StopWarpSafely();
            SetThrottle(vehicle, 0f);
            SetThrusterFlags(vehicle, 0);
            FlightAttitude.StopTracking(vehicle?.FlightComputer);
            _throttleReason = "both_orbit_apsides_reached";
            _insertionMode = "orbit_achieved";
            SetState(AscentState.OrbitAchieved, "apoapsis_and_periapsis_within_target_tolerance");
            _profile.SetStatus("status.orbit", ap / 1000.0, pe / 1000.0);
            Diag.Info("AUTOPILOT", Diag.F($"event=orbit_completed controls_released=true mission_clock_continues=true ap_m={ap:F3} pe_m={pe:F3}"));
        }

        private void FailInsertion(Vehicle vehicle, string reason, string message)
        {
            Diag.Warn("INSERTION", Diag.F($"reason={reason} delta_v_m_s={_plannedDeltaV:F3} time_to_ap_s={TimeToApoapsis(vehicle):F3} pointing_error_deg={_pointingErrorDeg:F3} predicted_ap_m={_predictedApoapsis:F3}"));
            _throttleReason = reason;
            Abort(vehicle, reason);
            _profile.SetStatus(message);
        }

        private void StopWarpSafely()
        {
            try
            {
                if (Universe.IsAutoWarpActive)
                {
                    Universe.AutoWarpStop(true);
                    Diag.Info("WARP", "event=warp_stopped simulation_speed_reset=true");
                }
            }
            catch (Exception ex) { Diag.EveryError("warpStop", 5.0, "WARP", ex, "Stopping auto warp failed"); }
        }

        private void SetThrottle(Vehicle vehicle, float throttle)
        {
            _requestedThrottle = throttle;
            double minimum = _engineMinimumThrottle;
            // TVC reads the previous gimbal thrust. One-frame ignitions may never
            // leave a powered control step in which the native controller can damp rotation.
            bool rotating = _insertionAttitude.NeedsThrust && (_pointingErrorDeg > 5.0 || _transverseRateDegS > 0.3);
            double minimumPulse = rotating ? Math.Max(0.12, _engineMinimumPulseSeconds) : _engineMinimumPulseSeconds;
            bool minimumSafe = true;
            if (throttle > 0.001f && throttle < minimum && vehicle?.Parent != null && _maxThrust > 0.0)
            {
                ReadOrbitalMotion(vehicle, out double radius, out double bodyRadius, out double mu, out double3 tangent);
                double pitch = ReadOrbitalThrustPitch(vehicle, tangent);
                // Only predict the upcoming minimum-throttle pulse, not a continuous minimum burn.
                double step = Math.Max(minimumPulse, Math.Clamp(_dtPlayer * Universe.SimulationSpeed, 0.01, 0.25));
                double acceleration = _maxThrust / Math.Max(1.0, vehicle.TotalMass);
                double limit = CurrentState == AscentState.GravityTurn || CurrentState == AscentState.VerticalClimb
                    ? _profile.TargetApoapsisM : OrbitInsertion.ApoapsisCeiling(_profile.TargetApoapsisM);
                double safe = OrbitInsertion.GuardImpulse(mu, bodyRadius, radius, _radialSpeed, _tangentialSpeed,
                    limit, acceleration, pitch, minimum, step, out _);
                minimumSafe = safe >= minimum - 1e-6;
            }
            double pulseStep = Math.Clamp(_dtPlayer * Universe.SimulationSpeed, 0.0, 0.1);
            _commandMinimumPulseSeconds = minimumPulse;
            _minimumPulseSafe = minimumSafe;
            throttle = (float)_throttleActuator.Command(throttle, minimum, pulseStep, minimumSafe, minimumPulse);
            _commandedThrottle = throttle;
            try
            {
                if (_manualInputsField != null && vehicle != null)
                {
                    object inputs = _manualInputsField.GetValue(vehicle);
                    if (inputs != null)
                    {
                        _engineThrottleField?.SetValue(inputs, throttle);
                        _engineOnField?.SetValue(inputs, throttle > 0.001f);
                        _manualInputsField.SetValue(vehicle, inputs);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.EveryError("thrErr", 10.0, "CONTROL", ex, "Writing manual engine throttle failed");
            }
        }

        private void UpdateTelemetry(Vehicle vehicle)
        {
            try
            {
                _profile.CurrentAltitudeKm = GetAltitude(vehicle) / 1000.0;
                _profile.CurrentPitchDeg = FlightAttitude.Pitch(vehicle.GetPositionCci(),
                    FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)));
                if (vehicle.Orbit != null)
                {
                    _profile.CurrentApoapsisKm = vehicle.Orbit.ApoapsisAltitude / 1000.0;
                    _profile.CurrentPeriapsisKm = vehicle.Orbit.PeriapsisAltitude / 1000.0;
                    _profile.CurrentOrbitalSpeed = vehicle.OrbitalSpeed;
                }
                if (vehicle.Parts != null)
                {
                    _profile.CurrentPropellantFraction = vehicle.Parts.TotalFillFraction;
                }
            }
            catch (Exception ex) { Diag.EveryError("uiTelemetry", 5.0, "TELEMETRY_READ", ex, "Updating GUI telemetry failed"); }
        }

        private static UniverseTime SecondsToUniverseTime(double seconds)
        {
            return new UniverseTime(seconds);
        }
    }
}
