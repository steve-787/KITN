using System;
using System.Text.Json.Serialization;

namespace KITN
{
    internal sealed class RendezvousSettings
    {
        public bool AutoWarp { get; set; } = true;
        public bool CourseCorrections { get; set; } = true;
        public float FinalDistanceM { get; set; } = 100;
        public int MaximumPhasingOrbits { get; set; } = 5;
        public float MaximumClosingSpeedMps { get; set; } = 5;
        public float BrakingMargin { get; set; } = 2;
        public float PositionToleranceM { get; set; } = 5;
        public float VelocityToleranceMps { get; set; } = 0.02f;
        public float SettleSeconds { get; set; } = 2;
        public float ReservePercent { get; set; }
        public int MaximumCorrections { get; set; } = 4;
        public float WarpLeadSeconds { get; set; } = 180;
        [JsonIgnore] public bool IsValid => ProximityRange.Valid(FinalDistanceM, 50, 1000)
            && MaximumPhasingOrbits >= 1 && MaximumPhasingOrbits <= 10
            && ProximityRange.Valid(MaximumClosingSpeedMps, 0.1f, 20) && ProximityRange.Valid(BrakingMargin, 1.2f, 5)
            && ProximityRange.Valid(PositionToleranceM, 1, 20) && ProximityRange.Valid(VelocityToleranceMps, 0.01f, 0.2f)
            && ProximityRange.Valid(SettleSeconds, 1, 10) && ProximityRange.Valid(ReservePercent, 0, 50)
            && MaximumCorrections >= 1 && MaximumCorrections <= 10 && ProximityRange.Valid(WarpLeadSeconds, 60, 600);
        public RendezvousSettings Copy() => (RendezvousSettings)MemberwiseClone();
        public bool Matches(RendezvousSettings other) => other != null && AutoWarp == other.AutoWarp
            && CourseCorrections == other.CourseCorrections && FinalDistanceM == other.FinalDistanceM
            && MaximumPhasingOrbits == other.MaximumPhasingOrbits && MaximumClosingSpeedMps == other.MaximumClosingSpeedMps
            && BrakingMargin == other.BrakingMargin && PositionToleranceM == other.PositionToleranceM
            && VelocityToleranceMps == other.VelocityToleranceMps && SettleSeconds == other.SettleSeconds
            && ReservePercent == other.ReservePercent && MaximumCorrections == other.MaximumCorrections
            && WarpLeadSeconds == other.WarpLeadSeconds;
    }

    internal sealed class DockingSettings
    {
        public bool ShowGuidanceOverlay { get; set; }
        public bool EngineAssist { get; set; }
        public float MaximumSpeedMps { get; set; } = 1;
        public float NearSpeedMps { get; set; } = 0.3f;
        public float ContactSpeedMps { get; set; } = 0.08f;
        public float AlignmentSpeedMps { get; set; } = 0.5f;
        public float SafetyMarginM { get; set; } = 20;
        public bool OverrideStartDistance { get; set; }
        public float StartDistanceM { get; set; } = 30;
        public float CorridorRadiusM { get; set; } = 0.3f;
        public float AlignmentToleranceDeg { get; set; } = 2;
        public float MaximumAngularRateDegS { get; set; } = 0.25f;
        public float MaximumTurnRateDegS { get; set; } = 1.5f;
        public float BrakingMargin { get; set; } = 1.25f;
        public bool ForceRoll { get; set; } = true;
        public float RollAngleDeg { get; set; }
        [JsonIgnore] public bool IsValid => ProximityRange.Valid(MaximumSpeedMps, 0.05f, 5)
            && ProximityRange.Valid(NearSpeedMps, 0.02f, 1) && ProximityRange.Valid(ContactSpeedMps, 0.01f, 0.2f)
            && ProximityRange.Valid(AlignmentSpeedMps, 0.05f, 2) && ProximityRange.Valid(SafetyMarginM, 5, 100)
            && ProximityRange.Valid(StartDistanceM, 10, 500) && ProximityRange.Valid(CorridorRadiusM, 0.05f, 0.5f)
            && ProximityRange.Valid(AlignmentToleranceDeg, 0.5f, 5) && ProximityRange.Valid(MaximumAngularRateDegS, 0.05f, 1)
            && ProximityRange.Valid(MaximumTurnRateDegS, 0.1f, 3) && ProximityRange.Valid(BrakingMargin, 1.2f, 5)
            && ProximityRange.Valid(RollAngleDeg, -180, 180);
        public DockingSettings Copy() => (DockingSettings)MemberwiseClone();
        public bool Matches(DockingSettings other) => other != null && ShowGuidanceOverlay == other.ShowGuidanceOverlay && EngineAssist == other.EngineAssist && MaximumSpeedMps == other.MaximumSpeedMps
            && NearSpeedMps == other.NearSpeedMps && ContactSpeedMps == other.ContactSpeedMps
            && AlignmentSpeedMps == other.AlignmentSpeedMps && SafetyMarginM == other.SafetyMarginM
            && OverrideStartDistance == other.OverrideStartDistance && StartDistanceM == other.StartDistanceM
            && CorridorRadiusM == other.CorridorRadiusM && AlignmentToleranceDeg == other.AlignmentToleranceDeg
            && MaximumAngularRateDegS == other.MaximumAngularRateDegS && MaximumTurnRateDegS == other.MaximumTurnRateDegS
            && BrakingMargin == other.BrakingMargin && ForceRoll == other.ForceRoll && RollAngleDeg == other.RollAngleDeg;
    }

    internal static class ProximityRange
    {
        public static bool Valid(float value, float low, float high) => float.IsFinite(value) && value >= low && value <= high;
    }
}
