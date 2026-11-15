using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal readonly struct ModPage
    {
        public readonly int Index;
        public readonly string Key, Icon;
        public ModPage(int index, string key, string icon) { Index = index; Key = key; Icon = icon; }
    }

    /// <summary>One catalog drives both the wrapping launcher and compact app navigation.</summary>
    internal sealed class AppNavigation
    {
        public static readonly ModPage[] Pages =
        {
            new ModPage(0, "tab.flight", "rocket"), new ModPage(1, "tab.plan", "route"),
            new ModPage(2, "tab.automation", "sliders-horizontal"), new ModPage(Rendezvous, "tab.rendezvous", "crosshair"),
            new ModPage(Docking, "tab.docking", "circle-plus"),
            new ModPage(4, "tab.landing", "land-plot"), new ModPage(5, "tab.maneuver", "orbit"),
            new ModPage(Diagnostics, "tab.diagnostics", "info")
        };
        public const int Home = -1, Settings = -2, NoSelection = -3, Rendezvous = 3, Docking = 6, Diagnostics = 7;
        private readonly float[] _expansion = new float[Pages.Length + 1];
        private readonly float[] _hover = new float[Pages.Length + 3];
        private bool _initialized;

        private static float Animate(float current, float target) => current + (target - current)
            * (1 - MathF.Exp(-Math.Clamp(ImGui.GetIO().DeltaTime, 0, 0.05f) * 18));

        public int DrawHeader(int active, ref bool open, PersistentWindowLayout layout)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            float width = ImGui.GetContentRegionAvail().X, button = 30 * scale, gap = 2 * scale;
            float2 origin = ImGui.GetCursorScreenPos();
            float iconSize = 20 * scale;
            float titleX = iconSize + 6 * scale;
            float nameWidth = titleX + ImGui.CalcTextSize("KITN").X + 2 * scale;
            float navigationX = nameWidth + 12 * scale;
            float room = Math.Max(0, width - navigationX - (Pages.Length + 3) * (button + gap));
            var draw = ImGui.GetWindowDrawList();
            // The title itself is the drag handle; navigation controls keep their own hit areas.
            ImGui.InvisibleButton("##kitn-title-drag", new float2(nameWidth, 38 * scale));
            layout?.BeginDrag();
            UiIcons.Draw(draw, "cat", origin + new float2(0, 4 * scale + (button - iconSize) / 2), iconSize,
                ConsoleStyle.ApplyAlpha(GameUiColors.AccentU32));
            draw.AddText(origin + new float2(titleX, 4 * scale + (button - ImGui.GetFontSize()) / 2),
                ConsoleStyle.ApplyAlpha(ConsoleStyle.TextPrimaryU32), "KITN");
            float maxLabel = 0;
            for (int i = 0; i < _expansion.Length; i++)
            {
                bool selected = active == (i == 0 ? Home : Pages[i - 1].Index);
                if (!_initialized) _expansion[i] = selected ? 1 : 0;
                else _expansion[i] = Animate(_expansion[i], selected ? 1 : 0);
                maxLabel = Math.Max(maxLabel, ImGui.CalcTextSize(L10n.Get(i == 0 ? "nav.home" : Pages[i - 1].Key)).X + 10 * scale);
            }
            _initialized = true;
            maxLabel = Math.Min(maxLabel, room);
            float x = navigationX;
            int clicked = NoSelection;
            for (int i = 0; i < _expansion.Length; i++)
            {
                string label = L10n.Get(i == 0 ? "nav.home" : Pages[i - 1].Key);
                int page = i == 0 ? Home : Pages[i - 1].Index;
                float extra = Math.Min(maxLabel, ImGui.CalcTextSize(label).X + 10 * scale) * _expansion[i];
                if (Item(i, i == 0 ? "house" : Pages[i - 1].Icon, label, origin + new float2(x, 4 * scale),
                    new float2(button + extra, button), active == page, _expansion[i])) clicked = page;
                x += button + extra + gap;
            }
            float settingsX = width - 2 * button - gap;
            if (settingsX - x > 0)
            {
                ImGui.SetCursorScreenPos(origin + new float2(x, 0));
                ImGui.InvisibleButton("##kitn-header-drag", new float2(settingsX - x, 38 * scale));
                layout?.BeginDrag();
            }
            if (Item(Pages.Length + 1, "settings", L10n.Get("settings.open"), origin + new float2(settingsX, 4 * scale),
                new float2(button, button), active == Settings, 0)) clicked = Settings;
            if (Item(Pages.Length + 2, null, L10n.Get("nav.minimize"), origin + new float2(width - button, 4 * scale),
                new float2(button, button), false, 0)) open = false;
            draw.AddLine(origin + new float2(0, 37 * scale), origin + new float2(width, 37 * scale),
                ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), Math.Max(1, scale));
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new float2(width, 38 * scale));
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
            if (icon != null)
                UiIcons.Draw(draw, icon, position + new float2(6 * scale, 6 * scale), 18 * scale, color);
            else
            {
                draw.AddLine(position + new float2(10, 10) * scale, position + new float2(20, 20) * scale, color, 1.5f * scale);
                draw.AddLine(position + new float2(20, 10) * scale, position + new float2(10, 20) * scale, color, 1.5f * scale);
            }
            if (expansion > 0.005f)
            {
                draw.PushClipRect(position + new float2(30 * scale, 0), position + size, true);
                var textColor = GameUiColors.Text; textColor.W *= expansion;
                draw.AddText(position + new float2(30 * scale, (size.Y - ImGui.GetFontSize()) / 2), ImGui.ColorConvertFloat4ToU32(textColor), label);
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
                if (ImGui.InvisibleButton("##launcher-" + i, size)) selected = Pages[i].Index;
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
