using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSAAutopilot
{
    internal sealed class MissionTimeWindow
    {
        public PersistentWindowLayout Layout { get; set; }
        public MissionAltitudeDisplay Altitude { get; set; }
        public bool IsVisible { get; private set; }
        public string Draw(ref bool open, MissionClock clock, LaunchProfile profile,
            AscentGuidance.AscentState state, bool actionPending, LandingController landing = null)
        {
            string action = null;
            IsVisible = false;
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.SetNextWindowSizeConstraints(new float2(300, 210) * scale, new float2(1000, 580) * scale);
            ImGuiCond sizeCondition = ImGuiCond.FirstUseEver;
            float2 size = new float2(380, 240) * scale;
            if (Layout != null) size = Layout.Prepare(size, scale, out sizeCondition);
            if (!ConsoleStyle.BeginWindow("KSAAutopilotMissionTime".AsSpan(), L10n.Get("window.mission_time").AsSpan(), "KSA-T".AsSpan(),
                ref open, size, ImGuiWindowFlags.NoFocusOnAppearing, sizeCondition)) return null;
            try
            {
                IsVisible = true;
                Layout?.Capture(scale);
                ConsoleStyle.BeginBody();
                try
                {
                    ConsoleStyle.PushWidgetStyle();
                    try
                    {
                        string display = clock.Mode == MissionClock.ClockMode.Ready
                            ? MissionClock.Format(false, profile.LaunchCountdownSeconds) : clock.Display;
                        float availableWidth = ImGui.GetContentRegionAvail().X;
                        // The numeric gauge font lacks +/- and colons. Use the game's interface
                        // font for the complete readout so T- / T+ and HH:MM:SS stay visible.
                        ConsoleStyle.PushLabelFont(Math.Clamp(availableWidth / Math.Max(1, ConsoleStyle.InterfaceFontSize * 7), 1.25f, 2.75f));
                        try
                        {
                            float width = ImGui.CalcTextSize(display).X;
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (availableWidth - width) * 0.5f));
                            ImGui.TextColored(GameUiColors.Text, display);
                        }
                        finally { ConsoleStyle.PopFont(); }
                        if (Altitude != null)
                        {
                            ImGui.Dummy(new float2(0, 6 * scale));
                            if (!Altitude.Draw()) ImGui.TextColored(GameUiColors.Muted, L10n.Get("clock.altitude_unavailable"));
                        }
                        if (clock.IsCountingDown)
                        {
                            ImGui.BeginDisabled(actionPending);
                            try
                            {
                                if (ConsoleWidgets.DangerButton(L10n.Get("action.cancel_countdown").AsSpan(),
                                    "action.cancel_countdown".AsSpan(), float2.Zero)) action = "cancel_countdown";
                            }
                            finally { ImGui.EndDisabled(); }
                        }
                    }
                    finally { ConsoleStyle.PopWidgetStyle(); }
                }
                finally { ConsoleStyle.EndBody(); }
                ConsoleStyle.BeginFooter();
                try
                {
                    string status = clock.Mode switch
                    {
                        MissionClock.ClockMode.Ready => L10n.Get("clock.ready"),
                        MissionClock.ClockMode.Countdown => L10n.Get("clock.countdown"),
                        MissionClock.ClockMode.Cancelled => L10n.Get("clock.cancelled"),
                        MissionClock.ClockMode.Stopped => L10n.Get("clock.stopped"),
                        _ => landing != null && (landing.IsActive || landing.State == LandingState.Landed || landing.State == LandingState.Aborted)
                            ? landing.Phase : AutopilotWindow.StateName(state)
                    };
                    ConsoleStyle.FooterStatus(status.AsSpan(), clock.IsCountingDown);
                }
                finally { ConsoleStyle.EndFooter(); }
            }
            finally { ConsoleStyle.EndWindow(); }
            return action;
        }
    }
}
