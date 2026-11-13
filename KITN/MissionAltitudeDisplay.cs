using System;
using System.Reflection;
using KSA;
using StarMap.API.Services;

namespace KITN
{
    /// <summary>Temporarily hides the native altitude gauge while KITN displays the same altitude.
    /// Never changes the gauge's enabled state, bindings or saved HUD position.</summary>
    internal sealed class MissionAltitudeDisplay : IDisposable
    {
        private readonly Func<bool> _windowOpen;
        private IDisposable _visibilityHook;
        private GaugeCanvas _canvas;
        private bool _hosted, _disposed;

        public MissionAltitudeDisplay(IPatchService patches, Func<bool> windowOpen)
        {
            _windowOpen = windowOpen;
            MethodInfo draw = typeof(GaugeCanvas).GetMethod(nameof(GaugeCanvas.OnDrawUi), BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException("GaugeCanvas.OnDrawUi()");
#pragma warning disable PatchHelperExperimental
            _visibilityHook = patches.RegisterInstanceVoidPatch<GaugeCanvas>(IPatchService.PatchType.Prefix, draw, BeforeNativeDraw);
#pragma warning restore PatchHelperExperimental
        }

        private bool BeforeNativeDraw(object[] args, GaugeCanvas canvas) =>
            _disposed || !_hosted || !_windowOpen() || !ReferenceEquals(canvas, _canvas);

        public bool Draw()
        {
            if (_disposed || !_windowOpen() || Program.ControlledVehicle == null) { Release(); return false; }
            _canvas = null;
            foreach (GaugeCanvas candidate in GaugeCanvas.AllCanvases)
                if (candidate.Id == "Altitude" && candidate.Enabled && !candidate.LoadFailed)
                { _canvas = candidate; break; }
            if (_canvas == null) { Release(); return false; }
            _hosted = true;
            return true;
        }

        public void Release() { _hosted = false; _canvas = null; }

        public void Dispose()
        {
            if (_disposed) return;
            Release(); _disposed = true;
            _visibilityHook?.Dispose(); _visibilityHook = null;
        }
    }
}
