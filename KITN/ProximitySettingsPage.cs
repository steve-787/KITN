using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal static class ProximitySettingsPage
    {
        public static void Rendezvous(RendezvousController controller, UiLayoutStore store, bool locked)
        {
            if (!ImGui.CollapsingHeader(L10n.Get("rendezvous.settings") + "###rendezvous-settings")) return;
            ImGui.BeginDisabled(locked || controller.IsActive);
            try
            {
                var s = controller.RendezvousOptions.Copy();
                s.FinalDistanceM = Slider("rv.final_distance", s.FinalDistanceM, 50, 1000, "m");
                s.MaximumClosingSpeedMps = Slider("proximity.max_speed", s.MaximumClosingSpeedMps, 0.1f, 20, "m/s");
                s.MaximumPhasingOrbits = Integer("rv.phasing_orbits", s.MaximumPhasingOrbits, 1, 10);
                s.AutoWarp = Toggle("rv.auto_warp", s.AutoWarp);
                if (ImGui.CollapsingHeader(L10n.Get("proximity.advanced") + "###rendezvous-advanced"))
                {
                    s.BrakingMargin = Slider("proximity.braking_margin", s.BrakingMargin, 1.2f, 5, "×");
                    s.PositionToleranceM = Slider("rv.position_tolerance", s.PositionToleranceM, 1, 20, "m");
                    s.VelocityToleranceMps = Slider("rv.velocity_tolerance", s.VelocityToleranceMps, 0.01f, 0.2f, "m/s");
                    s.SettleSeconds = Slider("rv.settle_time", s.SettleSeconds, 1, 10, "s");
                    s.ReservePercent = Slider("rv.reserve", s.ReservePercent, 0, 50, "%");
                    s.WarpLeadSeconds = Slider("rv.warp_lead", s.WarpLeadSeconds, 60, 600, "s");
                    s.CourseCorrections = Toggle("rv.corrections", s.CourseCorrections);
                    ImGui.BeginDisabled(!s.CourseCorrections);
                    try { s.MaximumCorrections = Integer("rv.correction_count", s.MaximumCorrections, 1, 10); }
                    finally { ImGui.EndDisabled(); }
                }
                ModWindowStyle.Hint(L10n.Get("rv.settings_hint"));
                if (Defaults("rendezvous")) s = new RendezvousSettings();
                if (s.IsValid && !controller.RendezvousOptions.Matches(s))
                { controller.RendezvousOptions = s; store?.SetRendezvous(s, Environment.TickCount64); }
            }
            finally { ImGui.EndDisabled(); }
        }

        public static void Docking(RendezvousController controller, UiLayoutStore store, bool locked)
        {
            if (!ImGui.CollapsingHeader(L10n.Get("docking.settings") + "###docking-settings")) return;
            ImGui.BeginDisabled(locked || controller.IsActive);
            try
            {
                var s = controller.DockingOptions.Copy();
                s.MaximumSpeedMps = Slider("proximity.max_speed", s.MaximumSpeedMps, 0.05f, 5, "m/s");
                s.NearSpeedMps = Slider("dock.near_speed", s.NearSpeedMps, 0.02f, 1, "m/s");
                s.ContactSpeedMps = Slider("dock.contact_speed", s.ContactSpeedMps, 0.01f, 0.2f, "m/s");
                s.EngineAssist = Toggle("dock.engine_assist", s.EngineAssist);
                if (s.EngineAssist) ModWindowStyle.Hint(L10n.Get("dock.engine_assist_hint"));
                s.SafetyMarginM = Slider("dock.safety_margin", s.SafetyMarginM, 5, 100, "m");
                s.OverrideStartDistance = Toggle("dock.override_start", s.OverrideStartDistance);
                ImGui.BeginDisabled(!s.OverrideStartDistance);
                try { s.StartDistanceM = Slider("dock.start_distance", s.StartDistanceM, 10, 500, "m"); }
                finally { ImGui.EndDisabled(); }
                s.ForceRoll = Toggle("dock.force_roll", s.ForceRoll);
                ImGui.BeginDisabled(!s.ForceRoll);
                try { s.RollAngleDeg = Slider("dock.roll_angle", s.RollAngleDeg, -180, 180, "°"); }
                finally { ImGui.EndDisabled(); }
                if (ImGui.CollapsingHeader(L10n.Get("proximity.advanced") + "###docking-advanced"))
                {
                    s.AlignmentSpeedMps = Slider("dock.alignment_speed", s.AlignmentSpeedMps, 0.05f, 2, "m/s");
                    s.MaximumTurnRateDegS = Slider("dock.turn_rate", s.MaximumTurnRateDegS, 0.1f, 3, "°/s");
                    s.AlignmentToleranceDeg = Slider("dock.angle_tolerance", s.AlignmentToleranceDeg, 0.5f, 5, "°");
                    s.MaximumAngularRateDegS = Slider("dock.angular_tolerance", s.MaximumAngularRateDegS, 0.05f, 1, "°/s");
                    s.CorridorRadiusM = Slider("dock.corridor", s.CorridorRadiusM, 0.05f, 0.5f, "m");
                    s.BrakingMargin = Slider("proximity.braking_margin", s.BrakingMargin, 1.2f, 5, "×");
                }
                ModWindowStyle.Hint(L10n.Get("dock.settings_hint"));
                if (Defaults("docking")) s = new DockingSettings();
                if (s.IsValid && !controller.DockingOptions.Matches(s))
                { controller.DockingOptions = s; store?.SetDocking(s, Environment.TickCount64); }
            }
            finally { ImGui.EndDisabled(); }
        }

        private static bool Defaults(string id) => ConsoleWidgets.Button(L10n.Get("proximity.defaults").AsSpan(), (id + ".defaults").AsSpan(), float2.Zero);
        private static bool Toggle(string key, bool value)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try { ConsoleWidgets.Checkbox(key.AsSpan(), ref value, false); return value; }
            finally { ConsoleWidgets.EndRow(); }
        }
        private static int Integer(string key, int value, int low, int high)
            => (int)Math.Round(Slider(key, value, low, high, "", true));
        private static float Slider(string key, float value, float low, float high, string unit, bool integer = false)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try
            {
                string format = integer || high >= 50 ? "N0" : high <= 1 ? "0.00" : "0.0";
                ConsoleWidgets.SliderFloat(key.AsSpan(), ref value, low, high, (value.ToString(format, L10n.Culture) + " " + unit).AsSpan(), false);
                return value;
            }
            finally { ConsoleWidgets.EndRow(); }
        }
    }
}
