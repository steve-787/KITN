using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal static class LauncherShortcut
    {
        public static bool Draw()
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.SetNextWindowPos(new float2(12 * scale, ImGui.GetIO().DisplaySize.Y - 56 * scale), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new float2(40, 40) * scale, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(3, 3) * scale);
            bool clicked = false;
            bool visible = ImGui.Begin("###KITNLauncherShortcut", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground);
            if (visible)
            {
                float2 pos = ImGui.GetCursorScreenPos();
                clicked = ImGui.InvisibleButton("##open-kitn", new float2(34, 34) * scale);
                ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new float2(34, 34) * scale,
                    ConsoleStyle.ApplyAlpha(ImGui.IsItemHovered() ? ConsoleStyle.FrameU32 : ConsoleStyle.PanelU32), 6 * scale);
                UiIcons.Draw(ImGui.GetWindowDrawList(), "house", pos + new float2(7, 7) * scale, 20 * scale, ConsoleStyle.ApplyAlpha(GameUiColors.AccentU32));
                ModWindowStyle.Hint(L10n.Get("nav.open_launcher"));
            }
            ImGui.End(); ImGui.PopStyleVar();
            return clicked;
        }
    }
}
