using System;
using System.Threading;
using System.Threading.Tasks;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Reads flight display data only at the synchronized physics hook.</summary>
    internal sealed class FlightDisplay
    {
        public readonly FlightTrack Track = new FlightTrack();
        public RoutePrediction Prediction { get; private set; } = new RoutePrediction();
        public AdaptiveFlightPlan LaunchPlan { get; private set; }
        public bool IsPlanning => _planning != null;
        public bool CanLaunch(LaunchProfile profile) => !IsPlanning && LaunchPlan?.Success == true
            && LaunchPlan.RequestedSettings.Matches(profile);
        public string PlanningMessage(LaunchProfile profile) => IsPlanning || LaunchPlan?.RequestedSettings?.Matches(profile) != true
            ? L10n.Get("planning.pending") : LaunchPlan.Message;
        public bool HasVehicle { get; private set; }
        private string _vehicleName;
        public string VehicleName { get => HasVehicle ? _vehicleName : L10n.Get("vehicle.none"); private set => _vehicleName = value; }
        public string BodyName { get; private set; } = "—";
        public double VerticalSpeed { get; private set; }
        public double AltitudeMeters { get; private set; } = double.NaN;
        public double DynamicPressureKpa { get; private set; }
        public double AccelerationG { get; private set; }
        public double Throttle { get; private set; }
        public int ActiveStage { get; private set; }
        private string _warningKey;
        public string Warning => _warningKey == null ? null : L10n.Get(_warningKey);

        private Vehicle _vehicle;
        private IParentBody _parent;
        private string _previewKey;
        private long _lastPreviewTick;
        private bool _planLocked;
        private Task<AdaptiveFlightPlan> _planning;
        private CancellationTokenSource _planningCancellation;

        public void Update(Vehicle vehicle, LaunchProfile profile, AscentGuidance guidance, string action)
        {
            try
            {
                UpdateCore(vehicle, profile, guidance, action);
            }
            catch (Exception ex)
            {
                // UI and preview errors must never abort the proven flight controller.
                _warningKey = "display.unavailable";
                Diag.EveryError("flight_display", 5, "FLIGHT_DISPLAY", ex, "Reading route/physics display failed");
            }
        }

        private void UpdateCore(Vehicle vehicle, LaunchProfile profile, AscentGuidance guidance, string action)
        {
            HasVehicle = vehicle != null;
            AltitudeMeters = double.NaN;
            if (!ReferenceEquals(vehicle, _vehicle))
            {
                Track.Clear();
                CancelPlanning();
                LaunchPlan = null;
                Prediction = new RoutePrediction();
                _previewKey = null;
                _planLocked = false;
                _vehicle = vehicle;
                _parent = vehicle?.Parent;
                _warningKey = null;
            }
            if (vehicle == null) return;
            VehicleName = vehicle.Id;
            IParentBody parent = vehicle.Parent;
            if (parent == null) return;
            if (!ReferenceEquals(parent, _parent))
            {
                Track.Stop();
                _parent = parent;
                _previewKey = null;
                _warningKey = "display.body_changed";
            }
            BodyName = parent is Astronomical body ? body.Id : parent.GetType().Name;
            AltitudeMeters = vehicle.GetBarometricAltitude();
            double3 position = vehicle.GetPositionCci();
            double length = Length(position);
            double3 surfaceVelocity = vehicle.GetSurfaceVelocityCci();
            VerticalSpeed = length > 0 ? Dot(position, surfaceVelocity) / length : 0;
            Throttle = vehicle.GetManualThrottle();
            ActiveStage = vehicle.Parts?.SequenceList.ActiveSequence ?? 0;
            DynamicPressureKpa = vehicle.HasPhysicsBubble ? vehicle.ComputeDynamicPressure() / 1000 : 0;
            AccelerationG = vehicle.HasPhysicsBubble ? Length(vehicle.AccelerationBody) / 9.80665 : 0;
            double time = (double)Universe.GetElapsedTime().Nanoseconds / (double)UniverseTime.NANOSECONDS_PER_SECOND;
            double bodyRadius = parent.GetNearSurfaceRadius();
            double3 fixedPosition = double3.Transform(position, parent.GetCci2Ccf());
            if (action == "reset")
            {
                Track.Clear();
                _planLocked = false;
                _previewKey = null;
                _warningKey = null;
            }
            if (action == "start")
            {
                if (LaunchPlan?.Success == true) Prediction = LaunchPlan.Route;
                _planLocked = true;
                Track.Begin(time, fixedPosition.X, fixedPosition.Y, fixedPosition.Z,
                    profile.CurrentAltitudeKm * 1000, bodyRadius);
                _warningKey = null;
                Diag.Info("FLIGHT_DISPLAY", "event=route_started coordinates=body_fixed distance=surface_arc preview_frozen=true");
            }
            if (action == "preset_loaded" && !guidance.IsActive)
            {
                RefreshLaunchPlan(vehicle, profile, true);
                _planLocked = false;
            }
            bool finished = !guidance.IsActive;
            Track.Sample(time, fixedPosition.X, fixedPosition.Y, fixedPosition.Z,
                profile.CurrentAltitudeKm * 1000, bodyRadius, finished && Track.IsRecording);
            if (finished) Track.Stop();
            if (!_planLocked && guidance.CurrentState == AscentGuidance.AscentState.Idle)
                RefreshLaunchPlan(vehicle, profile);
        }

        public void CancelPlanning()
        {
            _planningCancellation?.Cancel();
            _planningCancellation?.Dispose();
            _planningCancellation = null; _planning = null;
        }

        public void RefreshLaunchPlan(Vehicle vehicle, LaunchProfile profile, bool force = false)
        {
            if (vehicle == null) return;
            if (_planning?.IsCompleted == true)
            {
                if (!_planning.IsCanceled)
                {
                    LaunchPlan = _planning.GetAwaiter().GetResult();
                    Prediction = LaunchPlan.Success ? LaunchPlan.Route : new RoutePrediction { MessageKey = "planning.no_route" };
                    if (LaunchPlan.Success)
                        Diag.Info("FLIGHT_PLAN", Diag.F($"event=ready candidates={LaunchPlan.Candidates} twr={LaunchPlan.LiftoffTwr:F3} available_dv_m_s={LaunchPlan.AvailableDeltaV:F3} minimum_dv_m_s={LaunchPlan.MinimumDeltaV:F3} remaining_dv_m_s={LaunchPlan.Route.RemainingDeltaV:F3} gt_start_m={LaunchPlan.Settings.GravityTurnStartM:F3} gt_end_m={LaunchPlan.Settings.GravityTurnEndM:F3} gt_shape={LaunchPlan.Settings.GravityTurnShape:F3} predicted_ap_km={LaunchPlan.Route.FinalApoapsisKm:F3} predicted_pe_km={LaunchPlan.Route.FinalPeriapsisKm:F3} predicted_flight_s={LaunchPlan.Route.FlightSeconds:F3}"));
                    else
                        Diag.Warn("FLIGHT_PLAN", $"event=failed reason={LaunchPlan.MessageKey} detail=\"{LaunchPlan.Message}\"");
                    if (LaunchPlan.Error != null) Diag.Error("FLIGHT_PLAN", LaunchPlan.Error, "Computing an adaptive flight plan failed");
                }
                _planning = null;
                _planningCancellation?.Dispose();
                _planningCancellation = null;
            }
            long tick = Environment.TickCount64;
            if (!force && tick - _lastPreviewTick < 750) return;
            _lastPreviewTick = tick;
            string key = Diag.F($"{vehicle.Id}|{vehicle.Parent?.Mu}|{Math.Round(vehicle.TotalMass)}|{vehicle.Parts?.Parts.Length}|{vehicle.Parts?.SequenceList.ActiveSequence}|{Math.Round(vehicle.GetBarometricAltitude() / 10)}|{profile.TargetApoapsisKm}|{profile.TargetPeriapsisKm}|{profile.GravityTurnStartM}|{profile.GravityTurnEndM}|{profile.GravityTurnMinSpeedMps}|{profile.GravityTurnShape}|{profile.MaxPitchRateDegS}|{profile.LaunchHeadingDeg}|{profile.AutoStaging}|{profile.HotStaging}|{profile.HotStagingFuelPercent}|{profile.LaunchCountdownSeconds}|{profile.AutoTimeskipToApoapsis}");
            try
            {
                // A rebuild can keep the same mass and part count. Read an isolated snapshot
                // every 750 ms, then reuse the plan only if its actual performance matches.
                RocketFlightModel model = RocketSnapshotReader.Read(vehicle, profile, key);
                string performanceKey = key + Diag.F($"|{model.ErrorKey}|{model.HasFlightComputer}|{model.HasSteering}|{model.MinimumThrottle:F4}|{model.AtmosphericThrustRatio:F3}|{model.AttitudeAccelerationDegS2:F2}");
                foreach (RouteBurnPhase p in model.Phases)
                    performanceKey += Diag.F($"|{p.Thrust:F0},{p.MassFlow:F3},{p.StartMass:F1},{p.FullThrottleSeconds:F3},{p.StartsStage}");
                if (performanceKey == _previewKey) return;
                CancelPlanning(); _previewKey = model.Fingerprint = performanceKey; LaunchPlan = null;
                Prediction = new RoutePrediction { MessageKey = "planning.pending" };
                FlightPlanSettings settings = FlightPlanSettings.Capture(profile);
                Diag.Info("FLIGHT_PLAN", Diag.F($"event=planning vehicle=\"{vehicle.Id}\" mass_kg={vehicle.TotalMass:F3} phases={model.Phases.Length} atmosphere_thrust_ratio={model.AtmosphericThrustRatio:F3} attitude_acceleration_deg_s2={model.AttitudeAccelerationDegS2:F3} has_computer={model.HasFlightComputer} has_steering={model.HasSteering}"));
                for (int i = 0; i < model.Phases.Length; i++)
                {
                    RouteBurnPhase p = model.Phases[i];
                    Diag.Info("FLIGHT_PLAN_PHASE", Diag.F($"phase={i} starts_stage={p.StartsStage} thrust_n={p.Thrust:F3} mass_flow_kg_s={p.MassFlow:F3} start_mass_kg={p.StartMass:F3} burn_seconds={p.FullThrottleSeconds:F3}"));
                }
                _planningCancellation = new CancellationTokenSource();
                CancellationToken cancellation = _planningCancellation.Token;
                _planning = Task.Run(() =>
                {
                    try { return AdaptiveFlightPlanner.Calculate(settings, model, cancellation); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return new AdaptiveFlightPlan { RequestedSettings = settings, MessageKey = "planning.calculation_failed", Error = ex }; }
                }, cancellation);
            }
            catch (Exception ex)
            {
                CancelPlanning();
                _previewKey = null;
                LaunchPlan = new AdaptiveFlightPlan { RequestedSettings = FlightPlanSettings.Capture(profile), MessageKey = "planning.calculation_failed", Error = ex };
                Prediction = new RoutePrediction { MessageKey = "planning.calculation_failed" };
                Diag.EveryError("rocket_snapshot", 5, "FLIGHT_PLAN", ex, "Reading rocket performance for the flight plan failed");
            }
        }

        private static double Dot(double3 a, double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static double Length(double3 v) => Math.Sqrt(Dot(v, v));
    }
}
