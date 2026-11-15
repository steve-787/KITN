using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class MissionTimeWindow
    {
        private readonly InstrumentReadout _time = new InstrumentReadout(), _altitude = new InstrumentReadout();
        public PersistentWindowLayout Layout { get; set; }
        public UiLayoutStore Settings { get; set; }
        public MissionAltitudeDisplay Altitude { get; set; }
        public bool IsVisible { get; private set; }
        public bool IsChromeVisible { get; private set; }

        public string Draw(ref bool open, MissionClock clock, LaunchProfile profile,
            AscentGuidance.AscentState state, bool actionPending, LandingController landing = null, FlightDisplay flight = null)
        {
            string action = null;
            IsVisible = false;
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.SetNextWindowSizeConstraints(new float2(144, 108) * scale, new float2(1000, 600) * scale);
            ImGuiCond condition = ImGuiCond.FirstUseEver;
            float2 size = new float2(400, 272) * scale;
            if (Layout != null) size = Layout.Prepare(size, scale, out condition);
            UiWindowBounds bounds = Layout?.Current;
            float2 mouse = ImGui.GetIO().MousePos;
            bool hover = bounds != null && mouse.X >= bounds.X && mouse.X < bounds.X + bounds.Width
                && mouse.Y >= bounds.Y && mouse.Y < bounds.Y + bounds.Height;
            IsChromeVisible = Settings?.AutoHideInstrumentFrame != true || hover
                || (Layout?.IsInteracting == true && ImGui.IsMouseDown(ImGuiMouseButton.Left));
            if (!ModWindowStyle.Begin("KITNMissionTime", L10n.Get("window.instruments"), ref open, size, condition,
                !IsChromeVisible, true, detached: Layout?.Detached == true)) return null;
            try
            {
                IsVisible = true;
                Layout?.Capture(scale);
                UiTheme theme = Settings?.Theme ?? UiTheme.Retro;
                string display = clock.Mode == MissionClock.ClockMode.Ready
                    ? MissionClock.Format(false, profile.LaunchCountdownSeconds) : clock.Display;
                float2 windowSize = ImGui.GetWindowSize();
                float compact = Math.Clamp(Math.Min(windowSize.X / (400 * scale), windowSize.Y / (272 * scale)), 0.5f, 1);
                ConsoleStyle.PushLabelFont(compact);
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(6 * scale, 4 * scale * compact));
                try
                {
                    float footerHeight = ImGui.GetFontSize() + 6 * scale;
                    float rowHeight = Math.Max(4 * scale, (ImGui.GetContentRegionAvail().Y - footerHeight - 12 * scale * compact - 1) / 2);
                    _time.Draw(display, theme, rowHeight);
                    if (IsChromeVisible) ImGui.Separator();
                    else ImGui.Dummy(new float2(ImGui.GetContentRegionAvail().X, 1));
                    bool hasAltitude = flight?.HasVehicle == true && double.IsFinite(flight.AltitudeMeters);
                    if (hasAltitude) Altitude?.Draw(); else Altitude?.Release();
                    float altitudeLift = Math.Min(20 * scale, rowHeight * 0.15f
                        + Math.Max(0, rowHeight - 52 * scale) * 0.2f);
                    _altitude.Draw(InstrumentReadout.Altitude(hasAltitude ? flight.AltitudeMeters : double.NaN, theme),
                        theme, rowHeight, -altitudeLift);
                    string status = clock.Mode switch
                    {
                        MissionClock.ClockMode.Ready => L10n.Get("clock.ready"),
                        MissionClock.ClockMode.Countdown => L10n.Get("clock.countdown"),
                        MissionClock.ClockMode.Cancelled => L10n.Get("clock.cancelled"),
                        MissionClock.ClockMode.Stopped => L10n.Get("clock.stopped"),
                        _ => landing != null && (landing.IsActive || landing.State == LandingState.Landed || landing.State == LandingState.Aborted)
                            ? landing.Phase : AutopilotWindow.StateName(state)
                    };
                    if (IsChromeVisible && clock.IsCountingDown)
                    {
                        ImGui.BeginDisabled(actionPending);
                        try
                        {
                            ImGui.PushStyleColor(ImGuiCol.Button, GameUiColors.Inset);
                            ImGui.PushStyleColor(ImGuiCol.Border, ConsoleStyle.Danger);
                            ImGui.PushStyleColor(ImGuiCol.Text, ConsoleStyle.Danger);
                            try
                            {
                                if (ImGui.Button(L10n.Id("action.cancel_countdown"), new float2(-1, footerHeight))) action = "cancel_countdown";
                            }
                            finally { ImGui.PopStyleColor(3); }
                        }
                        finally { ImGui.EndDisabled(); }
                    }
                    else if (IsChromeVisible) ImGui.TextColored(GameUiColors.Message(clock.Mode == MissionClock.ClockMode.Flight
                            && MessageSeverity.IsError(landing != null && (landing.IsActive || landing.State == LandingState.Landed || landing.State == LandingState.Aborted)
                                ? landing.MessageKey : profile.StatusKey)),
                        hasAltitude && flight.AltitudeBodyName != "—" ? flight.AltitudeBodyName + " · " + status : status);
                    if (IsChromeVisible) ModWindowStyle.Hint(L10n.Get("settings.alignment_hint"));
                }
                finally { ImGui.PopStyleVar(); ConsoleStyle.PopFont(); }
            }
            finally { ModWindowStyle.End(); }
            return action;
        }
    }
}
