using Brutal.Numerics;
using KSA;

namespace KSAAutopilot
{
    internal static class GameUiColors
    {
        public static float4 Text => Unpack(ConsoleStyle.TextPrimaryU32);
        public static float4 Label => Unpack(ConsoleStyle.TextLabelU32);
        public static float4 Muted => Unpack(ConsoleStyle.TextMutedU32);
        public static float4 Pending => Unpack(ConsoleStyle.PendingU32);
        public static float4 Panel => Unpack(ConsoleStyle.PanelU32);
        public static float4 Inset => Unpack(ConsoleStyle.InsetU32);
        public static float4 Hairline => Unpack(ConsoleStyle.HairlineU32);
        public static float4 Frame => Unpack(ConsoleStyle.FrameU32);
        private static float4 Unpack(uint color) => new float4((color & 255) / 255f,
            (color >> 8 & 255) / 255f, (color >> 16 & 255) / 255f, (color >> 24) / 255f);
    }
}
