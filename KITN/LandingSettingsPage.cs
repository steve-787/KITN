using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal static class LandingSettingsPage
    {
        public static void Draw(LandingController landing, UiLayoutStore store, bool locked)
        {
            ImGui.BeginDisabled(locked);
            try
            {
                if (!ImGui.CollapsingHeader(L10n.Get("landing.settings") + "###landing-settings")) return;
                var settings = landing.Settings.Copy();
                settings.TouchdownSpeedMps = Slider("landing.touchdown_speed", settings.TouchdownSpeedMps, 0.2f, 1);
                settings.BrakingMargin = Slider("landing.braking_margin", settings.BrakingMargin, 1.1f, 3);
                settings.HandoverHeightM = Slider("landing.handover_height", settings.HandoverHeightM, 1000, 30000);
                settings.TerminalHorizonSeconds = Slider("landing.terminal_horizon", settings.TerminalHorizonSeconds, 10, 30);
                settings.MaximumTiltDeg = Slider("landing.maximum_tilt", settings.MaximumTiltDeg, 10, 85);
                settings.MaximumTurnRateDegS = Slider("landing.turn_rate", settings.MaximumTurnRateDegS, 2, 15);
                if (ImGui.CollapsingHeader(L10n.Get("landing.fuel_model") + "###landing-fuel-model"))
                {
                    settings.ReservePercent = Slider("landing.reserve_percent", settings.ReservePercent, 10, 100);
                    settings.ReserveExtraMps = Slider("landing.reserve_extra", settings.ReserveExtraMps, 0, 2000);
                    settings.DragAreaScale = Slider("landing.drag_scale", settings.DragAreaScale, 0.25f, 4);
                    settings.SearchWindowHours = Slider("landing.search_hours", settings.SearchWindowHours, 1, 168);
                    bool adaptive = settings.AdaptiveDrag;
                    ConsoleWidgets.BeginRow(L10n.Get("landing.adaptive_drag").AsSpan());
                    try { ConsoleWidgets.Checkbox("landing.adaptive_drag".AsSpan(), ref adaptive, false); }
                    finally { ConsoleWidgets.EndRow(); }
                    settings.AdaptiveDrag = adaptive;
                }
                if (ConsoleWidgets.Button(L10n.Get("landing.defaults").AsSpan(), "landing.defaults".AsSpan(), float2.Zero))
                { settings = new LandingSettings(); landing.AutoWarp = settings.AutoWarp; }
                settings.AutoWarp = landing.AutoWarp;
                if (settings.IsValid && !landing.Settings.Matches(settings))
                {
                    landing.Settings = settings; store?.SetLanding(settings, Environment.TickCount64);
                }
            }
            finally { ImGui.EndDisabled(); }
        }

        private static float Slider(string key, float value, float low, float high)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try
            {
                ConsoleWidgets.SliderFloat(key.AsSpan(), ref value, low, high,
                    value.ToString(high > 100 ? "N0" : "0.0", L10n.Culture).AsSpan(), false);
                return value;
            }
            finally { ConsoleWidgets.EndRow(); }
        }
    }
}
