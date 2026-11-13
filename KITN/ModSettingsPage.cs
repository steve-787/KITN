using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>General mod preferences. Each category owns its controls; theme choices stay in a scrolling dropdown.</summary>
    internal sealed class ModSettingsPage
    {
        private readonly struct ThemeChoice
        {
            public readonly UiTheme Theme;
            public readonly string NameKey, DescriptionKey;
            public ThemeChoice(UiTheme theme, string nameKey, string descriptionKey)
            { Theme = theme; NameKey = nameKey; DescriptionKey = descriptionKey; }
        }

        // Adding a theme adds one entry here, without changing the page's size or layout.
        private static readonly ThemeChoice[] Themes =
        {
            new ThemeChoice(UiTheme.Retro, "settings.retro", "settings.retro_hint"),
            new ThemeChoice(UiTheme.Modern, "settings.modern", "settings.modern_hint")
        };

        public void Draw(UiLayoutStore settings)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.Dummy(new float2(0, 6 * scale));
            ConsoleStyle.PushLabelFont(1.1f);
            try { ImGui.TextColored(GameUiColors.Text, L10n.Get("settings.title")); }
            finally { ConsoleStyle.PopFont(); }
            ImGui.Dummy(new float2(0, 8 * scale));
            Appearance(settings);
            // Future categories are drawn here, inside the parent's scrolling settings page.
            ImGui.Dummy(new float2(0, 12 * scale));
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, GameUiColors.Muted);
            try { ImGui.TextWrapped(L10n.Get("settings.alignment_hint")); }
            finally { ImGui.PopStyleColor(); }
            if (settings?.IsLoaded == false)
            {
                ImGui.TextWrapped(L10n.Get("settings.load_failed"));
                if (ConsoleWidgets.Button(L10n.Get("settings.retry").AsSpan(), "settings.retry".AsSpan(), float2.Zero)) settings.Load();
            }
            else if (settings?.LastError != null)
                ImGui.TextWrapped(L10n.Get("settings.save_failed"));
            else ImGui.TextColored(GameUiColors.Muted, L10n.Get(settings?.IsDirty == true ? "settings.saving" : "settings.saved"));
        }

        private static void Appearance(UiLayoutStore settings)
        {
            ImGui.TextColored(GameUiColors.Label, L10n.Get("settings.appearance"));
            UiTheme selected = settings?.Theme ?? UiTheme.Retro;
            ThemeChoice current = Array.Find(Themes, choice => choice.Theme == selected);
            ConsoleWidgets.BeginRow(L10n.Get("settings.theme").AsSpan());
            try
            {
                ImGui.BeginDisabled(settings?.IsLoaded != true);
                try
                {
                    if (ConsoleWidgets.BeginComboControl("mod-theme".AsSpan(), L10n.Get(current.NameKey).AsSpan(), false))
                    {
                        try
                        {
                            foreach (ThemeChoice choice in Themes)
                            {
                                if (ImGui.Selectable(L10n.Id(choice.NameKey), choice.Theme == selected))
                                    settings?.SetTheme(choice.Theme, Environment.TickCount64);
                                if (choice.Theme == selected) ImGui.SetItemDefaultFocus();
                            }
                        }
                        finally { ConsoleWidgets.EndComboControl(); }
                    }
                }
                finally { ImGui.EndDisabled(); }
            }
            finally { ConsoleWidgets.EndRow(); }
            current = Array.Find(Themes, choice => choice.Theme == (settings?.Theme ?? UiTheme.Retro));
            ImGui.PushStyleColor(ImGuiCol.Text, GameUiColors.Muted);
            try { ImGui.TextWrapped(L10n.Get(current.DescriptionKey)); }
            finally { ImGui.PopStyleColor(); }
            ConsoleWidgets.BeginRow(L10n.Get("settings.auto_hide_frame").AsSpan());
            try
            {
                bool autoHide = settings?.AutoHideInstrumentFrame == true;
                ImGui.BeginDisabled(settings?.IsLoaded != true);
                try
                {
                    if (ConsoleWidgets.Checkbox("auto-hide-instrument-frame".AsSpan(), ref autoHide, false))
                        settings?.SetAutoHideInstrumentFrame(autoHide, Environment.TickCount64);
                    ModWindowStyle.Hint(L10n.Get("settings.auto_hide_frame_hint"));
                }
                finally { ImGui.EndDisabled(); }
            }
            finally { ConsoleWidgets.EndRow(); }
        }
    }
}
