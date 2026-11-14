using System;
using System.Text.Json.Serialization;

namespace KITN
{
    internal sealed class LandingSettings
    {
        public bool AutoWarp { get; set; } = true;
        public bool AdaptiveDrag { get; set; } = true;
        public float TouchdownSpeedMps { get; set; } = 0.4f;
        public float BrakingMargin { get; set; } = 1.5f;
        public float HandoverHeightM { get; set; } = 5000;
        public float TerminalHorizonSeconds { get; set; } = 12;
        public float MaximumTiltDeg { get; set; } = 85;
        public float MaximumTurnRateDegS { get; set; } = 8;
        public float ReservePercent { get; set; } = 35;
        public float ReserveExtraMps { get; set; } = 250;
        public float DragAreaScale { get; set; } = 1;
        public float SearchWindowHours { get; set; } = 24;

        [JsonIgnore] public bool IsValid => Range(TouchdownSpeedMps, 0.2f, 1)
            && Range(BrakingMargin, 1.1f, 3) && Range(HandoverHeightM, 1000, 30000)
            && Range(TerminalHorizonSeconds, 10, 30) && Range(MaximumTiltDeg, 10, 85)
            && Range(MaximumTurnRateDegS, 2, 15) && Range(ReservePercent, 10, 100)
            && Range(ReserveExtraMps, 0, 2000) && Range(DragAreaScale, 0.25f, 4)
            && Range(SearchWindowHours, 1, 168);
        private static bool Range(float value, float low, float high) => float.IsFinite(value) && value >= low && value <= high;
        public LandingSettings Copy() => (LandingSettings)MemberwiseClone();
        public bool Matches(LandingSettings other) => other != null && AutoWarp == other.AutoWarp && AdaptiveDrag == other.AdaptiveDrag
            && TouchdownSpeedMps == other.TouchdownSpeedMps && BrakingMargin == other.BrakingMargin
            && HandoverHeightM == other.HandoverHeightM && TerminalHorizonSeconds == other.TerminalHorizonSeconds
            && MaximumTiltDeg == other.MaximumTiltDeg && MaximumTurnRateDegS == other.MaximumTurnRateDegS
            && ReservePercent == other.ReservePercent && ReserveExtraMps == other.ReserveExtraMps
            && DragAreaScale == other.DragAreaScale && SearchWindowHours == other.SearchWindowHours;
    }
}
