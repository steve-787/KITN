using System;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>One instrument face for altitude and time, with shared spacing and digit treatment.</summary>
    internal sealed class InstrumentReadout
    {
        private string _value, _previous;
        private long _changedAt;

        public void Draw(string value, UiTheme theme, float height, float verticalOffset = 0)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            float2 origin = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var draw = ImGui.GetWindowDrawList();
            float verticalPadding = theme == UiTheme.Modern && height >= 52 * scale ? 22 * scale : 0;
            float2 face = origin + new float2(0, verticalPadding / 2 + verticalOffset);
            float faceHeight = Math.Max(4 * scale, height - verticalPadding);
            if (theme == UiTheme.Modern)
            {
                ConsoleStyle.PushLabelFont(Math.Min(faceHeight * 0.74f / ConsoleStyle.InterfaceFontSize,
                    width / Math.Max(1, value.Length * ConsoleStyle.InterfaceFontSize * 0.65f)));
                try
                {
                    float2 textSize = ImGui.CalcTextSize(value);
                    draw.AddText(face + new float2((width - textSize.X) / 2, (faceHeight - textSize.Y) / 2), ConsoleStyle.TextPrimaryU32, value);
                }
                finally { ConsoleStyle.PopFont(); }
            }
            else
            {
                float drumHeight = Math.Min(faceHeight, width / 9 * 1.65f);
                Ticker(face + new float2(0, (faceHeight - drumHeight) / 2), width, drumHeight, value);
            }
            ImGui.Dummy(new float2(width, height));
        }

        private void Ticker(float2 origin, float width, float height, string value)
        {
            if (value != _value)
            { _previous = _value; _value = value; _changedAt = Environment.TickCount64; }
            float progress = Math.Clamp((Environment.TickCount64 - _changedAt) / 180f, 0, 1);
            float eased = 1 - MathF.Pow(1 - progress, 3);
            var draw = ImGui.GetWindowDrawList();
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            float weight = 0;
            for (int i = 0; i < value.Length; i++)
                weight += value[i] == ':' ? 0.32f : 1;
            // The two instruments use the same physical drum size.
            float cell = Math.Min(width / Math.Max(9, weight), height * 0.66f);
            float x = origin.X + (width - cell * weight) / 2;
            draw.AddRectFilled(new float2(x, origin.Y), new float2(x + cell * weight, origin.Y + height), ConsoleStyle.InsetU32, 3 * scale);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                bool digit = char.IsDigit(ch);
                float w = cell * (ch == ':' ? 0.32f : 1);
                float2 min = new float2(x + scale, origin.Y), max = new float2(x + w - scale, origin.Y + height);
                if (digit)
                {
                    draw.AddRectFilled(min, max, ConsoleStyle.InsetU32, 3 * scale);
                    draw.AddRectFilledMultiColor(min, max, ConsoleStyle.TopHighlightU32, ConsoleStyle.TopHighlightU32,
                        ConsoleStyle.BottomShadeU32, ConsoleStyle.BottomShadeU32);
                    draw.PushClipRect(min + new float2(0, 2 * scale), max - new float2(0, 2 * scale), true);
                    ConsoleStyle.PushGaugeFont(Math.Min(height * 0.8f, cell * 1.6f));
                    try
                    {
                        string text = ch.ToString();
                        float2 textSize = ImGui.CalcTextSize(text);
                        ConsoleStyle.GetGaugeInkExtents(ImGui.GetFontSize(), out float inkTop, out float inkHeight);
                        float2 center = new float2(x + (w - textSize.X) / 2, origin.Y + (height - inkHeight) / 2 - inkTop);
                        float step = height * 0.76f;
                        draw.AddText(center - new float2(0, step), ConsoleStyle.TextMutedU32, ((ch - '0' + 9) % 10).ToString());
                        draw.AddText(center + new float2(0, step), ConsoleStyle.TextMutedU32, ((ch - '0' + 1) % 10).ToString());
                        char old = _previous?.Length == value.Length ? _previous[i] : ch;
                        if (old != ch && progress < 1)
                        {
                            draw.AddText(center - new float2(0, height * eased), ConsoleStyle.TextPrimaryU32, old.ToString());
                            draw.AddText(center + new float2(0, height * (1 - eased)), ConsoleStyle.TextPrimaryU32, text);
                        }
                        else draw.AddText(center, ConsoleStyle.TextPrimaryU32, text);
                    }
                    finally { ConsoleStyle.PopFont(); draw.PopClipRect(); }
                }
                else
                {
                    string text = ch.ToString();
                    bool cap = ch != ':';
                    if (cap)
                    {
                        min.X = x; max.X = x + w - scale;
                        draw.AddRectFilled(min, max, ConsoleStyle.TextPrimaryU32, 3 * scale,
                            i == 0 ? ImDrawFlags.RoundCornersLeft : i == value.Length - 1 ? ImDrawFlags.RoundCornersRight : ImDrawFlags.RoundCornersNone);
                    }
                    draw.PushClipRect(min, max, true);
                    ConsoleStyle.PushLabelFont(Math.Min(height * (cap ? 0.95f : 0.64f), w * (cap ? 1.65f : 1.1f)) / ConsoleStyle.InterfaceFontSize);
                    try
                    {
                        float2 textSize = ImGui.CalcTextSize(text);
                        float2 center = new float2(x + (w - textSize.X) / 2, origin.Y + (height - textSize.Y) / 2);
                        uint color = cap ? ConsoleStyle.InsetU32 : ConsoleStyle.TextLabelU32;
                        draw.AddText(center, color, text);
                        if (cap)
                        {
                            string neighbor = ch == '+' ? "-" : ch == '-' ? "+" : text;
                            draw.AddText(center - new float2(0, height * 0.68f), color, neighbor);
                            draw.AddText(center + new float2(0, height * 0.68f), color, neighbor);
                        }
                    }
                    finally { ConsoleStyle.PopFont(); draw.PopClipRect(); }
                    if (cap)
                    {
                        float2 midLeft = new float2(min.X, (min.Y + max.Y) / 2), midRight = new float2(max.X, (min.Y + max.Y) / 2);
                        draw.AddRectFilledMultiColor(min, midRight, ConsoleStyle.BottomShadeU32, ConsoleStyle.BottomShadeU32, 0, 0);
                        draw.AddRectFilledMultiColor(midLeft, max, 0, 0, ConsoleStyle.BottomShadeU32, ConsoleStyle.BottomShadeU32);
                    }
                }
                x += w;
            }
        }

        public static string Altitude(double meters, UiTheme theme)
        {
            if (!double.IsFinite(meters)) return "—";
            if (theme == UiTheme.Modern) return Math.Round(meters).ToString("N0", L10n.Culture) + " m";
            double value = Math.Clamp(Math.Abs(Math.Round(meters)), 0, 99999999);
            return (meters < 0 ? "-" : "") + value.ToString("00000000", CultureInfo.InvariantCulture) + "M";
        }
    }
}
