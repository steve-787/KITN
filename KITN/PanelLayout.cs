using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Native containers separate navigation, page controls and scrollable content.</summary>
    internal static class PanelLayout
    {
        internal readonly struct SectionScope : IDisposable
        {
            public void Dispose() => EndCard();
        }
        public static SectionScope Section(string id, string title = null, string icon = null)
        { BeginCard(id, title, icon, padding: 8, framed: false); return new SectionScope(); }
        public static float4 NavigationColor => Mix(GameUiColors.Panel, GameUiColors.Label, 0.08f);
        public static float4 ControlsColor => Mix(GameUiColors.Panel, GameUiColors.Label, 0.045f);
        public static float4 ContentColor => GameUiColors.Panel;
        private static float4 Mix(float4 a, float4 b, float ratio) => a * (1 - ratio) + b * ratio;

        public static void BeginCard(string id, string title = null, string icon = null, bool controls = false, float padding = 12, bool framed = true)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.PushStyleColor(ImGuiCol.ChildBg, framed ? controls ? ControlsColor : ContentColor : float4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Border, GameUiColors.Hairline);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6 * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(padding, padding) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(12, 10) * scale);
            ImGui.BeginChild("kitn-card-" + id, new float2(0, 0),
                (framed ? ImGuiChildFlags.Borders : ImGuiChildFlags.None) | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysAutoResize | ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            if (title != null) Title(title, icon);
        }

        public static void EndCard()
        {
            ImGui.EndChild(); ImGui.PopStyleVar(3); ImGui.PopStyleColor(2);
        }

        public static void Title(string key, string icon = null)
        {
            ConsoleStyle.PushLabelFont(0.85f);
            try
            {
                if (icon == null) ImGui.TextColored(GameUiColors.Label, L10n.Get(key));
                else UiIcons.Text(icon, L10n.Get(key), GameUiColors.Label);
            }
            finally { ConsoleStyle.PopFont(); }
            ImGui.Separator();
        }

        public static void BeginBody(string id)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.PushStyleColor(ImGuiCol.ChildBg, float4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Border, GameUiColors.Hairline);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6 * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(4, 8) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(12, 16) * scale);
            ImGui.BeginChild(id, new float2(0, 0), ImGuiChildFlags.AlwaysUseWindowPadding);
        }

        public static void EndBody() => EndCard();
    }
}
