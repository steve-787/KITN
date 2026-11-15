using System;
using System.Text.Json.Serialization;

namespace KITN
{
    internal sealed class HohmannSettings
    {
        public float DepartureLeadSeconds { get; set; } = 300;
        public int SynodicWindows { get; set; } = 1;
        public float DurationTolerancePercent { get; set; } = 20;
        public bool IncludeCapture { get; set; } = true;
        public bool AutoWarp { get; set; } = true;
        [JsonIgnore] public bool IsValid => float.IsFinite(DepartureLeadSeconds)
            && DepartureLeadSeconds >= 60 && DepartureLeadSeconds <= 3600
            && SynodicWindows >= 1 && SynodicWindows <= 3
            && float.IsFinite(DurationTolerancePercent) && DurationTolerancePercent >= 1 && DurationTolerancePercent <= 50;
        public HohmannSettings Copy() => (HohmannSettings)MemberwiseClone();
        public bool Matches(HohmannSettings other) => other != null
            && DepartureLeadSeconds == other.DepartureLeadSeconds && SynodicWindows == other.SynodicWindows
            && DurationTolerancePercent == other.DurationTolerancePercent
            && IncludeCapture == other.IncludeCapture && AutoWarp == other.AutoWarp;
    }
}
