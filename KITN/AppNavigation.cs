using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal readonly struct ModPage
    {
        public readonly string Key, Icon;
        public ModPage(string key, string icon) { Key = key; Icon = icon; }
    }

    /// <summary>One catalog drives both the wrapping launcher and compact app navigation.</summary>
    internal sealed class AppNavigation
    {
        public static readonly ModPage[] Pages =
        {
            new ModPage("tab.flight", "rocket"), new ModPage("tab.plan", "route"),
            new ModPage("tab.automation", "sliders-horizontal"), new ModPage("tab.rendezvous", "crosshair"),
            new ModPage("tab.landing", "land-plot"), new ModPage("tab.maneuver", "orbit"),
            new ModPage("tab.diagnostics", "info")
        };
        public const int Home = -1, Settings = -2, NoSelection = -3;
        private readonly float[] _expansion = new float[Pages.Length + 1];
        private readonly float[] _hover = new float[Pages.Length + 2];
        private bool _initialized;

        private static float Animate(float current, float target) => current + (target - current)
            * (1 - MathF.Exp(-Math.Clamp(ImGui.GetIO().DeltaTime, 0, 0.05f) * 18));

        public int Draw(int active, bool launcher)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            float width = ImGui.GetContentRegionAvail().X, button = 34 * scale, gap = 3 * scale;
            float2 origin = ImGui.GetCursorScreenPos();
            float room = Math.Max(0, width - (Pages.Length + 2) * (button + gap) - 8 * scale);
            float maxLabel = 0;
            for (int i = 0; i < _expansion.Length; i++)
            {
                bool selected = active == i - 1;
                if (!_initialized) _expansion[i] = selected ? 1 : 0;
                else _expansion[i] = Animate(_expansion[i], selected ? 1 : 0);
                maxLabel = Math.Max(maxLabel, ImGui.CalcTextSize(L10n.Get(i == 0 ? "nav.home" : Pages[i - 1].Key)).X + 10 * scale);
            }
            _initialized = true;
            maxLabel = Math.Min(maxLabel, room);
            float x = 0;
            int clicked = NoSelection;
            for (int i = 0; i < _expansion.Length; i++)
            {
                if (launcher && i > 0) continue;
                string label = L10n.Get(i == 0 ? "nav.home" : Pages[i - 1].Key);
                float extra = Math.Min(maxLabel, ImGui.CalcTextSize(label).X + 10 * scale) * _expansion[i];
                if (Item(i, i == 0 ? "house" : Pages[i - 1].Icon, label, origin + new float2(x, 0),
                    new float2(button + extra, button), active == i - 1, _expansion[i])) clicked = i - 1;
                x += button + extra + gap;
            }
            if (Item(Pages.Length + 1, "settings", L10n.Get("settings.open"), origin + new float2(width - button, 0),
                new float2(button, button), active == Settings, 0)) clicked = Settings;
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new float2(width, button));
            ImGui.Separator();
            return clicked;
        }

        private bool Item(int id, string icon, string label, float2 position, float2 size, bool active, float expansion)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.SetCursorScreenPos(position);
            bool clicked = ImGui.InvisibleButton("##app-nav-" + id, size);
            _hover[id] = Animate(_hover[id], ImGui.IsItemHovered() ? 1 : 0);
            var draw = ImGui.GetWindowDrawList();
            float4 wash = GameUiColors.Label; wash.W = active ? 0.09f : _hover[id] * 0.07f;
            draw.AddRectFilled(position, position + size, ImGui.ColorConvertFloat4ToU32(wash), 6 * scale);
            uint color = ConsoleStyle.ApplyAlpha(active ? GameUiColors.AccentU32 : ConsoleStyle.TextLabelU32);
            UiIcons.Draw(draw, icon, position + new float2(7 * scale, 7 * scale), 20 * scale, color);
            if (expansion > 0.005f)
            {
                draw.PushClipRect(position + new float2(34 * scale, 0), position + size, true);
                var textColor = GameUiColors.Text; textColor.W *= expansion;
                draw.AddText(position + new float2(34 * scale, (size.Y - ImGui.GetFontSize()) / 2), ImGui.ColorConvertFloat4ToU32(textColor), label);
                draw.PopClipRect();
            }
            if (active) draw.AddRectFilled(position + new float2(8 * scale, size.Y - 2 * scale),
                position + new float2(size.X - 8 * scale, size.Y), color, scale);
            ModWindowStyle.Hint(label);
            return clicked;
        }

        public int Launcher()
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            UiIcons.Text("orbit", L10n.Get("nav.launcher"), GameUiColors.Text);
            ImGui.Dummy(new float2(0, 8 * scale));
            float width = ImGui.GetContentRegionAvail().X, gap = 12 * scale;
            int columns = Math.Max(1, (int)((width + gap) / (210 * scale + gap)));
            float cardWidth = (width - gap * (columns - 1)) / columns;
            float2 origin = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            int selected = NoSelection;
            for (int i = 0; i < Pages.Length; i++)
            {
                float2 p = origin + new float2(i % columns * (cardWidth + gap), i / columns * (110 * scale + gap));
                float2 size = new float2(cardWidth, 110 * scale);
                ImGui.SetCursorScreenPos(p);
                if (ImGui.InvisibleButton("##launcher-" + i, size)) selected = i;
                _hover[i] = Animate(_hover[i], ImGui.IsItemHovered() ? 1 : 0);
                var bg = GameUiColors.Inset + (GameUiColors.Frame - GameUiColors.Inset) * (_hover[i] * 0.35f);
                draw.AddRectFilled(p, p + size, ImGui.ColorConvertFloat4ToU32(bg), 8 * scale);
                var border = GameUiColors.Hairline + (GameUiColors.Label - GameUiColors.Hairline) * (_hover[i] * 0.4f);
                draw.AddRect(p, p + size, ImGui.ColorConvertFloat4ToU32(border), 8 * scale);
                UiIcons.Draw(draw, Pages[i].Icon, p + new float2(18, 18) * scale, 30 * scale, ConsoleStyle.ApplyAlpha(GameUiColors.AccentU32));
                draw.AddText(p + new float2(18, 72) * scale, ConsoleStyle.ApplyAlpha(ConsoleStyle.TextPrimaryU32), L10n.Get(Pages[i].Key));
                UiIcons.Draw(draw, "arrow-up-right", p + new float2(cardWidth - 34 * scale, 20 * scale), 16 * scale, ConsoleStyle.ApplyAlpha(ConsoleStyle.TextMutedU32));
                ModWindowStyle.Hint(L10n.Get(Pages[i].Key));
            }
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new float2(width, ((Pages.Length + columns - 1) / columns) * (110 * scale + gap)));
            return selected;
        }
    }
}
