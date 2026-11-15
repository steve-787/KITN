using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class TransferPlannerPage
    {
        private int _mode, _timeUnit = 1;
        private readonly string[] _modes = new string[4];
        private ManeuverPlanner _displayedPlanner;
        private TransferGrid _plotGrid;
        private TransferPlotData _plotData;
        private bool _plotCapture;
        public UiLayoutStore Settings;
        public string DrawControls(ManeuverPlanner planner, bool locked)
        {
            if (planner == null) { GameUiColors.MessageText(L10n.Get("transfer.require_orbit")); return null; }
            if (!ReferenceEquals(_displayedPlanner, planner))
            { _displayedPlanner = planner; _mode = planner.HohmannMode ? 3 : 0; }
            string action = null;
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            string phase = planner.IsExecuting ? planner.DisplayMessage : L10n.Get(planner.IsSearching ? "transfer.calculating" : "transfer.ready");
            float phaseWidth = Math.Min(ImGui.CalcTextSize(phase).X, ImGui.GetContentRegionAvail().X * 0.32f);
            float phaseX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - phaseWidth;
            planner.PreferEarliest = _mode == 2;
            ImGui.BeginDisabled(locked || planner.IsSearching || planner.IsExecuting || planner.IsChecking);
            try
            {
                UiIcons.Text("orbit", AutopilotWindow.FitHeaderText(planner.OriginName ?? "—", ImGui.GetContentRegionAvail().X * 0.2f) + " →", GameUiColors.Label);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(Math.Max(80 * scale, phaseX - ImGui.GetCursorPosX() - 16 * scale));
                {
                    float popupWidth = ImGui.GetContentRegionAvail().X;
                    foreach (var body in planner.Bodies)
                        popupWidth = Math.Max(popupWidth, ImGui.CalcTextSize(body.IsMoon() ? body.Id + " · " + body.Parent.Id : body.Id).X + 40 * ConsoleStyle.EffectiveInterfaceScale);
                    ImGui.SetNextWindowSizeConstraints(new float2(popupWidth, 0), new float2(Math.Max(popupWidth, ImGui.GetIO().DisplaySize.X - 32), 500 * ConsoleStyle.EffectiveInterfaceScale));
                    if (ImGui.BeginCombo("##transfer-target", planner.Target?.Id ?? L10n.Get("transfer.select_target"), ImGuiComboFlags.HeightLarge))
                    {
                        try
                        {
                            foreach (var body in planner.Bodies)
                            {
                                ImGui.PushID(body.Id);
                                try
                                {
                                    string label = body.IsMoon() ? body.Id + " · " + body.Parent.Id : body.Id;
                                    if (ImGui.Selectable(label, ReferenceEquals(body, planner.Target))) planner.SelectTarget(body);
                                    if (ReferenceEquals(body, planner.Target)) ImGui.SetItemDefaultFocus();
                                }
                                finally { ImGui.PopID(); }
                            }
                        }
                        finally { ImGui.EndCombo(); }
                    }
                }
                ModWindowStyle.Hint(L10n.Get("transfer.destination_hint"));
                ImGui.SameLine(); ImGui.SetCursorPosX(phaseX);
                ImGui.TextColored(GameUiColors.Message(planner.IsExecuting && MessageSeverity.IsError(planner.Message)),
                    AutopilotWindow.FitHeaderText(phase, phaseWidth));
                ModWindowStyle.Hint(phase);
                _modes[0] = "Porkchop"; _modes[1] = L10n.Get("transfer.lowest"); _modes[2] = L10n.Get("transfer.earliest");
                _modes[3] = L10n.Get("transfer.hohmann");
                int selected = ConsoleWidgets.Segmented("transfer-mode".AsSpan(), _modes, _mode);
                if (selected >= 0)
                {
                    bool hohmann = selected == 3;
                    if (planner.HohmannMode != hohmann)
                    { planner.HohmannMode = hohmann; planner.SettingsChanged(); }
                    _mode = selected;
                    if (_mode != 0) planner.SelectBest(_mode == 2);
                }
                // Native segmented controls leave no vertical gap after their row.
                ImGui.Dummy(new float2(0, 2 * scale));
                if (_mode == 2)
                {
                    float limit = planner.MaximumDv;
                    if (Input("transfer.dv_limit", ref limit)) { planner.MaximumDv = limit; planner.SelectBest(true); }
                    ModWindowStyle.Hint(L10n.Get("transfer.earliest_hint"));
                }
            }
            finally { ImGui.EndDisabled(); }
            if (_mode == 1)
            {
                ImGui.BeginDisabled(locked || planner.IsSearching || planner.IsChecking || planner.IsExecuting || !planner.CanSearchOberth);
                try { if (Button("transfer.oberth_lowest")) action = "transfer_oberth"; }
                finally { ImGui.EndDisabled(); }
            }
            return action;
        }

        public string Draw(ManeuverPlanner planner, bool locked)
        {
            if (planner == null) { GameUiColors.MessageText(L10n.Get("transfer.require_orbit")); return null; }
            string action = null;
            if (planner.HasNestedArrival)
                using (PanelLayout.Section("transfer-itinerary"))
                {
                    UiIcons.Text("route", L10n.Format("transfer.route_via", planner.TransitBody.Id, planner.Target.Id), GameUiColors.Pending);
                    ModWindowStyle.Hint(L10n.Format("transfer.route_via_hint", planner.TransitBody.Id, planner.Target.Id));
                }
            if (!planner.CanSearch && !planner.IsExecuting && !planner.HasNode && planner.Grid != null)
                using (PanelLayout.Section("transfer-unavailable")) UiIcons.Text("info", L10n.Get(planner.UnavailableReason), GameUiColors.Message(planner.UnavailableReason));
            if (_mode == 0)
            {
                float figureHeight = PlotHeight();
                using (PanelLayout.Section("transfer-plot", "panel.transfer_windows", "orbit"))
                {
                    if (planner.Grid != null) DrawPorkchop(planner, figureHeight);
                    else DrawPlotStatus(planner, figureHeight);
                }
            }
            else if (planner.IsSearching) ImGui.TextColored(GameUiColors.Pending, L10n.Format("transfer.progress", planner.Progress));
            if (planner.Grid != null)
            {
                using (PanelLayout.Section("transfer-selection", "panel.selected_route", "route"))
                {
                    if (_mode != 0)
                    {
                        ImGui.BeginDisabled(planner.IsSearching || planner.IsChecking || planner.NeedsBetterRoute);
                        try { if (Button(_mode == 2 ? "transfer.earliest" : "transfer.lowest")) planner.SelectBest(_mode == 2); }
                        finally { ImGui.EndDisabled(); }
                    }
                    var solution = planner.Selected;
                    if (solution.Valid && !planner.IsExecuting)
                    {
                        float width = ImGui.GetContentRegionAvail().X / 3;
                        float start = ImGui.GetCursorPosX();
                        Summary("transfer.departure", Duration(solution.Departure - planner.Now), "arrow-up-right");
                        ImGui.SameLine(); ImGui.SetCursorPosX(start + width);
                        Summary("transfer.arrival", L10n.Format("transfer.arrival_in", Duration(solution.ArrivalTime - planner.Now)), "calendar-clock");
                        ModWindowStyle.Hint(L10n.Format("transfer.arrival_epoch", Epoch(solution.ArrivalTime)));
                        ImGui.SameLine(); ImGui.SetCursorPosX(start + 2 * width);
                        Summary("transfer.delta_v", solution.Cost(planner.IncludeCapture).ToString("N0", L10n.Culture) + " m/s", "zap");
                        ModWindowStyle.Hint(L10n.Format("transfer.cost_hint", solution.DepartureDv.ToString("N0", L10n.Culture), solution.CaptureDv.ToString("N0", L10n.Culture)));
                        UiIcons.Text("clock", L10n.Get("transfer.duration") + " · " + Duration(solution.FlightTime), GameUiColors.Muted);
                        UiIcons.Text("calendar-clock", L10n.Format("transfer.arrival_epoch", Epoch(solution.ArrivalTime)), GameUiColors.Muted);
                        if (solution.Flyby != null) ImGui.Text(L10n.Format("transfer.via", solution.Flyby.Body.Id));
                        if (planner.Grid.Request.Oberth) ImGui.TextColored(GameUiColors.Muted, L10n.Get("transfer.oberth_route"));
                        if (planner.Grid.Request.Hohmann) ImGui.TextColored(GameUiColors.Muted, L10n.Get("transfer.hohmann_route"));
                        ModWindowStyle.Hint(L10n.Format("transfer.cost_hint", solution.DepartureDv.ToString("N0", L10n.Culture), solution.CaptureDv.ToString("N0", L10n.Culture)));
                        ImGui.BeginDisabled(locked || planner.IsExecuting || !planner.CanCreate);
                        try { if (Primary("transfer.create")) action = "transfer_create"; }
                        finally { ImGui.EndDisabled(); }
                        if (planner.HasOtherNodes) ModWindowStyle.Hint(L10n.Get("transfer.existing_nodes"));
                        SameLineIfFits("transfer.execute");
                        ImGui.BeginDisabled(locked || planner.IsExecuting || !planner.CanExecute);
                        try { if (Button("transfer.execute")) action = "transfer_execute"; }
                        finally { ImGui.EndDisabled(); }
                        ModWindowStyle.Hint(L10n.Get(planner.IsSearching ? "transfer.calculating" : planner.IsChecking ? "transfer.checking" : planner.HasOtherNodes ? "transfer.existing_nodes" : planner.CanCreate ? "transfer.execute_hint" : "transfer.stale"));
                        if (!planner.IsSearching && (planner.Stale || solution.Departure <= planner.Now + 30)) GameUiColors.MessageText(L10n.Get("transfer.stale"), true);
                    }
                }
            }
            if (planner.HasNode)
                using (PanelLayout.Section("transfer-node", "panel.planner_node", "orbit")) DrawNodeActions(planner, locked, ref action);
            if (planner.DisplayMessage != null)
                using (PanelLayout.Section("transfer-status"))
                {
                    bool missingSoi = planner.Message == "transfer.no_encounter";
                    UiIcons.Text(MessageSeverity.IsError(planner.Message) ? "circle-alert" : "info", planner.DisplayMessage, GameUiColors.Message(planner.Message));
                    if (missingSoi)
                    {
                        string detail = double.IsFinite(planner.MissingSoiDeltaV) && planner.MissingSoiDeltaV > 0
                            ? L10n.Format("transfer.missing_soi_dv", Math.Ceiling(planner.MissingSoiDeltaV))
                            : L10n.Get("transfer.missing_soi_dv_unknown");
                        UiIcons.Text("zap", detail, ConsoleStyle.Danger);
                        ModWindowStyle.Hint(L10n.Get("transfer.missing_soi_dv_hint"));
                    }
                }
            using (PanelLayout.Section("transfer-options")) DrawSearchOptions(planner, locked);
            return action;
        }

        private static void DrawNodeActions(ManeuverPlanner planner, bool locked, ref string action)
        {
            if (planner.HasNode)
            {
                if (planner.CanExecuteCapture)
                {
                    ImGui.BeginDisabled(locked);
                    try { if (Primary("transfer.execute_capture")) action = "transfer_capture"; }
                    finally { ImGui.EndDisabled(); }
                    SameLineIfFits("transfer.remove");
                }
                ImGui.BeginDisabled(locked && !planner.IsExecuting);
                try { if (Button("transfer.remove")) action = "transfer_remove"; }
                finally { ImGui.EndDisabled(); }
            }
            if (planner.IsExecuting)
            {
                SameLineIfFits("transfer.stop");
                if (ConsoleWidgets.DangerButton(L10n.Get("transfer.stop").AsSpan(), "transfer.stop".AsSpan(), float2.Zero)) action = "transfer_stop";
            }
        }

        private void DrawSearchOptions(ManeuverPlanner planner, bool locked)
        {
            ImGui.BeginDisabled(locked || planner.IsSearching || planner.IsExecuting || planner.IsChecking);
            try
            {
                if (_mode == 3) { DrawHohmannOptions(planner); return; }
                if (!ImGui.CollapsingHeader(L10n.Get("transfer.window") + "###transfer-window")) return;
                ConsoleWidgets.BeginRow(L10n.Get("transfer.units").AsSpan());
                try
                {
                    ImGui.SetNextItemWidth(ConsoleWidgets.RowControlWidth);
                    if (ImGui.BeginCombo("##transfer-unit", L10n.Get(_timeUnit == 0 ? "transfer.hours" : "transfer.days")))
                    {
                        try
                        {
                            if (ImGui.Selectable(L10n.Get("transfer.hours"), _timeUnit == 0)) _timeUnit = 0;
                            if (ImGui.Selectable(L10n.Get("transfer.days"), _timeUnit == 1)) _timeUnit = 1;
                        }
                        finally { ImGui.EndCombo(); }
                    }
                }
                finally { ConsoleWidgets.EndRow(); }
                double unit = _timeUnit == 0 ? 3600 : 86400;
                bool edited = TimeInput("transfer.delay", ref planner.EarliestDelay, unit);
                edited |= TimeInput("transfer.horizon", ref planner.SearchWindow, unit);
                edited |= TimeInput("transfer.min_duration", ref planner.MinimumDuration, unit);
                edited |= TimeInput("transfer.max_duration", ref planner.MaximumDuration, unit);
                if (edited) planner.SettingsChanged();
                bool capture = planner.IncludeCapture;
                if (Checkbox("transfer.capture", ref capture))
                {
                    planner.IncludeCapture = capture;
                    if (_mode != 0) planner.SelectBest(_mode == 2);
                }
                ModWindowStyle.Hint(L10n.Get("transfer.capture_hint"));
                float periapsis = planner.PeriapsisKm;
                if (Input("transfer.periapsis", ref periapsis)) { planner.PeriapsisKm = periapsis; planner.SettingsChanged(); }
                bool autoWarp = planner.AutoWarp;
                if (Checkbox("transfer.auto_warp", ref autoWarp)) planner.AutoWarp = autoWarp;
                bool assists = planner.GravityAssists;
                ImGui.BeginDisabled(planner.HasNestedArrival);
                bool assistsChanged;
                try { assistsChanged = Checkbox("transfer.gravity_assists", ref assists); }
                finally { ImGui.EndDisabled(); }
                if (planner.HasNestedArrival) ModWindowStyle.Hint(L10n.Get("transfer.nested_assists_hint"));
                if (assistsChanged)
                {
                    planner.GravityAssists = assists;
                    Settings?.SetGravityAssists(assists, Environment.TickCount64);
                    planner.SettingsChanged();
                }
            }
            finally { ImGui.EndDisabled(); }
        }

        private void DrawHohmannOptions(ManeuverPlanner planner)
        {
            ImGui.TextWrapped(L10n.Get("transfer.hohmann_hint"));
            if (!ImGui.CollapsingHeader(L10n.Get("transfer.hohmann_settings") + "###hohmann-settings")) return;
            var s = planner.HohmannOptions.Copy();
            s.DepartureLeadSeconds = HohmannSlider("transfer.hohmann_lead", s.DepartureLeadSeconds, 60, 3600, "s");
            s.SynodicWindows = (int)Math.Round(HohmannSlider("transfer.hohmann_windows", s.SynodicWindows, 1, 3, ""));
            s.DurationTolerancePercent = HohmannSlider("transfer.hohmann_tolerance", s.DurationTolerancePercent, 1, 50, "%");
            // Property values are copied to locals because the native controls take ref parameters.
            bool sCapture = s.IncludeCapture;
            if (Checkbox("transfer.capture", ref sCapture)) s.IncludeCapture = sCapture;
            ModWindowStyle.Hint(L10n.Get("transfer.capture_hint"));
            bool warp = s.AutoWarp;
            if (Checkbox("transfer.auto_warp", ref warp)) s.AutoWarp = warp;
            float periapsis = planner.PeriapsisKm;
            if (Input("transfer.periapsis", ref periapsis)) { planner.PeriapsisKm = periapsis; planner.SettingsChanged(); }
            if (Button("proximity.defaults")) s = new HohmannSettings();
            ImGui.TextColored(GameUiColors.Muted, L10n.Get("settings.saved"));
            if (s.IsValid && !planner.HohmannOptions.Matches(s))
            {
                bool boundsChanged = s.DepartureLeadSeconds != planner.HohmannOptions.DepartureLeadSeconds
                    || s.SynodicWindows != planner.HohmannOptions.SynodicWindows
                    || s.DurationTolerancePercent != planner.HohmannOptions.DurationTolerancePercent;
                planner.HohmannOptions = s;
                Settings?.SetHohmann(s, Environment.TickCount64);
                if (boundsChanged) planner.SettingsChanged(); else planner.SelectBest(false);
            }
        }

        private static float HohmannSlider(string key, float value, float low, float high, string unit)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try { SettingsSlider.Float(key, ref value, low, high, "N0", unit); return value; }
            finally { ConsoleWidgets.EndRow(); }
        }

        private static float PlotHeight()
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            // Use the viewport height: available content height changes as this child scrolls.
            return Math.Clamp((ImGui.GetWindowSize().Y - 86 * scale) * 0.32f, 80 * scale, 260 * scale);
        }

        private static void DrawPlotStatus(ManeuverPlanner planner, float height)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            string text = planner.IsSearching ? L10n.Format("transfer.progress", planner.Progress)
                : planner.IsExecuting ? planner.DisplayMessage ?? L10n.Get("transfer.executing")
                : L10n.Get(planner.CanSearch ? "transfer.plot_empty" : planner.UnavailableReason);
            ImGui.TextColored(GameUiColors.Muted, L10n.Get("transfer.plot_y"));
            float width = Math.Max(120, ImGui.GetContentRegionAvail().X - 156 * scale);
            height = Math.Max(height, ImGui.CalcTextSize(text, false, Math.Max(1, width - 24 * scale)).Y + 24 * scale);
            float2 size = new float2(width, height);
            float2 origin = ImGui.GetCursorScreenPos() + new float2(70 * scale, 0);
            ImGui.InvisibleButton("##transfer-porkchop", new float2(width + 156 * scale, height + 26 * scale));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin + size, ConsoleStyle.ApplyAlpha(ConsoleStyle.InsetU32), 4 * scale);
            uint guide = ImGui.ColorConvertFloat4ToU32(new float4(1, 1, 1, 0.05f));
            for (int i = 1; i < 4; i++)
            {
                draw.AddLine(origin + new float2(size.X * i / 4, 0), origin + new float2(size.X * i / 4, size.Y), guide);
                draw.AddLine(origin + new float2(0, size.Y * i / 4), origin + new float2(size.X, size.Y * i / 4), guide);
            }
            draw.AddRect(origin, origin + size, ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), 4 * scale);
            DrawPlotMessage(draw, origin, size, text, planner.IsSearching, planner.Progress, scale,
                !planner.IsSearching && MessageSeverity.IsError(planner.IsExecuting ? planner.Message : planner.UnavailableReason));
            ImGui.TextColored(GameUiColors.Muted, L10n.Get("transfer.plot_x"));
        }

        private static void DrawPlotMessage(ImDrawListPtr draw, float2 origin, float2 size, string text, bool searching, int progress, float scale, bool error = false)
        {
            draw.PushClipRect(origin, origin + size, true);
            try
            {
                float wrapWidth = Math.Max(1, size.X - 24 * scale);
                float2 textSize = ImGui.CalcTextSize(text, false, wrapWidth);
                float textY = Math.Max(12 * scale, (size.Y - textSize.Y - (searching ? 24 * scale : 0)) / 2);
                draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + new float2(12 * scale, textY),
                    ImGui.ColorConvertFloat4ToU32(GameUiColors.Message(error)), text, wrapWidth);
                if (searching)
                {
                    float2 bar = origin + new float2(12 * scale, Math.Min(size.Y - 18 * scale, textY + textSize.Y + 12 * scale));
                    draw.AddRectFilled(bar, bar + new float2(size.X - 24 * scale, 6 * scale), ConsoleStyle.FrameU32, 3 * scale);
                    draw.AddRectFilled(bar, bar + new float2((size.X - 24 * scale) * Math.Clamp(progress, 0, 100) / 100f, 6 * scale), ConsoleStyle.PendingU32, 3 * scale);
                }
            }
            finally { draw.PopClipRect(); }
        }

        private static void Summary(string label, string value, string icon)
        {
            ImGui.BeginGroup();
            try
            {
                ConsoleStyle.PushLabelFont(0.8f);
                try { UiIcons.Text(icon, L10n.Get(label), GameUiColors.Muted); }
                finally { ConsoleStyle.PopFont(); }
                ImGui.TextColored(label == "transfer.arrival" ? GameUiColors.Accent : GameUiColors.Text, value);
            }
            finally { ImGui.EndGroup(); }
        }

        // Draw with the native ImGui list, keeping the plot interactive without a texture or graphics-device dependency.
        public void DrawPorkchop(ManeuverPlanner planner, float height)
        {
            var grid = planner.Grid.PorkchopGrid ?? planner.Grid;
            var request = grid.Request;
            if (!ReferenceEquals(grid, _plotGrid) || _plotData == null || _plotCapture != planner.IncludeCapture)
            {
                _plotGrid = grid; _plotCapture = planner.IncludeCapture;
                _plotData = new TransferPlotData(grid, planner.IncludeCapture);
            }
            var plot = _plotData;
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.TextColored(GameUiColors.Muted, L10n.Get("transfer.plot_y"));
            float2 region = ImGui.GetCursorScreenPos();
            string[] ticks = new string[3];
            float gutter = 70 * scale;
            ConsoleStyle.PushLabelFont(0.8f);
            try
            {
                for (int i = 0; i < ticks.Length; i++)
                {
                    ticks[i] = AxisDuration(request.MinimumDuration + (request.MaximumDuration - request.MinimumDuration) * i / 2);
                    gutter = Math.Max(gutter, ImGui.CalcTextSize(ticks[i]).X + 10 * scale);
                }
            }
            finally { ConsoleStyle.PopFont(); }
            float width = Math.Max(120 * scale, ImGui.GetContentRegionAvail().X - gutter - 86 * scale);
            float2 origin = region + new float2(gutter, 0);
            ImGui.InvisibleButton("##transfer-porkchop", new float2(width + gutter + 86 * scale, height + 26 * scale));
            float2 mouse = ImGui.GetMousePos();
            bool hovered = !planner.IsSearching && !planner.IsChecking && !planner.IsExecuting && ImGui.IsItemHovered() && mouse.X >= origin.X && mouse.X <= origin.X + width
                && mouse.Y >= origin.Y && mouse.Y <= origin.Y + height;
            bool select = hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                || (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)));
            var draw = ImGui.GetWindowDrawList();
            double minimum = plot.Minimum, maximum = plot.Maximum;
            float dx = width / (request.Width - 1), dy = height / (request.Height - 1);
            draw.AddRectFilled(origin, origin + new float2(width, height), HeatColor(-1));
            int strideX = Math.Max(1, (request.Width - 1 + 127) / 128);
            int strideY = Math.Max(1, (request.Height - 1 + 63) / 64);
            for (int y = 0; y < request.Height - 1; y += strideY)
                for (int x = 0; x < request.Width - 1; x += strideX)
                {
                    int nextX = Math.Min(request.Width - 1, x + strideX), nextY = Math.Min(request.Height - 1, y + strideY);
                    float a = plot.DisplayValues[y * request.Width + x], b = plot.DisplayValues[y * request.Width + nextX];
                    float c = plot.DisplayValues[nextY * request.Width + nextX], d = plot.DisplayValues[nextY * request.Width + x];
                    draw.AddRectFilledMultiColor(origin + new float2(x * dx, height - nextY * dy),
                        origin + new float2(nextX * dx, height - y * dy), HeatColor(d), HeatColor(c), HeatColor(b), HeatColor(a));
                }
            draw.PushClipRect(origin, origin + new float2(width, height), true);
            try
            {
                for (int i = 0; i < plot.Contours.Count; i++)
                {
                    var contour = plot.Contours[i];
                    uint contourColor = ImGui.ColorConvertFloat4ToU32(new float4(0.92f, 0.98f, 1, contour.Level % 3 == 0 ? 0.35f : 0.16f));
                    draw.AddLine(origin + new float2((float)contour.X1 * width, height * (1 - (float)contour.Y1)),
                        origin + new float2((float)contour.X2 * width, height * (1 - (float)contour.Y2)), contourColor, Math.Max(0.75f, scale * (contour.Level % 3 == 0 ? 1.1f : 0.8f)));
                }
                uint gridColor = ImGui.ColorConvertFloat4ToU32(new float4(0.9f, 0.97f, 1, 0.08f));
                for (int i = 1; i < 4; i++)
                {
                    draw.AddLine(origin + new float2(width * i / 4, 0), origin + new float2(width * i / 4, height), gridColor);
                    draw.AddLine(origin + new float2(0, height * i / 4), origin + new float2(width, height * i / 4), gridColor);
                }
            }
            finally { draw.PopClipRect(); }
            draw.AddRect(origin, origin + new float2(width, height), ImGui.ColorConvertFloat4ToU32(GameUiColors.Hairline));
            uint muted = ImGui.ColorConvertFloat4ToU32(GameUiColors.Muted);
            ConsoleStyle.PushLabelFont(0.8f);
            draw.PushClipRect(region - new float2(0, ImGui.GetFontSize()),
                new float2(origin.X - 6 * scale, origin.Y + height + ImGui.GetFontSize()), true);
            try
            {
                for (int i = 0; i < ticks.Length; i++)
                {
                    float y = origin.Y + height * (1 - i / 2f);
                    draw.AddText(new float2(origin.X - 8 * scale - ImGui.CalcTextSize(ticks[i]).X, y - ImGui.GetFontSize() / 2), muted, ticks[i]);
                }
            }
            finally { draw.PopClipRect(); ConsoleStyle.PopFont(); }
            var best = grid.Pick(planner.IncludeCapture, false, planner.MaximumDv, planner.Now + 30);
            if (best.Valid) Marker(draw, origin, width, height, request, best, false, scale);
            if (planner.Selected.Valid) Marker(draw, origin, width, height, request, planner.Selected, true, scale);
            if (hovered)
            {
                int x = Math.Clamp((int)Math.Round((mouse.X - origin.X) / dx), 0, request.Width - 1);
                int y = Math.Clamp((int)Math.Round((height - mouse.Y + origin.Y) / dy), 0, request.Height - 1);
                int index = y * request.Width + x;
                if (select) planner.SelectPorkchop(index);
                uint hoverGuide = ImGui.ColorConvertFloat4ToU32(new float4(1, 1, 1, 0.28f));
                draw.AddLine(new float2(mouse.X, origin.Y), new float2(mouse.X, origin.Y + height), hoverGuide);
                draw.AddLine(new float2(origin.X, mouse.Y), new float2(origin.X + width, mouse.Y), hoverGuide);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(12, 10) * scale);
                ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6 * scale);
                float tooltipWidth = Math.Min(340 * scale, Math.Max(100, ImGui.GetIO().DisplaySize.X - 32 * scale));
                ImGui.SetNextWindowSizeConstraints(new float2(tooltipWidth, 0), new float2(tooltipWidth, float.MaxValue));
                if (ImGui.BeginTooltip())
                {
                    try
                    {
                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + tooltipWidth - 24 * scale);
                        var cell = grid.Cells[index];
                        UiIcons.Text("route", planner.OriginName + " → " + (planner.RouteTarget?.Id ?? "—"), GameUiColors.Text);
                        ImGui.Separator();
                        if (cell.Valid)
                        {
                            UiIcons.Text("arrow-up-right", L10n.Get("transfer.departure") + " · " + Duration(cell.Departure - planner.Now), GameUiColors.Label);
                            UiIcons.Text("calendar-clock", L10n.Get("transfer.arrival") + " · " + L10n.Format("transfer.arrival_in", Duration(cell.ArrivalTime - planner.Now)), GameUiColors.Text);
                            ImGui.TextColored(GameUiColors.Muted, L10n.Format("transfer.arrival_epoch", Epoch(cell.ArrivalTime)));
                            UiIcons.Text("clock", L10n.Get("transfer.duration") + " · " + Duration(cell.FlightTime), GameUiColors.Muted);
                            ImGui.Separator();
                            UiIcons.Text("zap", "Δv · " + cell.Cost(planner.IncludeCapture).ToString("N0", L10n.Culture) + " m/s", GameUiColors.Text);
                        }
                        else UiIcons.Text("circle-alert", L10n.Get("transfer.invalid_cell"), GameUiColors.Error);
                    }
                    finally { ImGui.PopTextWrapPos(); ImGui.EndTooltip(); }
                }
                ImGui.PopStyleVar(2);
            }
            float2 legend = origin + new float2(width + 10 * scale, 0);
            for (int i = 0; i < 64; i++)
                draw.AddRectFilled(legend + new float2(0, i * height / 64), legend + new float2(12 * scale, (i + 1) * height / 64 + 0.1f), HeatColor(1 - i / 63f));
            ConsoleStyle.PushLabelFont(0.8f);
            try
            {
                draw.AddText(legend + new float2(17 * scale, 0), muted, double.IsFinite(minimum) ? maximum.ToString("N0", L10n.Culture) + "+" : "—");
                draw.AddText(legend + new float2(17 * scale, height - ImGui.GetFontSize()), muted, double.IsFinite(minimum) ? minimum.ToString("N0", L10n.Culture) : "—");
                draw.AddText(legend + new float2(17 * scale, height / 2), muted, "m/s");
                draw.AddText(origin + new float2(0, height + 5 * scale), muted, AxisDuration(request.Earliest - planner.Now));
                string end = AxisDuration(request.Latest - planner.Now);
                draw.AddText(origin + new float2(width - ImGui.CalcTextSize(end).X, height + 5 * scale), muted, end);
            }
            finally { ConsoleStyle.PopFont(); }
            ImGui.TextColored(GameUiColors.Muted, L10n.Get("transfer.plot_x"));
            ModWindowStyle.Hint(L10n.Format("transfer.plot_hint", Duration(request.MinimumDuration), Duration(request.MaximumDuration),
                double.IsFinite(minimum) ? minimum.ToString("N0", L10n.Culture) : "—", maximum.ToString("N0", L10n.Culture)));
            if (planner.IsSearching)
            {
                draw.AddRectFilled(origin, origin + new float2(width, height), ImGui.ColorConvertFloat4ToU32(new float4(0.05f, 0.07f, 0.10f, 0.82f)));
                DrawPlotMessage(draw, origin, new float2(width, height), L10n.Format("transfer.progress", planner.Progress), true, planner.Progress, scale);
            }
        }

        private static uint HeatColor(float value)
        {
            if (value < 0) return ImGui.ColorConvertFloat4ToU32(new float4(0.09f, 0.12f, 0.15f, 1));
            float4 a, b; float t;
            if (value < 0.35f) { a = new float4(0.10f, 0.32f, 0.66f, 1); b = new float4(0.10f, 0.72f, 0.67f, 1); t = value / 0.35f; }
            else if (value < 0.7f) { a = new float4(0.10f, 0.72f, 0.67f, 1); b = new float4(0.95f, 0.79f, 0.30f, 1); t = (value - 0.35f) / 0.35f; }
            else { a = new float4(0.95f, 0.79f, 0.30f, 1); b = new float4(0.80f, 0.23f, 0.19f, 1); t = (value - 0.7f) / 0.3f; }
            return ImGui.ColorConvertFloat4ToU32(a * (1 - t) + b * t);
        }
        private static void Marker(ImDrawListPtr draw, float2 origin, float width, float height, TransferRequest request, TransferSolution solution, bool selected, float scale)
        {
            float2 point = origin + new float2((float)Math.Clamp((solution.Departure - request.Earliest) / (request.Latest - request.Earliest), 0, 1) * width,
                (1 - (float)Math.Clamp((solution.Duration - request.MinimumDuration) / (request.MaximumDuration - request.MinimumDuration), 0, 1)) * height);
            uint ink = ImGui.ColorConvertFloat4ToU32(new float4(0.03f, 0.05f, 0.06f, 1));
            uint white = ImGui.ColorConvertFloat4ToU32(new float4(1, 1, 1, 1));
            draw.AddCircle(point, (selected ? 7 : 4) * scale, ink, 20, 4 * scale);
            draw.AddCircle(point, (selected ? 7 : 4) * scale, white, 20, 1.5f * scale);
            if (selected)
            {
                draw.AddLine(point - new float2(11, 0) * scale, point + new float2(11, 0) * scale, white, scale);
                draw.AddLine(point - new float2(0, 11) * scale, point + new float2(0, 11) * scale, white, scale);
            }
        }
        public static string AxisDuration(double seconds)
        {
            if (!double.IsFinite(seconds)) return "—";
            double absolute = Math.Abs(seconds);
            const double day = 86400, year = 365.25 * day, month = year / 12;
            if (absolute < month)
                return absolute >= day ? L10n.Format("transfer.axis_days", seconds / day)
                    : absolute >= 3600 ? L10n.Format("transfer.axis_hours", seconds / 3600)
                    : L10n.Format("transfer.axis_minutes", seconds / 60);
            if (absolute < year) return L10n.Format("transfer.axis_months", seconds / month);
            if (absolute >= 1000 * year) return L10n.Format("transfer.axis_years_large", seconds / year);
            // Fixed elapsed units, independent of the simulation epoch or a planet's calendar.
            double months = Math.Round(absolute / month, MidpointRounding.AwayFromZero);
            double years = Math.Floor(months / 12), remainder = months % 12;
            string value = remainder == 0 ? L10n.Format("transfer.axis_years", years)
                : L10n.Format("transfer.axis_year_months", years, remainder);
            return seconds < 0 ? "−" + value : value;
        }

        public static string Duration(double seconds)
        {
            if (!double.IsFinite(seconds)) return "—";
            double absolute = Math.Abs(seconds);
            if (absolute >= 365.25 * 86400 / 12) return AxisDuration(seconds);
            return absolute >= 86400 ? L10n.Format("transfer.day_value", seconds / 86400)
                : absolute >= 3600 ? L10n.Format("transfer.hour_value", seconds / 3600)
                : L10n.Format("transfer.minute_value", seconds / 60);
        }
        public static string Epoch(double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0) return "—";
            double days = Math.Floor(seconds / 86400);
            int remainder = (int)(seconds % 86400);
            return L10n.Format("transfer.epoch", days.ToString("N0", L10n.Culture),
                $"{remainder / 3600:00}:{remainder / 60 % 60:00}:{remainder % 60:00}");
        }
        private static bool Input(string key, ref float value)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try { ImGui.SetNextItemWidth(ConsoleWidgets.RowControlWidth); return ImGui.InputFloat("##" + key, ref value, 0, 0, "%.3f"); }
            finally { ConsoleWidgets.EndRow(); }
        }
        private static bool TimeInput(string key, ref double seconds, double unit)
        {
            float value = (float)(seconds / unit); bool changed = Input(key, ref value);
            if (changed) seconds = value * unit;
            return changed;
        }
        private static bool Checkbox(string key, ref bool value)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try { return ConsoleWidgets.Checkbox(key.AsSpan(), ref value, false); }
            finally { ConsoleWidgets.EndRow(); }
        }
        private static bool Button(string key) => UiIcons.Button(key);
        private static bool Primary(string key) => UiIcons.Button(key, primary: true);
        private static void SameLineIfFits(string key)
        {
            ImGui.SameLine();
            if (ImGui.GetContentRegionAvail().X < UiIcons.ButtonWidth(key)) ImGui.NewLine();
        }
    }
}
