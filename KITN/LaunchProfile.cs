using System;

namespace KITN
{
    public class LaunchProfile
    {
        public float TargetPeriapsisKm = 200.0f;
        public float TargetApoapsisKm = 200.0f;

        public float GravityTurnStartM = 2500.0f;
        public float GravityTurnEndM = 65000.0f;
        public float GravityTurnMinSpeedMps = 125.0f;
        public float LaunchHeadingDeg = 90.0f;
        public float GravityTurnShape = 1.1f;
        public float MaxPitchRateDegS = 1.0f;

        public bool AutoStaging = true;
        public bool HotStaging = false;
        public float HotStagingFuelPercent = 5.0f;

        public bool AutoTimeskipToApoapsis = true;
        public int LaunchCountdownSeconds = 10;

        public bool DebugLoggingEnabled = true;
        public float DebugLogIntervalSeconds = 0.5f;
        public bool WriteTelemetryCsv = true;

        public double TargetPeriapsisM => TargetPeriapsisKm * 1000.0;
        public double TargetApoapsisM => TargetApoapsisKm * 1000.0;
        public double GravityTurnStartAlt => GravityTurnStartM;
        public double GravityTurnEndAlt => GravityTurnEndM;

        public double CurrentAltitudeKm = 0.0;
        public double CurrentApoapsisKm = 0.0;
        public double CurrentPeriapsisKm = 0.0;
        public double CurrentOrbitalSpeed = 0.0;
        public float CurrentPropellantFraction = 1.0f;
        public double CurrentPitchDeg = 90.0;
        public double DesiredPitchDeg = 90.0;
        private LocalizedMessage _status = new LocalizedMessage("status.ready", Array.Empty<object>());
        public string StatusMessage
        {
            get => _status.ToString();
            set => _status = LocalizedMessage.Literal(value);
        }
        public void SetStatus(string key, params object[] values) => _status = new LocalizedMessage(key, values);
    }
}
