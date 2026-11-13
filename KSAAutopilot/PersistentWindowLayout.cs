using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSAAutopilot
{
    internal sealed class PersistentWindowLayout
    {
        private readonly UiLayoutStore _store;
        private readonly string _id;
        private bool _restorePending = true;
        private float _lastScale;
        public PersistentWindowLayout(UiLayoutStore store, string id) { _store = store; _id = id; }

        public float2 Prepare(float2 defaultSize, float scale, out ImGuiCond sizeCondition)
        {
            sizeCondition = ImGuiCond.FirstUseEver;
            var viewport = ImGui.GetMainViewport();
            if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(viewport.WorkPos.X) || !float.IsFinite(viewport.WorkPos.Y)
                || !float.IsFinite(viewport.WorkSize.X) || !float.IsFinite(viewport.WorkSize.Y)
                || viewport.WorkSize.X < 1 || viewport.WorkSize.Y < 1) return defaultSize;
            if (_restorePending || scale != _lastScale)
            {
                UiWindowBounds bounds = _store.Find(_id)?.Fit(scale, viewport.WorkPos.X, viewport.WorkPos.Y,
                    viewport.WorkSize.X, viewport.WorkSize.Y);
                if (bounds != null)
                {
                    ImGui.SetNextWindowPos(new float2(bounds.X, bounds.Y), ImGuiCond.Always);
                    defaultSize = new float2(bounds.Width, bounds.Height);
                    // ConsoleStyle sets the next size itself; provide the restored size and its condition there.
                    sizeCondition = ImGuiCond.Always;
                }
                _restorePending = false; _lastScale = scale;
            }
            return defaultSize;
        }

        public void Capture(float scale)
        {
            float2 position = ImGui.GetWindowPos(), size = ImGui.GetWindowSize();
            _store.Record(_id, new UiWindowBounds
            { X = position.X, Y = position.Y, Width = size.X, Height = size.Y, InterfaceScale = scale }, Environment.TickCount64);
        }
    }
}
