using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.Pointers;
using KSA;
using StarMap.API.Services;

namespace KITN
{
    /// <summary>Hosts KSA's original altitude canvas without changing its HUD layout or enabled state.</summary>
    internal sealed unsafe class MissionAltitudeDisplay : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CanvasDrawData
        {
            public int CanvasIndex;
            public float2 RectMin;
            public float2 RectSize;
            public float4 Tint;
        }

        private readonly Func<bool> _windowOpen;
        private readonly ImDrawCallback _drawCallback;
        private IDisposable _visibilityHook;
        private GaugeCanvas _canvas;
        private bool _hosted, _disposed;

        public MissionAltitudeDisplay(IPatchService patches, Func<bool> windowOpen)
        {
            _windowOpen = windowOpen;
            // ImGui copies this data into its command buffer. Check the native layout before
            // reusing the game's callback, so an API change cannot pass it incompatible data.
            Type nativeData = typeof(GaugeCanvas).GetNestedType("GaugeDrawCallbackData", BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingMemberException("GaugeCanvas.GaugeDrawCallbackData");
            if (Marshal.SizeOf(nativeData) != sizeof(CanvasDrawData))
                throw new InvalidOperationException("KSA gauge draw data size changed");
            foreach (string name in new[] { "CanvasIndex", "RectMin", "RectSize", "Tint" })
                if (Marshal.OffsetOf(nativeData, name) != Marshal.OffsetOf<CanvasDrawData>(name))
                    throw new InvalidOperationException("KSA gauge draw data layout changed: " + name);
            _drawCallback = (ImDrawCallback)(typeof(GaugeCanvas).GetField("_gaugeDrawCallback", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) ?? throw new MissingFieldException("GaugeCanvas._gaugeDrawCallback"));
            MethodInfo draw = typeof(GaugeCanvas).GetMethod(nameof(GaugeCanvas.OnDrawUi), BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException("GaugeCanvas.OnDrawUi()");
#pragma warning disable PatchHelperExperimental
            _visibilityHook = patches.RegisterInstanceVoidPatch<GaugeCanvas>(IPatchService.PatchType.Prefix, draw, BeforeNativeDraw);
#pragma warning restore PatchHelperExperimental
            Diag.Info("ALTITUDE_DISPLAY", "event=registered renderer=original_KSA_canvas visibility=temporary_UI_prefix");
        }

        private bool BeforeNativeDraw(object[] args, GaugeCanvas canvas)
        {
            return _disposed || !_hosted || !_windowOpen() || !ReferenceEquals(canvas, _canvas);
        }

        public bool Draw()
        {
            if (_disposed || !_windowOpen() || Program.ControlledVehicle == null)
            { Release(); return false; }
            try
            {
                var canvases = GaugeCanvas.AllCanvases;
                if (_canvas == null || CanvasIndex(canvases, _canvas) < 0 || !Ready(_canvas))
                {
                    Release();
                    _canvas = null;
                    foreach (GaugeCanvas candidate in canvases)
                        if (string.Equals(candidate.Id, "Altitude", StringComparison.Ordinal) && Ready(candidate))
                        { _canvas = candidate; break; }
                }
                if (_canvas == null) return false;
                float2 resolution = _canvas.RenderResolution;
                float aspect = resolution.X / resolution.Y;
                float available = ImGui.GetContentRegionAvail().X;
                if (!float.IsFinite(aspect) || aspect <= 0 || !float.IsFinite(available) || available < 1)
                { Release(); return false; }
                float width = Math.Min(available, 400 * ConsoleStyle.EffectiveInterfaceScale);
                var size = new float2(width, width / aspect);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (available - width) * 0.5f));
                var data = new CanvasDrawData
                {
                    CanvasIndex = CanvasIndex(canvases, _canvas), RectMin = ImGui.GetCursorScreenPos(),
                    RectSize = size, Tint = float4.One
                };
                // Exactly the same renderer, digit bindings, shader and atlas as the HUD.
                // Only the destination rectangle is the T-window's content region.
                ImGui.GetWindowDrawList().AddCallback(_drawCallback, new Ptr(&data), (nuint)sizeof(CanvasDrawData));
                ImGui.Dummy(size);
                if (!_hosted)
                    Diag.Info("ALTITUDE_DISPLAY", "event=hosted canvas=Altitude original_hud_hidden=true layout_changed=false");
                _hosted = true;
                return true;
            }
            catch (Exception ex)
            {
                Release();
                Diag.EveryError("mission_altitude", 5, "ALTITUDE_DISPLAY", ex, "Drawing the original altitude instrument in the mission clock failed");
                return false;
            }
        }

        private static bool Ready(GaugeCanvas canvas) => !canvas.LoadFailed && canvas.Enabled
            && canvas.CanvasRenderer != null && canvas.Bundles.Length > 0
            && canvas.RenderResolution.X > 0 && canvas.RenderResolution.Y > 0;

        private static int CanvasIndex(IReadOnlyList<GaugeCanvas> canvases, GaugeCanvas canvas)
        {
            for (int i = 0; i < canvases.Count; i++) if (ReferenceEquals(canvases[i], canvas)) return i;
            return -1;
        }

        public void Release()
        {
            if (_hosted) Diag.Info("ALTITUDE_DISPLAY", "event=released original_hud_restored=true layout_changed=false");
            _hosted = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            Release();
            _disposed = true;
            _visibilityHook?.Dispose();
            _visibilityHook = null;
            _canvas = null;
        }
    }
}
