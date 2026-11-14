using System;
using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Scrollable categories and a data-driven widget list leave room for future preferences.</summary>
    internal sealed class ModSettingsPage
    {
        private static readonly ModPage[] Widgets = { new ModPage("settings.instruments", "timer") };
        private static readonly string[] WidgetIds = { "mission_time" };
        private static readonly UiTheme[] Themes = Enum.GetValues<UiTheme>();

        public bool Draw(UiLayoutStore settings)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.BeginDisabled(settings?.IsLoaded != true);
            try
            {
                using (PanelLayout.Section("settings-widgets"))
                {
                    if (ImGui.CollapsingHeader(L10n.Get("settings.widgets") + "###settings-widgets", ImGuiTreeNodeFlags.DefaultOpen))
                        for (int i = 0; i < Widgets.Length; i++) DrawWidget(settings, WidgetIds[i], Widgets[i]);
                }
                using (PanelLayout.Section("settings-control"))
                {
                    if (ImGui.CollapsingHeader(L10n.Get("settings.control") + "###settings-control", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ConsoleWidgets.BeginRow(L10n.Get("settings.allow_rcs").AsSpan());
                        try
                        {
                            bool allowed = settings?.AllowRcs ?? true;
                            if (ConsoleWidgets.Checkbox("allow-rcs".AsSpan(), ref allowed, false)) settings?.SetAllowRcs(allowed, Environment.TickCount64);
                            ModWindowStyle.Hint(L10n.Get("settings.allow_rcs_hint"));
                        }
                        finally { ConsoleWidgets.EndRow(); }
                    }
                }
            }
            finally { ImGui.EndDisabled(); }
            ImGui.Dummy(new float2(0, 8 * scale));
            bool diagnostics = UiIcons.Button("diagnostics.open");
            ImGui.Dummy(new float2(0, 8 * scale));
            if (settings?.IsLoaded == false)
            {
                UiIcons.Text("circle-alert", L10n.Get("settings.load_failed"), GameUiColors.Pending);
                if (UiIcons.Button("settings.retry")) settings.Load();
            }
            else UiIcons.Text(settings?.LastError != null ? "circle-alert" : "info",
                L10n.Get(settings?.LastError != null ? "settings.save_failed" : settings?.IsDirty == true ? "settings.saving" : "settings.saved"), GameUiColors.Muted);
            float remaining = ImGui.GetContentRegionAvail().Y;
            ImGui.Dummy(new float2(0, Math.Max(8 * scale, remaining - 72 * scale)));
            ImGui.Separator();
            float footerWidth = ImGui.GetContentRegionAvail().X;
            float versionWidth = ImGui.CalcTextSize(BuildInfo.DisplayVersion).X;
            UiIcons.Text("github", "steve-787/KITN", GameUiColors.Muted);
            if (ImGui.IsItemClicked()) Process.Start(new ProcessStartInfo(BuildInfo.RepositoryUrl) { UseShellExecute = true });
            ModWindowStyle.Hint("GitHub · " + BuildInfo.RepositoryUrl);
            if (footerWidth >= ImGui.CalcTextSize("steve-787/KITN").X + ImGui.GetFontSize() + versionWidth + 32 * scale)
                ImGui.SameLine();
            ImGui.TextColored(GameUiColors.Muted, BuildInfo.DisplayVersion);
            ModWindowStyle.Hint(BuildInfo.InformationalVersion);
            return diagnostics;
        }

        private static void DrawWidget(UiLayoutStore settings, string id, ModPage widget)
        {
            ImGui.PushID(id);
            try
            {
                UiIcons.Text(widget.Icon, L10n.Get(widget.Key), GameUiColors.Label);
                var prefs = settings?.Widget(id) ?? new UiWidgetPreferences();
                bool changed = false;
                ConsoleWidgets.BeginRow(L10n.Get("settings.widget_enabled").AsSpan());
                try { bool enabled = prefs.Enabled; changed |= ConsoleWidgets.Checkbox("widget-enabled".AsSpan(), ref enabled, false); prefs.Enabled = enabled; }
                finally { ConsoleWidgets.EndRow(); }
                ImGui.TextColored(GameUiColors.Muted, L10n.Get("settings.widget_theme"));
                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo("##mod-theme", ThemeName(prefs.Theme), ImGuiComboFlags.HeightLarge))
                {
                    try
                    {
                        foreach (UiTheme theme in Themes)
                        {
                            if (ImGui.Selectable(ThemeName(theme), prefs.Theme == theme))
                            { prefs.Theme = theme; changed = true; }
                            if (prefs.Theme == theme) ImGui.SetItemDefaultFocus();
                        }
                    }
                    finally { ImGui.EndCombo(); }
                }
                ConsoleWidgets.BeginRow(L10n.Get("settings.transparency").AsSpan());
                try
                {
                    bool hide = prefs.AutoHideFrame; changed |= ConsoleWidgets.Checkbox("auto-hide-instrument-frame".AsSpan(), ref hide, false); prefs.AutoHideFrame = hide;
                    ModWindowStyle.Hint(L10n.Get("settings.auto_hide_frame_hint"));
                }
                finally { ConsoleWidgets.EndRow(); }
                if (changed) settings?.SetWidget(id, prefs, Environment.TickCount64);
            }
            finally { ImGui.PopID(); }
        }
        private static string ThemeName(UiTheme theme) => L10n.Get("settings." + theme.ToString().ToLowerInvariant());
    }
}
