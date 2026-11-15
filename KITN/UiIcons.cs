using System;
using System.Collections.Generic;
using System.IO;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Lucide vectors, flattened once at build time and drawn with the game's native draw list.</summary>
    internal static class UiIcons
    {
        private static readonly Dictionary<string, float2[][]> Paths = Load();
        private static Dictionary<string, float2[][]> Load()
        {
            var icons = new Dictionary<string, float2[][]>(StringComparer.Ordinal);
            using var stream = typeof(UiIcons).Assembly.GetManifestResourceStream("KITN.Lucide");
            using var reader = new BinaryReader(stream);
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                var paths = new float2[reader.ReadInt32()][];
                for (int p = 0; p < paths.Length; p++)
                {
                    paths[p] = new float2[reader.ReadInt32()];
                    for (int v = 0; v < paths[p].Length; v++) paths[p][v] = new float2(reader.ReadSingle(), reader.ReadSingle());
                }
                icons.Add(name, paths);
            }
            return icons;
        }

        public static void Draw(ImDrawListPtr draw, string name, float2 position, float size, uint color)
        {
            if (!Paths.TryGetValue(name, out var paths)) return;
            float factor = size / 24;
            float thickness = Math.Max(1, size / 13);
            foreach (var path in paths)
            {
                foreach (var vertex in path) draw.PathLineTo(position + vertex * factor);
                draw.PathStroke(color, ImDrawFlags.None, thickness);
            }
        }

        public static string ForAction(string key) => key switch
        {
            "transfer.calculate" => "calculator",
            "transfer.improve" or "transfer.lowest" or "transfer.earliest" or "transfer.oberth_lowest" => "search",
            "transfer.create" => "circle-plus",
            "transfer.execute" or "transfer.execute_capture" or "action.countdown" or "landing.start" or "rendezvous.dock" or "rendezvous.start" => "play",
            "action.reset" => "rotate-ccw",
            "settings.open" => "settings",
            "diagnostics.open" => "sliders-horizontal",
            "transfer.stop" or "landing.abort" or "rendezvous.stop" or "action.abort" or "action.cancel_countdown" => "square",
            _ => "chevron-right"
        };

        public static float ButtonWidth(string key) => ImGui.CalcTextSize(L10n.Get(key)).X + 48 * ConsoleStyle.EffectiveInterfaceScale;

        public static bool Button(string key, bool primary = false, bool danger = false)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            float2 origin = ImGui.GetCursorScreenPos();
            float2 size = new float2(ButtonWidth(key), ConsoleStyle.FrameHeightPx);
            bool clicked = ImGui.InvisibleButton("##" + key, size);
            bool hover = ImGui.IsItemHovered();
            var draw = ImGui.GetWindowDrawList();
            uint accent = danger ? ImGui.ColorConvertFloat4ToU32(ConsoleStyle.Danger) : GameUiColors.AccentU32;
            var hoverTint = danger ? ConsoleStyle.Danger : GameUiColors.Accent;
            hoverTint.W = 0.12f;
            uint bg = primary ? accent : hover ? ImGui.ColorConvertFloat4ToU32(hoverTint) : ConsoleStyle.InsetU32;
            uint fg = primary ? ConsoleStyle.PanelU32 : danger ? accent : ConsoleStyle.TextPrimaryU32;
            draw.AddRectFilled(origin, origin + size, ConsoleStyle.ApplyAlpha(bg), 5 * scale);
            draw.AddRect(origin, origin + size, ConsoleStyle.ApplyAlpha(primary || danger ? accent : ConsoleStyle.FrameU32), 5 * scale);
            Draw(draw, ForAction(key), origin + new float2(10 * scale, (size.Y - 18 * scale) / 2), 18 * scale, ConsoleStyle.ApplyAlpha(fg));
            draw.AddText(origin + new float2(34 * scale, (size.Y - ImGui.GetFontSize()) / 2), ConsoleStyle.ApplyAlpha(fg), L10n.Get(key));
            return clicked;
        }

        public static void Text(string icon, string text, float4 color)
        {
            float size = ImGui.GetFontSize();
            Draw(ImGui.GetWindowDrawList(), icon, ImGui.GetCursorScreenPos(), size, ConsoleStyle.ApplyAlpha(ImGui.ColorConvertFloat4ToU32(color)));
            ImGui.Dummy(new float2(size, size));
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            try { ImGui.TextWrapped(text); }
            finally { ImGui.PopStyleColor(); }
        }
    }
}
