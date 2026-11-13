using System;
using Brutal.Numerics;
using KSA;

namespace KSAAutopilot
{
    internal struct GuidanceDebugSnapshot
    {
        public LandingTelemetry Landing;
        public AscentGuidance.AscentState State;
        public int FlightNumber;
        public double PlayerTime, FlightElapsed, StateElapsed;
        public float CommandedThrottle;
        public double RequestedThrottle;
        public ulong CommandedThrusterFlags;
        public double ProfilePitch, ControllerPitch, PitchRate, YawRate;
        public double PitchError, HeadingError, PointingError, FlightPathPitch, AngleOfAttack, RateLimit, SimulationDelta;
        public double PlannedDeltaV, PlannedBurnDuration, BurnLead, RadialSpeed, TangentialSpeed;
        public double TargetTangentialSpeed, PredictedApoapsis;
        public double RadialAccelerationDemand, TangentialAccelerationDemand, InsertionPitch, TransverseRate;
        public double OrbitalThrustPitch;
        public double EngineMinimumThrottle, EngineMinimumPulseSeconds;
        public double CommandMinimumPulseSeconds;
        public bool MinimumPulseSafe;
        public string InsertionMode, InsertionAttitudeReason;
        public bool TvcSettling;
        public string ThrottleReason;
        public string TurnHoldReason;
        public bool AngleOfAttackLimited;
        public bool PitchLagLimited, StagingEvaluated;
    }

    /// <summary>Samples after guidance commands, using real time for pacing and simulation time for physics.</summary>
    internal sealed class FlightRecorder
    {
        private readonly LaunchProfile _profile;
        private readonly StagingController _staging;
        private Vehicle _lastVehicle;
        private IParentBody _lastParent;
        private AscentGuidance.AscentState _lastState;
        private LandingState _lastLandingState;
        private int _lastFlightNumber = -1;
        private int _lastStageEventNumber = -1;
        private long _lastSampleTick;
        private long _sampleNumber;
        private ulong _flagsSeen;
        private bool _wasEnabled = true;
        private string _lastConfiguration;
        private double _lastSimulationTime = double.NaN;
        private Int128 _lastSimulationNanoseconds;
        private Int128 _flightSimulationStart;
        private bool _flightSimulationStartValid;
        private double3 _lastVelocity;
        private bool _lastVelocityValid;
        private static double3 MissingVector => new double3(double.NaN, double.NaN, double.NaN);

        public FlightRecorder(LaunchProfile profile, StagingController staging)
        {
            _profile = profile;
            _staging = staging;
        }

        public void Record(Vehicle vehicle, GuidanceDebugSnapshot guidance, double dtPlayer,
            bool force = false, string reason = "periodic")
        {
            try
            {
                RecordCore(vehicle, guidance, dtPlayer, force, reason);
            }
            catch (Exception ex)
            {
                // Diagnostic failures must never interrupt throttle or attitude control.
                Diag.EveryError("flight_recorder", 5.0, "DIAGNOSTICS", ex, "Building flight telemetry failed");
            }
        }

        private void RecordCore(Vehicle vehicle, GuidanceDebugSnapshot guidance, double dtPlayer, bool force, string reason)
        {
            _flagsSeen |= guidance.CommandedThrusterFlags;
            if (!_profile.DebugLoggingEnabled)
            {
                if (_wasEnabled) Diag.Info("CONFIG", "debug_logging=false");
                _wasEnabled = false;
                _flagsSeen = 0;
                _lastVelocityValid = false;
                return;
            }

            long tick = Environment.TickCount64;
            double interval = float.IsFinite(_profile.DebugLogIntervalSeconds)
                ? Math.Clamp(_profile.DebugLogIntervalSeconds, 0.1, 5.0) : 0.5;
            bool vehicleChanged = !ReferenceEquals(vehicle, _lastVehicle);
            bool stateChanged = _sampleNumber == 0 || guidance.State != _lastState || guidance.Landing.State != _lastLandingState;
            bool flightChanged = guidance.FlightNumber != _lastFlightNumber;
            bool stageChanged = _staging.StageEventNumber != _lastStageEventNumber;
            if (!force && _wasEnabled && !vehicleChanged && !stateChanged && !flightChanged && !stageChanged
                && tick - _lastSampleTick < interval * 1000.0) return;

            if (!force)
            {
                reason = !_wasEnabled ? "logging_enabled" : vehicleChanged ? "vehicle_changed"
                    : flightChanged ? "flight_changed" : stateChanged ? "state_changed"
                    : stageChanged ? "stage_activated" : "periodic";
            }
            _wasEnabled = true;
            double wallInterval = _sampleNumber == 0 ? double.NaN : (tick - _lastSampleTick) / 1000.0;
            _lastSampleTick = tick;
            _sampleNumber++;

            string configuration = Diag.F($"target_ap_m={_profile.TargetApoapsisM:F3} target_pe_m={_profile.TargetPeriapsisM:F3} gt_start_m={_profile.GravityTurnStartAlt:F3} gt_end_m={_profile.GravityTurnEndAlt:F3} gt_min_speed_m_s={_profile.GravityTurnMinSpeedMps:F3} heading_deg={_profile.LaunchHeadingDeg:F3} gt_shape={_profile.GravityTurnShape:F3} max_pitch_rate_deg_s={_profile.MaxPitchRateDegS:F3} attitude_controller=native_custom_target auto_staging={_profile.AutoStaging} hot_staging={_profile.HotStaging} hot_stage_fuel_pct={_profile.HotStagingFuelPercent:F3} auto_warp={_profile.AutoTimeskipToApoapsis} debug_logging={_profile.DebugLoggingEnabled} sample_interval_wall_s={interval:F3} csv={_profile.WriteTelemetryCsv}");
            if (configuration != _lastConfiguration)
            {
                Diag.Info("CONFIG", "flight=" + guidance.FlightNumber + " " + configuration);
                _lastConfiguration = configuration;
            }

            var record = new TelemetryRecord();
            double simTime = double.NaN, warpRate = double.NaN;
            Int128 simulationNanoseconds = 0;
            bool autoWarp = false;
            bool timeValid = Read("TIME", () =>
            {
                simulationNanoseconds = Universe.GetElapsedTime().Nanoseconds;
                simTime = (double)simulationNanoseconds / (double)UniverseTime.NANOSECONDS_PER_SECOND;
                warpRate = Universe.SimulationSpeed;
                autoWarp = Universe.IsAutoWarpActive;
            });
            if (flightChanged)
            {
                _flightSimulationStart = simulationNanoseconds;
                _flightSimulationStartValid = guidance.FlightNumber > 0 && timeValid;
            }

            record.Section("TELEMETRY");
            record.Add("utc", DateTime.UtcNow.ToString("O"));
            record.Add("session_id", Diag.SessionId);
            record.Add("flight", (long)guidance.FlightNumber);
            record.Add("sample", _sampleNumber);
            record.Add("reason", reason);
            record.Add("vehicle", vehicle.Id);
            record.Add("state", guidance.State.ToString());
            record.Add("status", _profile.StatusMessage);
            record.Add("player_t_s", guidance.PlayerTime);
            record.Add("dt_player_s", dtPlayer, "F6");
            record.Add("flight_elapsed_player_s", guidance.FlightElapsed);
            record.Add("state_elapsed_player_s", guidance.StateElapsed);
            record.Add("sample_wall_dt_s", wallInterval);
            record.Add("simulation_t_s", simTime);
            record.Add("flight_elapsed_simulation_s", timeValid && _flightSimulationStartValid
                ? (double)(simulationNanoseconds - _flightSimulationStart) / (double)UniverseTime.NANOSECONDS_PER_SECOND : double.NaN);
            record.Add("time_valid", timeValid);
            record.Add("warp_rate", warpRate);
            record.Add("auto_warp_active", autoWarp);

            double3 position = MissingVector, positionFixed = MissingVector, positionEcl = MissingVector;
            double baroAltitude = double.NaN, radarAltitude = double.NaN;
            double3 lla = MissingVector;
            IParentBody parent = null;
            bool positionValid = Read("POSITION", () =>
            {
                parent = vehicle.Parent;
                position = vehicle.GetPositionCci();
                positionEcl = vehicle.GetPositionEcl();
                if (parent != null)
                {
                    positionFixed = double3.Transform(position, parent.GetCci2Ccf());
                    lla = parent.GetLlaFromCcf(positionFixed);
                }
                baroAltitude = vehicle.GetBarometricAltitude();
                radarAltitude = vehicle.GetRadarAltitude();
            });
            double radius = Length(position);
            record.Section("POSITION");
            record.Add("position_valid", positionValid);
            record.Add("parent_body", parent is Astronomical astronomical ? astronomical.Id : parent?.GetType().Name);
            Vector(record, "pos_cci", "m", position);
            Vector(record, "pos_ccf", "m", positionFixed);
            Vector(record, "pos_ecl", "m", positionEcl);
            // IParentBody.GetLlaFromCcf returns latitude/longitude in degrees and altitude in metres.
            record.Add("latitude_deg", lla.X, "F8");
            record.Add("longitude_deg", lla.Y, "F8");
            record.Add("lla_altitude_m", lla.Z);
            record.Add("radius_from_parent_m", radius);
            record.Add("barometric_altitude_m", baroAltitude);
            record.Add("radar_altitude_m", radarAltitude);

            double3 velocity = MissingVector, surfaceVelocity = MissingVector;
            double orbitalSpeed = double.NaN, surfaceSpeed = double.NaN;
            bool velocityValid = Read("VELOCITY", () =>
            {
                velocity = vehicle.GetVelocityCci();
                surfaceVelocity = vehicle.GetSurfaceVelocityCci();
                orbitalSpeed = vehicle.OrbitalSpeed;
                surfaceSpeed = vehicle.GetSurfaceSpeed();
            });
            double radialSpeed = radius > 0.0 ? Dot(position, surfaceVelocity) / radius : double.NaN;
            double horizontalSpeed = Math.Sqrt(Math.Max(0.0, Dot(surfaceVelocity, surfaceVelocity) - radialSpeed * radialSpeed));
            double flightPathAngle = surfaceSpeed > 0.001
                ? Math.Asin(Math.Clamp(radialSpeed / surfaceSpeed, -1.0, 1.0)) * 180.0 / Math.PI : double.NaN;
            double simulationDt = timeValid && double.IsFinite(_lastSimulationTime)
                ? (double)(simulationNanoseconds - _lastSimulationNanoseconds) / (double)UniverseTime.NANOSECONDS_PER_SECOND
                : double.NaN;
            bool referenceChanged = vehicleChanged || !ReferenceEquals(parent, _lastParent);
            if (!referenceChanged && timeValid && simulationDt < 0.0)
                Diag.Warn("TIME", Diag.F($"simulation_time_reversed previous_s={_lastSimulationTime:F3} current_s={simTime:F3}; resetting acceleration estimate"));
            bool derivativeValid = !referenceChanged && _lastVelocityValid && velocityValid
                && Finite(velocity) && timeValid && double.IsFinite(simulationDt) && simulationDt > 0.0;
            double3 estimatedAcceleration = derivativeValid ? (velocity - _lastVelocity) / simulationDt : MissingVector;

            record.Section("VELOCITY");
            record.Add("velocity_valid", velocityValid);
            Vector(record, "velocity_cci", "m_s", velocity);
            Vector(record, "surface_velocity_cci", "m_s", surfaceVelocity);
            record.Add("orbital_speed_m_s", orbitalSpeed);
            record.Add("surface_speed_m_s", surfaceSpeed);
            record.Add("vertical_speed_m_s", radialSpeed);
            record.Add("horizontal_speed_m_s", horizontalSpeed);
            record.Add("flight_path_angle_deg", flightPathAngle);
            record.Add("accel_fd_valid", derivativeValid);
            record.Add("accel_fd_sim_dt_s", simulationDt);
            Vector(record, "accel_fd_cci", "m_s2", estimatedAcceleration);

            bool physicsActive = false;
            double3 acceleration = MissingVector, angularAcceleration = MissingVector, bodyRates = MissingVector;
            double density = double.NaN, pressure = double.NaN, temperature = double.NaN;
            double soundSpeed = double.NaN, dynamicPressure = double.NaN, airSpeed = double.NaN;
            bool physicsValid = Read("PHYSICS", () =>
            {
                physicsActive = vehicle.HasPhysicsBubble;
                if (!physicsActive) return;
                acceleration = vehicle.AccelerationBody;
                angularAcceleration = vehicle.AngularAccelerationBody;
                bodyRates = vehicle.GetBodyRates();
                var environment = vehicle.PhysicsEnvironment;
                density = environment.AtmosphericDensity;
                pressure = environment.AtmosphericPressure;
                temperature = environment.AtmosphericTemperature;
                soundSpeed = environment.SpeedOfSound;
                dynamicPressure = vehicle.ComputeDynamicPressure();
                var airVelocity = vehicle.GetPhysicsStates().ComputeAirVelocityBody();
                airSpeed = Math.Sqrt(airVelocity.X * (double)airVelocity.X + airVelocity.Y * (double)airVelocity.Y + airVelocity.Z * (double)airVelocity.Z);
            }) && physicsActive;
            record.Section("PHYSICS");
            record.Add("physics_active", physicsActive);
            record.Add("physics_valid", physicsValid);
            Vector(record, "acceleration_body", "m_s2", acceleration);
            record.Add("acceleration_body_norm_m_s2", Length(acceleration));
            Vector(record, "angular_acceleration_body", "rad_s2", angularAcceleration);
            Vector(record, "body_rates", "rad_s", bodyRates);
            record.Add("gravity_two_body_m_s2", parent != null && radius > 0 ? parent.Mu / (radius * radius) : double.NaN);
            record.Add("atmosphere_density_kg_m3", density, "F6");
            record.Add("atmosphere_pressure_pa", pressure);
            record.Add("atmosphere_temperature_k", temperature);
            record.Add("dynamic_pressure_pa", dynamicPressure);
            record.Add("air_speed_m_s", airSpeed);
            record.Add("speed_of_sound_m_s", soundSpeed);
            record.Add("mach", soundSpeed > 0 ? airSpeed / soundSpeed : double.NaN);

            double apoapsis = double.NaN, periapsis = double.NaN, secondsToAp = double.NaN;
            double eccentricity = double.NaN, period = double.NaN;
            bool orbitPresent = false;
            bool orbitValid = Read("ORBIT", () =>
            {
                var orbit = vehicle.Orbit;
                orbitPresent = orbit != null;
                if (!orbitPresent) return;
                apoapsis = orbit.ApoapsisAltitude;
                periapsis = orbit.PeriapsisAltitude;
                secondsToAp = (double)(vehicle.NextApoapsisTime.Nanoseconds - Universe.GetElapsedTime().Nanoseconds)
                    / (double)UniverseTime.NANOSECONDS_PER_SECOND;
                eccentricity = orbit.Eccentricity;
                period = orbit.Period;
            }) && orbitPresent;
            record.Section("ORBIT");
            record.Add("orbit_valid", orbitValid);
            record.Add("apoapsis_altitude_m", apoapsis);
            record.Add("periapsis_altitude_m", periapsis);
            record.Add("time_to_apoapsis_s", secondsToAp);
            record.Add("orbit_eccentricity", eccentricity, "F8");
            record.Add("orbit_period_s", period);
            record.Add("target_apoapsis_m", _profile.TargetApoapsisM);
            record.Add("target_periapsis_m", _profile.TargetPeriapsisM);
            record.Add("insertion_delta_v_m_s", guidance.PlannedDeltaV);
            record.Add("insertion_burn_duration_s", guidance.PlannedBurnDuration);
            record.Add("insertion_burn_lead_s", guidance.BurnLead);
            record.Add("inertial_radial_speed_m_s", guidance.RadialSpeed);
            record.Add("inertial_tangential_speed_m_s", guidance.TangentialSpeed);
            record.Add("target_tangential_speed_m_s", guidance.TargetTangentialSpeed);
            record.Add("predicted_apoapsis_altitude_m", guidance.PredictedApoapsis);

            double mass = double.NaN, inertMass = double.NaN, propellantMass = double.NaN;
            double fill = double.NaN, thrust = double.NaN, manualThrottle = double.NaN;
            bool engineActive = false, propellantAvailable = false;
            long partCount = -1, activeSequence = -1, nextSequence = -1, sequenceCount = -1;
            bool engineValid = Read("ENGINE", () =>
            {
                mass = vehicle.TotalMass;
                inertMass = vehicle.InertMass;
                propellantMass = vehicle.PropellantMass;
                thrust = vehicle.ComputeLiveThrust();
                engineActive = vehicle.IsAnyEngineActive();
                propellantAvailable = vehicle.IsAnyEnginePropellantAvailable();
                manualThrottle = vehicle.GetManualThrottle();
                if (vehicle.Parts == null) return;
                fill = vehicle.Parts.TotalFillFraction;
                partCount = vehicle.Parts.Count;
                var sequences = vehicle.Parts.SequenceList;
                if (sequences == null) return;
                activeSequence = sequences.ActiveSequence;
                nextSequence = sequences.GetNextSequenceNumber();
                sequenceCount = sequences.Count;
            });
            record.Section("ENGINE");
            record.Add("engine_data_valid", engineValid);
            record.Add("total_mass_kg", mass);
            record.Add("inert_mass_kg", inertMass);
            record.Add("propellant_mass_kg", propellantMass);
            record.Add("total_fill_fraction", fill, "F6");
            record.Add("live_thrust_n", thrust);
            record.Add("thrust_acceleration_m_s2", mass > 0 ? thrust / mass : double.NaN);
            record.Add("engine_active", engineActive);
            record.Add("engine_propellant_available", propellantAvailable);
            record.Add("manual_throttle_observed", manualThrottle, "F6");
            record.Add("part_count", partCount);
            record.Add("active_sequence", activeSequence);
            record.Add("next_sequence", nextSequence);
            record.Add("sequence_count", sequenceCount);
            record.Add("staging_evaluated", guidance.StagingEvaluated);
            record.Add("staging_status", guidance.StagingEvaluated ? _staging.DebugStatus : "not_evaluated_in_this_state");
            record.Add("staging_confirmation_s", guidance.StagingEvaluated ? _staging.ConfirmationElapsedSeconds : double.NaN);
            record.Add("staging_cooldown_remaining_s", guidance.StagingEvaluated ? _staging.CooldownRemainingSeconds : double.NaN);
            record.Add("stage_event", (long)_staging.StageEventNumber);
            record.Add("booster_candidates", _staging.BoosterCandidateCount);
            record.Add("booster_pending_separations", _staging.BoosterPendingCount);
            record.Add("booster_confirmed_separations", _staging.BoosterDropCount);
            record.Add("booster_status", _staging.BoosterStatus);
            record.Add("game_locale", L10n.Locale);

            NavBallData nav = default;
            doubleQuat body2Cci = new doubleQuat(double.NaN, double.NaN, double.NaN, double.NaN);
            bool attitudeValid = Read("ATTITUDE", () => { nav = vehicle.NavBallData; body2Cci = vehicle.GetBody2Cci(); });
            record.Section("ATTITUDE");
            record.Add("attitude_valid", attitudeValid);
            record.Add("nav_frame", attitudeValid ? nav.Frame.ToString() : null);
            record.Add("nav_angle_x_deg", attitudeValid ? nav.AttitudeAngles.X : double.NaN);
            record.Add("nav_angle_y_deg", attitudeValid ? nav.AttitudeAngles.Y : double.NaN);
            record.Add("nav_angle_z_deg", attitudeValid ? nav.AttitudeAngles.Z : double.NaN);
            double3 actualForward = attitudeValid ? FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)) : MissingVector;
            record.Add("actual_pitch_deg", FlightAttitude.Pitch(position, actualForward));
            record.Add("actual_heading_deg", FlightAttitude.Heading(position, actualForward));
            Vector(record, "actual_forward_cci", "unit", actualForward);
            Vector(record, "nav_attitude_rates", "rad_s", attitudeValid ? nav.AttitudeRates : MissingVector);
            record.Add("nav_thrust_weight_ratio", attitudeValid ? nav.ThrustWeightRatio : double.NaN);
            record.Add("nav_delta_v_m_s", attitudeValid ? nav.DeltaV : double.NaN);
            record.Add("body_to_cci_quat_x", body2Cci.X, "F8");
            record.Add("body_to_cci_quat_y", body2Cci.Y, "F8");
            record.Add("body_to_cci_quat_z", body2Cci.Z, "F8");
            record.Add("body_to_cci_quat_w", body2Cci.W, "F8");

            var fc = vehicle.FlightComputer;
            string observedFlags = null;
            Read("THRUSTER_FLAGS", () => observedFlags = vehicle.GetThrusterFlags().ToString());
            record.Section("CONTROL");
            bool ascentActive = guidance.State != AscentGuidance.AscentState.Idle
                && guidance.State != AscentGuidance.AscentState.Aborted && guidance.State != AscentGuidance.AscentState.OrbitAchieved;
            record.Add("autopilot_controlling", ascentActive || guidance.Landing.Active);
            record.Add("flight_mode", guidance.Landing.Active ? "landing" : ascentActive ? "ascent" : "inactive");
            record.Add("commanded_throttle", guidance.Landing.Active ? guidance.Landing.CommandedThrottle : guidance.CommandedThrottle, "F6");
            record.Add("requested_average_throttle", guidance.Landing.Active ? guidance.Landing.RequestedThrottle : guidance.RequestedThrottle, "F6");
            record.Add("engine_min_throttle", guidance.EngineMinimumThrottle);
            record.Add("fc_reported_min_throttle", fc != null ? fc.ActiveEnginePerformanceMax.MinThrottle : double.NaN);
            record.Add("engine_minimum_pulse_s", guidance.EngineMinimumPulseSeconds);
            record.Add("command_minimum_pulse_s", guidance.CommandMinimumPulseSeconds);
            record.Add("minimum_throttle_pulse_safe", guidance.MinimumPulseSafe);
            record.Add("throttle_reason", guidance.Landing.Active ? guidance.Landing.Reason : guidance.ThrottleReason);
            record.Add("guidance_hook", "after_auto_warp_before_job_sim_step");
            record.Add("commanded_thruster_flags", FormatFlags(guidance.CommandedThrusterFlags));
            record.Add("observed_thruster_flags", observedFlags);
            // OR over every frame catches brief RCS pulses between telemetry samples.
            record.Add("commanded_flags_seen_since_sample", FormatFlags(_flagsSeen));
            record.Add("gravity_turn_controller_active", guidance.State == AscentGuidance.AscentState.GravityTurn);
            record.Add("profile_pitch_deg", guidance.ProfilePitch);
            record.Add("commanded_pitch_deg", guidance.Landing.Active ? guidance.Landing.TargetPitch : ascentActive ? _profile.DesiredPitchDeg : double.NaN);
            record.Add("controller_pitch_deg", guidance.ControllerPitch);
            record.Add("target_heading_deg", _profile.LaunchHeadingDeg);
            record.Add("pitch_error_deg", guidance.PitchError);
            record.Add("heading_error_deg", guidance.HeadingError);
            record.Add("measured_ctrl_pitch_rate_deg_s", guidance.PitchRate);
            record.Add("measured_ctrl_yaw_rate_deg_s", guidance.YawRate);
            record.Add("pointing_error_deg", guidance.PointingError);
            record.Add("insertion_mode", guidance.InsertionMode);
            record.Add("insertion_target_pitch_deg", guidance.InsertionPitch);
            record.Add("actual_orbital_thrust_pitch_deg", guidance.OrbitalThrustPitch);
            record.Add("insertion_radial_acceleration_demand_m_s2", guidance.RadialAccelerationDemand);
            record.Add("insertion_tangential_acceleration_demand_m_s2", guidance.TangentialAccelerationDemand);
            record.Add("insertion_transverse_rate_deg_s", guidance.TransverseRate);
            record.Add("insertion_tvc_settling", guidance.TvcSettling);
            record.Add("insertion_attitude_reason", guidance.InsertionAttitudeReason);
            record.Add("turn_flight_path_pitch_deg", guidance.FlightPathPitch);
            record.Add("angle_of_attack_deg", guidance.AngleOfAttack);
            record.Add("turn_rate_limit_deg_s", guidance.RateLimit);
            record.Add("turn_hold_reason", guidance.TurnHoldReason);
            record.Add("angle_of_attack_limited", guidance.AngleOfAttackLimited);
            record.Add("guidance_simulation_dt_s", guidance.SimulationDelta);
            record.Add("pitch_lag_limited", guidance.PitchLagLimited);
            record.Add("flight_computer_present", fc != null);
            record.Add("fc_attitude_mode", fc?.AttitudeMode.ToString());
            record.Add("fc_attitude_frame", fc?.AttitudeFrame.ToString());
            record.Add("fc_track_target", fc?.AttitudeTrackTarget.ToString());
            record.Add("fc_manual_thrust_mode", fc?.ManualThrustMode.ToString());
            record.Add("fc_burn_mode", fc?.BurnMode.ToString());
            record.Add("fc_rcs_mode", fc?.RCSMode.ToString());
            record.Add("fc_rate_limit_deg_s", fc != null ? fc.RateLimit * FlightAttitude.RAD_TO_DEG : double.NaN);
            Vector(record, "fc_custom_target", "rad", fc != null ? fc.CustomAttitudeTarget : MissingVector);
            record.Add("fc_roll_mode", fc?.RollMode.ToString());
            record.Add("fc_active_control_frame", fc?.ActiveControlFrame.ToString());
            record.Add("fc_control_system_pitch", fc != null ? fc.ActiveControlSystem.Y.ToString() : null);
            record.Add("fc_control_system_yaw", fc != null ? fc.ActiveControlSystem.Z.ToString() : null);
            // The game divides TVC torque by inertia; this is angular acceleration authority.
            Vector(record, "fc_tvc_angular_authority", "rad_s2", fc != null && physicsValid ? ToDouble(fc.TvcTorqueAuthority) : MissingVector);
            Vector(record, "fc_tvc_max_angular_authority", "rad_s2", fc != null && physicsValid ? ToDouble(fc.TvcTorqueAuthorityMax) : MissingVector);
            Vector(record, "fc_rcs_angular_authority", "rad_s2", fc != null && physicsValid ? ToDouble(fc.RcsTorqueAuthority) : MissingVector);
            record.Add("fc_planned_throttle", fc != null ? fc.PlannedBurnThrottle : double.NaN);
            record.Add("fc_measured_acceleration_m_s2", fc != null && physicsValid ? fc.TotalAcceleration : double.NaN);
            record.Add("fc_max_active_engine_thrust_n", fc != null ? fc.ActiveEnginePerformanceMax.Thrust : double.NaN);
            record.Add("fc_max_active_engine_mass_flow_kg_s", fc != null ? fc.ActiveEnginePerformanceMax.MassFlowRate : double.NaN);
            record.Add("fc_g_load_throttle_limited", fc != null && fc.IsThrottleGLoadLimited);
            Vector(record, "fc_error_angles", "rad", fc != null && physicsValid ? ToDouble(fc.ErrorAngles) : MissingVector);
            Vector(record, "fc_error_rates", "rad_s", fc != null && physicsValid ? ToDouble(fc.ErrorRates) : MissingVector);

            guidance.Landing.Append(record);
            string sampleTag = "flight=" + guidance.FlightNumber + " sample=" + _sampleNumber + " ";
            foreach (var section in record.Sections) Diag.Debug(section.Key, sampleTag + section.Value);
            if (_profile.WriteTelemetryCsv) Diag.Csv(record);

            _lastState = guidance.State;
            _lastLandingState = guidance.Landing.State;
            _lastFlightNumber = guidance.FlightNumber;
            _lastStageEventNumber = _staging.StageEventNumber;
            _lastVehicle = vehicle;
            _lastParent = parent;
            _lastSimulationTime = simTime;
            _lastSimulationNanoseconds = simulationNanoseconds;
            _lastVelocity = velocity;
            _lastVelocityValid = positionValid && velocityValid && Finite(velocity) && timeValid;
            _flagsSeen = 0;
        }

        private static bool Read(string section, Action read)
        {
            try { read(); return true; }
            catch (Exception ex)
            {
                Diag.EveryError("telemetry_" + section, 5.0, "TELEMETRY_READ", ex, "section=" + section);
                return false;
            }
        }

        private static string FormatFlags(ulong flags) => ((ThrusterMapFlags)flags).ToString();
        private static double3 ToDouble(float3 value) => new double3(value.X, value.Y, value.Z);
        private static double Dot(double3 a, double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static double Length(double3 value) => Math.Sqrt(Dot(value, value));
        private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

        private static void Vector(TelemetryRecord record, string name, string unit, double3 value)
        {
            record.Add(name + "_x_" + unit, value.X);
            record.Add(name + "_y_" + unit, value.Y);
            record.Add(name + "_z_" + unit, value.Z);
        }
    }
}
