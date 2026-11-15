using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Activity and idle views retain independent positions across minimization and restart.</summary>
    internal sealed class MinimizedWindow
    {
        private readonly UiLayoutStore _store;
        private readonly PersistentWindowLayout _activityLayout, _iconLayout;
        private UiWindowBounds _fallbackBounds;
        private bool _fallbackDetached;
        private bool _active, _enabled;
        public PersistentWindowLayout Layout => _active ? _activityLayout : _iconLayout;
        public PersistentWindowLayout Peer { set { _activityLayout.Peer = _iconLayout.Peer = value; } }
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; _activityLayout.Enabled = value && _active; _iconLayout.Enabled = value && !_active; }
        }
        private bool _dragged;
        private float _hover;

        public MinimizedWindow(UiLayoutStore store)
        {
            _store = store;
            _activityLayout = new PersistentWindowLayout(store, "minimized_activity");
            _iconLayout = new PersistentWindowLayout(store, "minimized_icon");
            // Preserve the former shared position as the starting point for each new view.
            UiWindowBounds previous = store.Find("minimized");
            if (previous != null)
            {
                if (store.Find("minimized_activity") == null) store.Record("minimized_activity", previous, Environment.TickCount64);
                if (store.Find("minimized_icon") == null) store.Record("minimized_icon", previous, Environment.TickCount64);
            }
        }

        public void PlaceAt(UiWindowBounds bounds)
        {
            // Closing the main window supplies a fallback, never replaces a saved view position.
            _fallbackBounds = bounds;
            _fallbackDetached = _store.DetachedWindows && ModWindowStyle.SupportsDetachedWindows;
        }

        public bool Draw(ModActivity activity)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            float2 size = (activity.Active ? new float2(280, 108) : new float2(40, 40)) * scale;
            _active = activity.Active;
            Enabled = _enabled;
            bool detached = _store.DetachedWindows && ModWindowStyle.SupportsDetachedWindows;
            string suffix = detached ? "_detached" : "";
            if (_store.Find((_active ? "minimized_activity" : "minimized_icon") + suffix) == null)
            {
                var viewport = ImGui.GetMainViewport();
                UiWindowBounds initial = (_fallbackDetached == detached ? _fallbackBounds : null)
                    ?? _store.Find("autopilot" + suffix);
                if (!detached) initial = initial?.Fit(scale,
                    viewport.WorkPos.X, viewport.WorkPos.Y, viewport.WorkSize.X, viewport.WorkSize.Y);
                if (initial != null) Layout.PlaceAt(new float2(initial.X, initial.Y));
            }
            // Restore position independently of size: status changes never resize the active readout.
            Layout?.Prepare(size, scale, out _, fixedSize: true);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ConsoleStyle.PushWidgetStyle();
            ImGui.PushStyleColor(ImGuiCol.WindowBg, GameUiColors.Panel);
            ImGui.PushStyleColor(ImGuiCol.Border, GameUiColors.Hairline);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, float2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6 * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, Math.Max(1, scale));
            bool restore = false;
            if (ImGui.Begin(_active ? "###KITNMinimizedActivity" : "###KITNMinimizedIcon", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar))
            {
                Layout?.Capture(scale);
                float2 origin = ImGui.GetWindowPos();
                ImGui.SetCursorScreenPos(origin);
                bool clicked = ImGui.InvisibleButton("##restore-kitn", size);
                if (ImGui.IsItemActivated()) _dragged = false;
                Layout?.BeginDrag();
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) _dragged = true;
                restore = clicked && !_dragged;
                _hover += ((ImGui.IsItemHovered() ? 1 : 0) - _hover)
                    * (1 - MathF.Exp(-Math.Clamp(ImGui.GetIO().DeltaTime, 0, 0.05f) * 18));
                var draw = ImGui.GetWindowDrawList();
                var wash = GameUiColors.Label; wash.W = _hover * 0.06f;
                draw.AddRectFilled(origin, origin + size, ConsoleStyle.ApplyAlpha(ImGui.ColorConvertFloat4ToU32(wash)), 6 * scale);
                if (!activity.Active)
                {
                    float iconSize = 22 * scale;
                    float2 iconPosition = origin + (size - new float2(iconSize, iconSize)) / 2;
                    UiIcons.Draw(draw, "cat", iconPosition, iconSize, ConsoleStyle.ApplyAlpha(GameUiColors.AccentU32));
                }
                else
                {
                    float pulse = 0.55f + 0.2f * MathF.Sin((float)ImGui.GetTime() * 3);
                    var accent = activity.Controlling ? GameUiColors.Accent : GameUiColors.Label;
                    accent.W *= pulse;
                    draw.AddCircleFilled(origin + new float2(15, 17) * scale, 3 * scale,
                        ConsoleStyle.ApplyAlpha(ImGui.ColorConvertFloat4ToU32(accent)));
                    bool hasDetail = !string.IsNullOrEmpty(activity.Detail);
                    Text(draw, origin + new float2(27, 9) * scale, activity.Label, size.X - 40 * scale, GameUiColors.Muted, true);
                    bool hasNext = !string.IsNullOrEmpty(activity.Next);
                    Text(draw, origin + new float2(12, hasDetail ? 28 : 46) * scale, activity.Phase, size.X - 24 * scale, GameUiColors.Pending);
                    if (hasNext) Text(draw, origin + new float2(12, 47) * scale, "→ " + activity.Next, size.X - 24 * scale, GameUiColors.Pending, true);
                    if (hasDetail) Text(draw, origin + new float2(12, hasNext ? 67 : 68) * scale, activity.Detail, size.X - 24 * scale, GameUiColors.Muted, true);
                    if (!string.IsNullOrEmpty(activity.Extra)) Text(draw, origin + new float2(12, 86) * scale, activity.Extra, size.X - 24 * scale, GameUiColors.Muted, true);
                }
                ModWindowStyle.Hint(L10n.Get("nav.restore"));
            }
            ImGui.End();
            ImGui.PopStyleVar(3); ImGui.PopStyleColor(2); ConsoleStyle.PopWidgetStyle();
            return restore;
        }

        private static void Text(ImDrawListPtr draw, float2 position, string text, float width, float4 color, bool small = false)
        {
            if (small) ConsoleStyle.PushLabelFont(0.8f);
            try { draw.AddText(position, ConsoleStyle.ApplyAlpha(ImGui.ColorConvertFloat4ToU32(color)), AutopilotWindow.FitHeaderText(text ?? "—", width)); }
            finally { if (small) ConsoleStyle.PopFont(); }
        }
    }

    internal readonly struct ModActivity
    {
        public readonly bool Active, Controlling;
        public readonly string Label, Phase, Detail, Next, Extra;
        public ModActivity(string label, string phase, string detail, bool controlling = false, string next = null, string extra = null)
        { Active = true; Controlling = controlling; Label = label; Phase = phase; Detail = detail; Next = next; Extra = extra; }

        public static ModActivity Read(AscentGuidance guidance, FlightDisplay flight,
            MissionClock clock, LandingController landing, RendezvousController rendezvous, ManeuverPlanner planner, bool pending, bool instrumentsEnabled = false)
        {
            string altitude = double.IsFinite(flight.AltitudeMeters) ? (flight.AltitudeMeters / 1000).ToString("N1", L10n.Culture) + " km" : "—";
            string flightDetail = instrumentsEnabled ? null : altitude + " · " + clock.Display;
            if (rendezvous?.IsAutoDocking == true)
                return new ModActivity(L10n.Get("tab.docking") + " · " + rendezvous.DockingEta,
                    rendezvous.DockingCurrentStep, rendezvous.DockingMotionSummary, true, rendezvous.DockingNextStep,
                    rendezvous.DockingRotationSummary);
            if (rendezvous?.IsActive == true)
                return new ModActivity(L10n.Get("activity.controlling"), RendezvousPage.PhaseName(rendezvous.Phase),
                    double.IsFinite(rendezvous.Distance) ? rendezvous.Distance.ToString("N1", L10n.Culture) + " m · " + rendezvous.RelativeSpeed.ToString("N2", L10n.Culture) + " m/s" : rendezvous.TargetName, true);
            if (landing?.IsActive == true)
                return new ModActivity(L10n.Get(landing.State == LandingState.Planning ? "activity.landing_plan" : "activity.controlling"), landing.Phase,
                    double.IsFinite(landing.Clearance) ? (instrumentsEnabled ? "" : landing.Clearance.ToString("N0", L10n.Culture) + " m · ")
                        + landing.VerticalSpeed.ToString("N1", L10n.Culture) + " m/s" : flightDetail,
                    landing.State != LandingState.Planning);
            if (guidance.IsActive)
                return new ModActivity(L10n.Get("activity.controlling"), AutopilotWindow.StateName(guidance.CurrentState), flightDetail, true);
            if (planner?.IsExecuting == true)
                return new ModActivity(L10n.Get("activity.controlling"), planner.DisplayMessage ?? L10n.Get("tab.maneuver"), planner.Target?.Id ?? "—", true);
            if (clock.IsCountingDown)
                return new ModActivity(L10n.Get("activity.countdown"), instrumentsEnabled ? L10n.Get("clock.countdown") : clock.Display, flight.VehicleName);
            if (planner?.IsChecking == true)
                return new ModActivity(L10n.Get("activity.checking"), planner.DisplayMessage ?? L10n.Get("transfer.checking"), planner.Target?.Id ?? "—");
            if (planner?.IsSearching == true)
                return new ModActivity(L10n.Get("activity.transfer"), L10n.Get("transfer.calculating"), planner.Progress.ToString(L10n.Culture) + "%");
            if (flight.IsPlanning)
                return new ModActivity(L10n.Get("activity.planning"), L10n.Get("planning.pending"), flight.VehicleName);
            if (pending)
                return new ModActivity(L10n.Get("activity.pending"), flight.VehicleName, flightDetail);
            return default;
        }
    }
}
