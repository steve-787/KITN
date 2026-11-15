using System;
using Brutal.ImGuiApi;
using KSA;

namespace KITN
{
    internal static class SettingsSlider
    {
        public static void Float(string key, ref float value, float min, float max, string format, string unit = "")
        {
            string Text(float number) => number.ToString(format, L10n.Culture)
                + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
            string current = Text(value), lower = Text(min), upper = Text(max), widest = current;
            ConsoleStyle.PushValueFont();
            try
            {
                float width = ImGui.CalcTextSize(widest).X;
                float lowerWidth = ImGui.CalcTextSize(lower).X;
                if (lowerWidth > width) { widest = lower; width = lowerWidth; }
                if (ImGui.CalcTextSize(upper).X > width) widest = upper;
            }
            finally { ConsoleStyle.PopFont(); }
            // Reserve the complete readout across the range, including units,
            // grouping separators and minus signs. The native control keeps its gap.
            ConsoleWidgets.SliderFloat(key.AsSpan(), ref value, min, max, current.AsSpan(), widest.AsSpan(), false);
        }
    }
}
