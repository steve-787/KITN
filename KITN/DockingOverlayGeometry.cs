using System;

namespace KITN
{
    internal enum DockingOverlayKind { Clearance, Route, Checkpoint, Corridor, Port }
    internal readonly struct DockingOverlaySegment
    {
        public readonly LandingVector A, B;
        public readonly DockingOverlayKind Kind;
        public DockingOverlaySegment(LandingVector a, LandingVector b, DockingOverlayKind kind)
        { A = a; B = b; Kind = kind; }
    }

    /// <summary>Fixed-cost 3D wire geometry; no meshes, physics queries or per-frame arrays.</summary>
    internal sealed class DockingOverlayGeometry
    {
        public const int MaximumSegments = 320, RingSegments = 24;
        private static readonly double[] Cos = new double[RingSegments + 1], Sin = new double[RingSegments + 1];
        public readonly DockingOverlaySegment[] Segments = new DockingOverlaySegment[MaximumSegments];
        public int Count { get; private set; }
        static DockingOverlayGeometry()
        { for (int i = 0; i <= RingSegments; i++) { Cos[i] = Math.Cos(i * 2 * Math.PI / RingSegments); Sin[i] = Math.Sin(i * 2 * Math.PI / RingSegments); } }

        public void Build(DockingGuidancePlan plan, LandingVector livePosition, double standOff, double corridor, double markerSize = 0.7)
        {
            Count = 0;
            if (plan.Current == DockingStep.None || !livePosition.IsFinite) return;
            double radius = plan.SafeRadius;
            var x = new LandingVector(radius, 0, 0); var y = new LandingVector(0, radius, 0); var z = new LandingVector(0, 0, radius);
            Ring(default, x, y, DockingOverlayKind.Clearance);
            Ring(default, x, z, DockingOverlayKind.Clearance);
            Ring(default, y, z, DockingOverlayKind.Clearance);
            var corridorEnd = new LandingVector(Math.Max(3, standOff), 0, 0);
            var cy = new LandingVector(0, corridor, 0); var cz = new LandingVector(0, 0, corridor);
            Ring(corridorEnd, cy, cz, DockingOverlayKind.Corridor);
            Ring(default, cy, cz, DockingOverlayKind.Corridor);
            Add(cy, corridorEnd + cy, DockingOverlayKind.Corridor);
            Add(-cy, corridorEnd - cy, DockingOverlayKind.Corridor);
            Add(cz, corridorEnd + cz, DockingOverlayKind.Corridor);
            Add(-cz, corridorEnd - cz, DockingOverlayKind.Corridor);
            for (int i = 1; i < plan.Count; i++)
                Add(i == 1 ? livePosition : plan.Points[i - 1], plan.Points[i], DockingOverlayKind.Route);
            Box(plan.Checkpoint, Math.Clamp(markerSize, 0.35, 4), DockingOverlayKind.Checkpoint);
            Box(default, Math.Max(0.15, Math.Min(0.4, corridor)), DockingOverlayKind.Port);
        }

        private void Ring(LandingVector center, LandingVector a, LandingVector b, DockingOverlayKind kind)
        { for (int i = 1; i <= RingSegments; i++) Add(center + a * Cos[i - 1] + b * Sin[i - 1], center + a * Cos[i] + b * Sin[i], kind); }
        private void Box(LandingVector center, double half, DockingOverlayKind kind)
        {
            for (int i = 0; i < 8; i++)
            {
                var vertex = center + new LandingVector((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half, (i & 4) == 0 ? -half : half);
                for (int axis = 1; axis <= 4; axis <<= 1)
                    if ((i & axis) == 0) Add(vertex, vertex + new LandingVector(axis == 1 ? 2 * half : 0,
                        axis == 2 ? 2 * half : 0, axis == 4 ? 2 * half : 0), kind);
            }
        }
        private void Add(LandingVector a, LandingVector b, DockingOverlayKind kind)
        { if (Count < MaximumSegments && a.IsFinite && b.IsFinite && (a - b).LengthSquared > 1e-12) Segments[Count++] = new DockingOverlaySegment(a, b, kind); }
    }
}
