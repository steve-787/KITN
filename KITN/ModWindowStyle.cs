using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Game palette, fonts and controls with a thin frame and compact native title bar.</summary>
    internal static class ModWindowStyle
    {
        public static bool Begin(string id, string title, ref bool open, float2 size, ImGuiCond condition,
            bool hideChrome = false, bool compact = false)
        {
            ConsoleStyle.PushWidgetStyle();
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.PushStyleColor(ImGuiCol.WindowBg, hideChrome ? float4.Zero : GameUiColors.Panel);
            ImGui.PushStyleColor(ImGuiCol.TitleBg, hideChrome ? float4.Zero : GameUiColors.Panel);
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, hideChrome ? float4.Zero : GameUiColors.Panel);
            ImGui.PushStyleColor(ImGuiCol.Border, hideChrome ? float4.Zero : GameUiColors.Hairline);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, hideChrome ? 0 : Math.Max(1, scale));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6 * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, (compact ? new float2(8, 6) : new float2(16, 12)) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(8, 8) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new float2(6, 3) * scale);
            size = ConsoleStyle.ConstrainNextWindowToScreen(size);
            ImGui.SetNextWindowSize(size, condition);
            if (hideChrome)
            {
                // Keep the native title/resize hit areas and layout intact while making
                // decorations invisible. Hovering never shifts the instruments.
                foreach (ImGuiCol color in new[] { ImGuiCol.Text, ImGuiCol.ButtonHovered, ImGuiCol.ButtonActive,
                    ImGuiCol.ResizeGrip, ImGuiCol.ResizeGripHovered, ImGuiCol.ResizeGripActive })
                    ImGui.PushStyleColor(color, float4.Zero);
            }
            bool visible = ImGui.Begin(title + "###" + id, ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                | (hideChrome ? ImGuiWindowFlags.NoBackground : ImGuiWindowFlags.None));
            if (hideChrome) ImGui.PopStyleColor(6);
            if (!visible) End();
            return visible;
        }

        public static void End()
        {
            ImGui.End();
            ImGui.PopStyleVar(5);
            ImGui.PopStyleColor(4);
            ConsoleStyle.PopWidgetStyle();
        }

        public static void Hint(string text)
        {
            if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) || !ImGui.BeginTooltip()) return;
            try
            {
                ImGui.PushTextWrapPos(Math.Min(450 * ConsoleStyle.EffectiveInterfaceScale, Math.Max(180, ImGui.GetIO().DisplaySize.X - 40)));
                try { ImGui.TextWrapped(text); }
                finally { ImGui.PopTextWrapPos(); }
            }
            finally { ImGui.EndTooltip(); }
        }
    }
}
