using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class PersistentWindowLayout
    {
        private readonly UiLayoutStore _store;
        private readonly string _id;
        private bool _restorePending = true;
        private float _lastScale;
        private bool _dragging;
        private bool _interacting;
        private float2 _dragOffset;
        private int _lastFrame = -2;
        private AlignmentResult _alignment;
        public PersistentWindowLayout Peer { get; set; }
        public bool Enabled { get; set; }
        public UiWindowBounds Current { get; private set; }
        public bool IsInteracting => _interacting;
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
                    // The window style supplies its size, so return the restore condition as well.
                    sizeCondition = ImGuiCond.Always;
                }
                _restorePending = false; _lastScale = scale;
            }
            _alignment = default;
            if (Current?.IsValid == true && sizeCondition != ImGuiCond.Always)
            {
                var io = ImGui.GetIO();
                bool move = _dragging && (ImGui.IsMouseDragging(ImGuiMouseButton.Left)
                    || ImGui.IsMouseReleased(ImGuiMouseButton.Left));
                float2 raw = move ? io.MousePos - _dragOffset : new float2(Current.X, Current.Y);
                var moving = new UiWindowBounds { X = raw.X, Y = raw.Y,
                    Width = Current.Width, Height = Current.Height, InterfaceScale = scale };
                var screen = new UiWindowBounds { X = viewport.WorkPos.X, Y = viewport.WorkPos.Y,
                    Width = viewport.WorkSize.X, Height = viewport.WorkSize.Y, InterfaceScale = scale };
                UiWindowBounds peer = Peer?.Enabled == true && Peer._lastFrame >= ImGui.GetFrameCount() - 1 ? Peer.Current : null;
                _alignment = move && io.KeyAlt ? WindowAlignment.Snap(moving, screen, peer, 10 * scale) : new AlignmentResult(
                    Math.Clamp(raw.X, screen.X, screen.X + Math.Max(0, screen.Width - moving.Width)),
                    Math.Clamp(raw.Y, screen.Y, screen.Y + Math.Max(0, screen.Height - moving.Height)), null, null);
                if (move || _alignment.X != Current.X || _alignment.Y != Current.Y)
                    // Apply before Begin: native title, background, border and content are all
                    // generated at the snapped position. SetWindowPos after Begin moves only
                    // the content; the decorations have already been emitted to the draw list.
                    ImGui.SetNextWindowPos(new float2(_alignment.X, _alignment.Y), ImGuiCond.Always);
            }
            return defaultSize;
        }

        public void Capture(float scale)
        {
            float2 position = ImGui.GetWindowPos(), size = ImGui.GetWindowSize();
            var io = ImGui.GetIO();
            float titleHeight = ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.Y * 2;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered()) _interacting = true;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered()
                && io.MousePos.Y >= position.Y && io.MousePos.Y < position.Y + titleHeight)
            { _dragging = true; _dragOffset = io.MousePos - position; }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { _dragging = false; _interacting = false; }
            Current = new UiWindowBounds
            { X = position.X, Y = position.Y, Width = size.X, Height = size.Y, InterfaceScale = scale };
            _lastFrame = ImGui.GetFrameCount();
            _store.Record(_id, Current, Environment.TickCount64);
        }

        public static void DrawGuides(PersistentWindowLayout first, PersistentWindowLayout second)
        {
            if (!ImGui.GetIO().KeyAlt) return;
            bool Visible(PersistentWindowLayout layout) => layout?.Enabled == true && layout._lastFrame == ImGui.GetFrameCount();
            if (!Visible(first) && !Visible(second)) return;
            var viewport = ImGui.GetMainViewport();
            float2 min = viewport.WorkPos, max = min + viewport.WorkSize;
            var draw = ImGui.GetForegroundDrawList();
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            uint faint = ImGui.ColorConvertFloat4ToU32(new float4(ConsoleStyle.ActiveTool.X, ConsoleStyle.ActiveTool.Y, ConsoleStyle.ActiveTool.Z, 0.25f));
            uint active = ConsoleStyle.ActiveToolU32;
            void Vertical(float x, uint color) => draw.AddLine(new float2(x, min.Y), new float2(x, max.Y), color, Math.Max(1, scale));
            void Horizontal(float y, uint color) => draw.AddLine(new float2(min.X, y), new float2(max.X, y), color, Math.Max(1, scale));
            Vertical((min.X + max.X) / 2, faint); Horizontal((min.Y + max.Y) / 2, faint);
            void WindowGuides(PersistentWindowLayout layout)
            {
                if (!Visible(layout)) return;
                Vertical(layout.Current.X + layout.Current.Width / 2, faint);
                Horizontal(layout.Current.Y + layout.Current.Height / 2, faint);
                if (layout._alignment.GuideX.HasValue) Vertical(layout._alignment.GuideX.Value, active);
                if (layout._alignment.GuideY.HasValue) Horizontal(layout._alignment.GuideY.Value, active);
            }
            WindowGuides(first); WindowGuides(second);
        }
    }
}
