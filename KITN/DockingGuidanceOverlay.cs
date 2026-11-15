using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>World-space wireframes projected like KSA's native collider overlays.
    /// Draws behind UI windows; no GPU resources, scene objects or physics work.</summary>
    internal sealed class DockingGuidanceOverlay
    {
        private readonly DockingOverlayGeometry _geometry = new DockingOverlayGeometry();
        public int LastDrawnSegments { get; private set; }

        public void Draw(RendezvousController controller)
        {
            LastDrawnSegments = 0;
            if (controller?.IsAutoDocking != true || !controller.DockingOptions.ShowGuidanceOverlay
                || controller.Target?.Parent == null || controller.TargetPort?.Connector == null) return;
            var viewport = ViewportRegistry.MainViewport;
            if (!viewport.Visible || viewport.Width < 1 || viewport.Height < 1) return;
            var camera = viewport.GetCamera();
            var target = controller.Target;
            var body2Ecl = target.GetBodyFixed2Ecl();
            var port2Ecl = doubleQuat.Concatenate(controller.TargetPort.Connector.Asmb2VehicleAsmb, body2Ecl);
            // GetPositionEgo uses local physics coordinates when both craft share a bubble,
            // avoiding subtraction of huge heliocentric positions during moon docking.
            var origin = camera.GetPositionEgo(target)
                + (controller.TargetPort.Connector.PositionVehicleAsmb - target.CenterOfMassAsmb).Transform(body2Ecl);
            DrawScene(ImGuiHelper.GetOverlayDrawList(viewport), camera, viewport.Position, new float2(viewport.Width, viewport.Height),
                origin, port2Ecl, controller.DockingPreview, controller.DockingRelativePosition,
                controller.DockingStandOff, controller.DockingOptions.CorridorRadiusM);
        }

        internal void DrawScene(ImDrawListPtr draw, Camera camera, float2 viewportOrigin, float2 viewportSize,
            double3 origin, doubleQuat basis, DockingGuidancePlan plan, LandingVector livePosition, double standOff, double corridor)
        {
            LastDrawnSegments = 0;
            if (!origin.IsFinite() || origin.Length() > 100000 || plan.Current == DockingStep.None) return;
            double markerSize = Math.Max(0.35, (origin + Native(plan.Checkpoint).Transform(basis)).Length() * 0.0015);
            _geometry.Build(plan, livePosition, standOff, corridor, markerSize);
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            draw.PushClipRect(viewportOrigin, viewportOrigin + viewportSize, true);
            try
            {
                for (int i = 0; i < _geometry.Count; i++)
                {
                    var segment = _geometry.Segments[i];
                    var a = origin + Native(segment.A).Transform(basis);
                    var b = origin + Native(segment.B).Transform(basis);
                    if (!ClipFront(camera, ref a, ref b)
                        || !Project(camera, viewportOrigin, a, out var screenA) || !Project(camera, viewportOrigin, b, out var screenB)) continue;
                    if ((screenB - screenA).LengthSquared() < 0.1f) continue;
                    float4 color = segment.Kind == DockingOverlayKind.Clearance ? new float4(0.95f, 0.68f, 0.25f, 0.28f)
                        : segment.Kind == DockingOverlayKind.Route ? new float4(0.35f, 0.83f, 0.91f, 0.95f)
                        : segment.Kind == DockingOverlayKind.Corridor ? new float4(0.4f, 0.85f, 0.64f, 0.32f)
                        : segment.Kind == DockingOverlayKind.Checkpoint ? GameUiColors.Pending : GameUiColors.Text;
                    float thickness = (segment.Kind == DockingOverlayKind.Route ? 1.8f : segment.Kind == DockingOverlayKind.Checkpoint ? 1.5f : 1) * scale;
                    if (segment.Kind == DockingOverlayKind.Route)
                        draw.AddLine(screenA, screenB, ImGui.ColorConvertFloat4ToU32(new float4(0.03f, 0.05f, 0.07f, 0.7f)), thickness + 2 * scale);
                    draw.AddLine(screenA, screenB, ImGui.ColorConvertFloat4ToU32(color), thickness);
                    LastDrawnSegments++;
                }
                Label(draw, camera, viewportOrigin, origin + Native(plan.Checkpoint).Transform(basis),
                    L10n.Get("dock.overlay_checkpoint") + " · " + (plan.Checkpoint - livePosition).Length.ToString("N1", L10n.Culture) + " m", scale);
                Label(draw, camera, viewportOrigin, origin + new double3(0, plan.SafeRadius, 0).Transform(basis),
                    L10n.Get("dock.overlay_zone"), scale);
            }
            finally { draw.PopClipRect(); }
        }

        private static bool ClipFront(Camera camera, ref double3 a, ref double3 b)
        {
            var forward = camera.GetForwardEcl();
            double da = double3.Dot(a, forward), db = double3.Dot(b, forward);
            const double near = 0.05;
            if (da < near && db < near) return false;
            if (da < near) a += (b - a) * ((near - da) / (db - da));
            else if (db < near) b += (a - b) * ((near - db) / (da - db));
            return true;
        }
        private static bool Project(Camera camera, float2 offset, double3 point, out float2 screen)
        {
            screen = camera.EgoToScreen(point) + offset;
            return float.IsFinite(screen.X) && float.IsFinite(screen.Y) && Math.Abs(screen.X) < 1e6 && Math.Abs(screen.Y) < 1e6;
        }
        private static void Label(ImDrawListPtr draw, Camera camera, float2 offset, double3 point, string text, float scale)
        {
            if (!Project(camera, offset, point, out var screen)) return;
            ConsoleStyle.PushLabelFont(0.8f);
            try
            {
                var padding = new float2(5, 3) * scale;
                var size = ImGui.CalcTextSize(text);
                screen += new float2(8, -size.Y - 8) * scale;
                draw.AddRectFilled(screen - padding, screen + size + padding,
                    ImGui.ColorConvertFloat4ToU32(new float4(0.03f, 0.05f, 0.07f, 0.82f)), 3 * scale);
                draw.AddText(screen, ImGui.ColorConvertFloat4ToU32(GameUiColors.Pending), text);
            }
            finally { ConsoleStyle.PopFont(); }
        }
        private static double3 Native(LandingVector v) => new double3(v.X, v.Y, v.Z);
    }
}
