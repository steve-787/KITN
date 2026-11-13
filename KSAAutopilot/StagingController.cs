using System;
using KSA;

namespace KSAAutopilot
{
    /// <summary>
    /// Automatic staging controller with Hot-Staging support for Kitten Space Agency.
    /// Uses KSA SequenceList to trigger stages.
    /// </summary>
    public class StagingController
    {
        private readonly LaunchProfile _profile;
        private readonly BoosterMonitor _boosters;
        private double _lastStageTime = -100.0;
        private double _dryingSince = double.NaN;
        private const double STAGE_COOLDOWN_SECONDS = 1.0;
        private const double HOT_STAGE_COOLDOWN_SECONDS = 15.0;
        private const double CONFIRM_SECONDS = 0.5;   // condition must hold this long before staging
        private const float MIN_THROTTLE_FOR_STAGING = 0.05f;
        private bool _lastWasHot = false;

        public string DebugStatus { get; private set; } = "not_evaluated";
        public double ConfirmationElapsedSeconds { get; private set; }
        public double CooldownRemainingSeconds { get; private set; }
        public int StageEventNumber { get; private set; }
        public int BoosterCandidateCount => _boosters.CandidateCount;
        public int BoosterPendingCount => _boosters.PendingCount;
        public int BoosterDropCount => _boosters.ConfirmedDropCount;
        public string BoosterStatus => _boosters.Status;

        public StagingController(LaunchProfile profile)
        {
            _profile = profile;
            _boosters = new BoosterMonitor(profile);
        }

        public void Reset()
        {
            _lastStageTime = -100.0;
            _dryingSince = double.NaN;
            _lastWasHot = false;
            _boosters.Reset();
            DebugStatus = "reset";
            ConfirmationElapsedSeconds = 0;
            CooldownRemainingSeconds = 0;
        }

        /// <summary>
        /// Evaluates vehicle staging status and triggers the next stage if necessary.
        /// commandedThrottle is what the autopilot currently asks for: while it is ~0 (coast)
        /// engines are off on purpose, so the main stage must be preserved.
        /// Empty, previously ignited side boosters may still be discarded.
        /// </summary>
        public void Update(Vehicle vehicle, double currentTime, float commandedThrottle)
        {
            ConfirmationElapsedSeconds = 0;
            CooldownRemainingSeconds = 0;
            if (!_profile.AutoStaging || vehicle == null)
            {
                _boosters.Suspend();
                DebugStatus = "auto_staging_disabled_or_no_vehicle";
                return;
            }
            if (vehicle.Parts?.SequenceList == null) { DebugStatus = "no_sequence_list"; return; }

            // Local exhaustion is independent of the core engine and an intentional coast cutoff.
            // Separations still use the same synchronized hook and KSA's native input queue.
            double simulationTime = (double)Universe.GetElapsedTime().Nanoseconds / (double)UniverseTime.NANOSECONDS_PER_SECOND;
            int beforeRequests = _boosters.DropRequestCount;
            bool separating;
            try { separating = _boosters.Update(vehicle, simulationTime); }
            catch (Exception ex)
            {
                _boosters.Suspend();
                _dryingSince = double.NaN;
                DebugStatus = "booster_monitor_failed";
                Diag.EveryError("booster_monitor", 5, "BOOSTER", ex, "Booster monitoring failed; staging withheld for this step");
                return;
            }
            if (_boosters.DropRequestCount > beforeRequests)
            {
                StageEventNumber += _boosters.DropRequestCount - beforeRequests;
                _lastStageTime = currentTime;
                _lastWasHot = false;
            }
            if (separating || _boosters.NextSequenceOnlyBoosters)
            {
                _dryingSince = double.NaN;
                DebugStatus = _boosters.Status;
                ConfirmationElapsedSeconds = _boosters.ConfirmationElapsed;
                return;
            }

            double cooldown = _lastWasHot ? HOT_STAGE_COOLDOWN_SECONDS : STAGE_COOLDOWN_SECONDS;
            if (currentTime - _lastStageTime < cooldown)
            {
                DebugStatus = _lastWasHot ? "hot_stage_cooldown" : "stage_cooldown";
                CooldownRemainingSeconds = cooldown - (currentTime - _lastStageTime);
                return;
            }

            if (commandedThrottle < MIN_THROTTLE_FOR_STAGING)
            {
                _dryingSince = double.NaN;
                DebugStatus = "throttle_below_0.05_intentional_engine_cutoff";
                return;
            }

            if (vehicle.Parts.SequenceList.GetNextSequenceNumber() < 0)
            {
                _dryingSince = double.NaN;
                DebugStatus = "no_remaining_sequence";
                return;
            }

            if (_profile.HotStaging && ShouldHotStage(vehicle))
            {
                DebugStatus = "hot_stage_fuel_threshold_reached";
                FireStage(vehicle, currentTime, isHotStage: true, DebugStatus);
                return;
            }

            if (!ShouldNormalStage(vehicle))
            {
                _dryingSince = double.NaN;
                return;
            }

            if (double.IsNaN(_dryingSince)) _dryingSince = currentTime;
            ConfirmationElapsedSeconds = currentTime - _dryingSince;
            if (currentTime - _dryingSince >= CONFIRM_SECONDS)
            {
                _dryingSince = double.NaN;
                FireStage(vehicle, currentTime, isHotStage: false, DebugStatus);
            }
        }

        private bool ShouldHotStage(Vehicle vehicle)
        {
            // Only hot-stage while engines are burning
            if (!vehicle.IsAnyEngineActive()) return false;

            float fillPercent = vehicle.Parts.TotalFillFraction * 100.0f;
            _profile.CurrentPropellantFraction = vehicle.Parts.TotalFillFraction;

            return fillPercent <= _profile.HotStagingFuelPercent;
        }

        private bool ShouldNormalStage(Vehicle vehicle)
        {
            bool engineActive = vehicle.IsAnyEngineActive();
            bool propellantAvailable = vehicle.IsAnyEnginePropellantAvailable();
            bool enginesFlameout = !engineActive || !propellantAvailable;
            bool zeroThrust = vehicle.ComputeLiveThrust() <= 0.001;

            DebugStatus = !engineActive ? "confirming_no_active_engine"
                : !propellantAvailable ? "confirming_no_engine_propellant"
                : zeroThrust ? "confirming_zero_live_thrust" : "engines_burning";

            return enginesFlameout || zeroThrust;
        }

        private void FireStage(Vehicle vehicle, double currentTime, bool isHotStage, string reason)
        {
            try
            {
                Diag.Info("STAGING", Diag.F($"event=activation_requested type={(isHotStage ? "hot" : "normal")} reason={reason} player_t_s={currentTime:F3} confirmed_s={ConfirmationElapsedSeconds:F3} hot_stage_fuel_threshold_pct={_profile.HotStagingFuelPercent:F3} ") + StageDetails(vehicle));
                vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);
                _lastStageTime = currentTime;
                _lastWasHot = isHotStage;
                StageEventNumber++;
                DebugStatus = isHotStage ? "hot_stage_activation_returned" : "normal_stage_activation_returned";

                _profile.SetStatus(isHotStage ? "status.hot_stage" : "status.stage", currentTime);
                Diag.Info("STAGING", Diag.F($"event=activation_returned type={(isHotStage ? "hot" : "normal")} player_t_s={currentTime:F3} stage_event={StageEventNumber} ") + StageDetails(vehicle));
            }
            catch (Exception ex)
            {
                DebugStatus = "stage_activation_failed";
                Diag.Error("STAGING", ex, "Activating next sequence failed; reason=" + reason);
            }
        }

        private static string StageDetails(Vehicle vehicle)
        {
            try
            {
                var sequences = vehicle.Parts?.SequenceList;
                return Diag.F($"vehicle=\"{vehicle.Id}\" active_sequence={sequences?.ActiveSequence} next_sequence={sequences?.GetNextSequenceNumber()} sequence_count={sequences?.Count} engine_active={vehicle.IsAnyEngineActive()} propellant_available={vehicle.IsAnyEnginePropellantAvailable()} live_thrust_n={vehicle.ComputeLiveThrust():F3} total_fill_fraction={vehicle.Parts?.TotalFillFraction:F6} total_mass_kg={vehicle.TotalMass:F3}");
            }
            catch (Exception ex)
            {
                Diag.EveryError("staging_details", 5.0, "STAGING", ex, "Reading staging diagnostics failed");
                return "staging_details=unavailable";
            }
        }
    }
}
