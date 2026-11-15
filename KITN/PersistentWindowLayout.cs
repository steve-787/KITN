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
        private bool _customDragging;
        private bool _interacting;
        private float2 _dragOffset;
        private ImGuiID _windowId;
        private int _lastFrame = -2;
        private AlignmentResult _alignment;
        private float2? _pendingPosition;
        public PersistentWindowLayout Peer { get; set; }
        public bool Enabled { get; set; }
        public UiWindowBounds Current { get; private set; }
        public bool IsInteracting => _interacting;
        public bool Detached { get; private set; }
        public string SavedId => Detached ? _id + "_detached" : _id;
        public PersistentWindowLayout(UiLayoutStore store, string id) { _store = store; _id = id; }

        public unsafe float2 Prepare(float2 defaultSize, float scale, out ImGuiCond sizeCondition, bool fixedSize = false)
        {
            sizeCondition = ImGuiCond.FirstUseEver;
            var viewport = ImGui.GetMainViewport();
            bool detached = _store.DetachedWindows && ModWindowStyle.SupportsDetachedWindows;
            if (Detached != detached)
            {
                Detached = detached;
                _restorePending = true;
                _dragging = _customDragging = _interacting = false;
                _alignment = default;
            }
            if (Detached)
            {
                var windowClass = new ImGuiWindowClass { ViewportFlagsOverrideSet = ImGuiViewportFlags.NoAutoMerge };
                ImGui.SetNextWindowClass(&windowClass);
            }
            else ImGui.SetNextWindowViewport(viewport.ID);
            if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(viewport.WorkPos.X) || !float.IsFinite(viewport.WorkPos.Y)
                || !float.IsFinite(viewport.WorkSize.X) || !float.IsFinite(viewport.WorkSize.Y)
                || viewport.WorkSize.X < 1 || viewport.WorkSize.Y < 1) return defaultSize;
            if (_restorePending || scale != _lastScale)
            {
                UiWindowBounds saved = _store.Find(SavedId) ?? Current ?? _store.Find(_id);
                UiWindowBounds screen = WorkArea(saved, scale);
                UiWindowBounds bounds = Fit(saved, scale, screen);
                if (bounds != null)
                {
                    if (fixedSize)
                    {
                        bounds.Width = defaultSize.X; bounds.Height = defaultSize.Y;
                        bounds = Fit(bounds, scale, screen);
                    }
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
                var context = ImGui.GetCurrentContext();
                var nativeMoving = context.MovingWindow;
                bool nativeDrag = !nativeMoving.IsNull() && nativeMoving.RootWindow.ID == _windowId;
                if (nativeDrag)
                {
                    // Use the native move state and click offset. It includes dragging from
                    // empty content/child areas and never mistakes a control or resize for a move.
                    _dragging = true;
                    _dragOffset = context.ActiveIdClickOffset;
                }
                bool move = ((nativeDrag || _customDragging) && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    || (_dragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left));
                float2 raw = move ? io.MousePos - _dragOffset : new float2(Current.X, Current.Y);
                var moving = new UiWindowBounds { X = raw.X, Y = raw.Y,
                    Width = fixedSize ? defaultSize.X : Current.Width, Height = fixedSize ? defaultSize.Y : Current.Height, InterfaceScale = scale };
                var screen = WorkArea(moving, scale);
                UiWindowBounds peer = Peer?.Enabled == true && Peer.Detached == Detached
                    && Peer._lastFrame >= ImGui.GetFrameCount() - 1 ? Peer.Current : null;
                _alignment = Detached ? new AlignmentResult(raw.X, raw.Y, null, null)
                    : move && AlignmentHeld ? WindowAlignment.Snap(moving, screen, peer, 10 * scale) : new AlignmentResult(
                    Math.Clamp(raw.X, screen.X, screen.X + Math.Max(0, screen.Width - moving.Width)),
                    Math.Clamp(raw.Y, screen.Y, screen.Y + Math.Max(0, screen.Height - moving.Height)), null, null);
                if (move || _alignment.X != Current.X || _alignment.Y != Current.Y)
                    // Apply before Begin: native title, background, border and content are all
                    // generated at the snapped position. SetWindowPos after Begin moves only
                    // the content; the decorations have already been emitted to the draw list.
                    ImGui.SetNextWindowPos(new float2(_alignment.X, _alignment.Y), ImGuiCond.Always);
            }
            if (_pendingPosition.HasValue)
            {
                float2 position = _pendingPosition.Value;
                float2 actualSize = fixedSize || sizeCondition == ImGuiCond.Always || Current == null
                    ? defaultSize : new float2(Current.Width, Current.Height);
                var screen = WorkArea(new UiWindowBounds { X = position.X, Y = position.Y,
                    Width = actualSize.X, Height = actualSize.Y, InterfaceScale = scale }, scale);
                ImGui.SetNextWindowPos(new float2(
                    Math.Clamp(position.X, screen.X, screen.X + Math.Max(0, screen.Width - actualSize.X)),
                    Math.Clamp(position.Y, screen.Y, screen.Y + Math.Max(0, screen.Height - actualSize.Y))), ImGuiCond.Always);
                _pendingPosition = null;
            }
            return defaultSize;
        }

        private UiWindowBounds Fit(UiWindowBounds bounds, float scale, UiWindowBounds screen) => Detached
            ? bounds?.FitDesktop(scale, screen.X, screen.Y, screen.Width, screen.Height)
            : bounds?.Fit(scale, screen.X, screen.Y, screen.Width, screen.Height);

        private UiWindowBounds WorkArea(UiWindowBounds bounds, float scale)
        {
            var viewport = ImGui.GetMainViewport();
            var screen = new UiWindowBounds { X = viewport.WorkPos.X, Y = viewport.WorkPos.Y,
                Width = viewport.WorkSize.X, Height = viewport.WorkSize.Y, InterfaceScale = scale };
            if (!Detached || bounds?.IsValid != true) return screen;
            // Prefer the monitor containing most of the window. If it was disconnected,
            // use the closest remaining work area, including monitors left of the game.
            var monitors = ImGui.GetPlatformIO().Monitors;
            double bestOverlap = -1, bestDistance = double.PositiveInfinity;
            for (int i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                var candidate = new UiWindowBounds { X = monitor.WorkPos.X, Y = monitor.WorkPos.Y,
                    Width = monitor.WorkSize.X, Height = monitor.WorkSize.Y, InterfaceScale = scale };
                if (!candidate.IsValid) continue;
                double overlap = Math.Max(0, Math.Min((double)bounds.X + bounds.Width, (double)candidate.X + candidate.Width)
                    - Math.Max(bounds.X, candidate.X))
                    * Math.Max(0, Math.Min((double)bounds.Y + bounds.Height, (double)candidate.Y + candidate.Height)
                    - Math.Max(bounds.Y, candidate.Y));
                double centerX = (double)bounds.X + bounds.Width / 2, centerY = (double)bounds.Y + bounds.Height / 2;
                double dx = centerX - Math.Clamp(centerX, candidate.X, (double)candidate.X + candidate.Width);
                double dy = centerY - Math.Clamp(centerY, candidate.Y, (double)candidate.Y + candidate.Height);
                double distance = dx * dx + dy * dy;
                if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
                { screen = candidate; bestOverlap = overlap; bestDistance = distance; }
            }
            return screen;
        }

        public void PlaceAt(float2 position) => _pendingPosition = position;

        public void Capture(float scale)
        {
            float2 position = ImGui.GetWindowPos(), size = ImGui.GetWindowSize();
            var io = ImGui.GetIO();
            _windowId = ImGui.GetCurrentContext().CurrentWindow.RootWindow.ID;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
                _interacting = true;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { _dragging = _customDragging = false; _interacting = false; }
            Current = new UiWindowBounds
            { X = position.X, Y = position.Y, Width = size.X, Height = size.Y, InterfaceScale = scale };
            _lastFrame = ImGui.GetFrameCount();
            _store.Record(SavedId, Current, Environment.TickCount64);
        }

        public void BeginDrag()
        {
            if (!ImGui.IsItemActivated()) return;
            _customDragging = _dragging = _interacting = true;
            _dragOffset = ImGui.GetIO().MousePos - ImGui.GetWindowPos();
        }

        public static void DrawGuides(PersistentWindowLayout first, PersistentWindowLayout second)
        {
            if (!AlignmentHeld) return;
            bool Visible(PersistentWindowLayout layout) => layout?.Enabled == true && !layout.Detached
                && layout._lastFrame == ImGui.GetFrameCount();
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

        private static bool AlignmentHeld => ImGui.GetIO().KeyAlt
            || ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
    }
}
