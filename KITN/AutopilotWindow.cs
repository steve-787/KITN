using System;
using Brutal.ImGuiApi;
using Brutal.ImPlotApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class AutopilotWindow : IDisposable
    {
        private readonly AppNavigation _navigation = new AppNavigation();
        private bool _homeOpen = true;
        private bool _wasOpen;
        private static readonly float4 RouteRed = new float4(1, 0.2f, 0.25f, 1);
        private static readonly float4 White = new float4(1, 1, 1, 1);
        private ImPlotContextPtr _ownedPlotContext;
        private bool _liveScale = true, _fitNext = true;
        private int _tab;
        private string _action;
        public FlightPresetStore Presets { get; set; }
        public PersistentWindowLayout Layout { get; set; }
        public UiLayoutStore Settings { get; set; }
        public ManeuverPlanner Planner { get; set; }
        public RendezvousController Rendezvous { get; set; }
        private readonly RendezvousPage _rendezvousPage = new RendezvousPage();
        private readonly TransferPlannerPage _transferPage = new TransferPlannerPage();
        private bool _settingsOpen;
        private bool _diagnosticsOpen;
        private readonly ModSettingsPage _settingsPage = new ModSettingsPage();
        private string _presetName = "", _selectedPresetName;
        private LocalizedMessage _presetMessage;
        private bool _presetError;
        private static readonly FlightPlanSettings DefaultPlan = new FlightPlanSettings();

        public string Draw(ref bool open, LaunchProfile profile, AscentGuidance guidance, FlightDisplay flight,
            MissionClock clock, bool hookReady, bool actionPending, LandingController landing = null)
        {
            _action = null;
            if (!open) { _wasOpen = false; return null; }
            if (!_wasOpen) { _homeOpen = true; _settingsOpen = _diagnosticsOpen = false; }
            _wasOpen = true;
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.SetNextWindowSizeConstraints(new float2(560, 400) * scale, new float2(1600, 1400) * scale);
            ImGuiCond sizeCondition = ImGuiCond.FirstUseEver;
            float2 size = new float2(780, 650) * scale;
            if (Layout != null) size = Layout.Prepare(size, scale, out sizeCondition);
            if (!ModWindowStyle.Begin("KITN", "KITN · " + flight.VehicleName, ref open, size, sizeCondition))
                return null;
            try
            {
                Layout?.Capture(scale);
                int activePage = _settingsOpen ? (_diagnosticsOpen ? 6 : AppNavigation.Settings) : _homeOpen ? AppNavigation.Home : _tab;
                int selectedPage = _navigation.Draw(activePage, _homeOpen && !_settingsOpen);
                if (selectedPage != AppNavigation.NoSelection) OpenPage(selectedPage);
                bool running = guidance.IsActive || clock.IsCountingDown || landing?.IsActive == true
                    || Rendezvous?.IsActive == true || Planner?.IsExecuting == true;
                if (_settingsOpen)
                {
                    if (running) Actions(profile, guidance, flight, clock, hookReady, actionPending, landing);
                    ImGui.BeginChild("kitn-settings-page", new float2(0, 0));
                    try
                    {
                        if (_diagnosticsOpen) DiagnosticsTab(profile, flight);
                        else if (_settingsPage.Draw(Settings)) OpenPage(6);
                    }
                    finally { ImGui.EndChild(); }
                    return _action;
                }
                if (_homeOpen)
                {
                    if (running) Actions(profile, guidance, flight, clock, hookReady, actionPending, landing);
                    ImGui.BeginChild("kitn-launcher", new float2(0, 0));
                    try
                    {
                        int launch = _navigation.Launcher();
                        if (launch != AppNavigation.NoSelection) OpenPage(launch);
                    }
                    finally { ImGui.EndChild(); }
                    return _action;
                }
                bool recovery = landing != null && (_tab == 4 || landing.IsActive);
                bool transfer = _tab == 5;
                bool rendezvous = _tab == 3 || Rendezvous?.IsActive == true;
                string target = rendezvous ? Rendezvous?.TargetName ?? "—" : transfer ? (Planner?.OriginName ?? "—") + " → "
                    + (Planner?.Selected.Flyby is TransferFlyby flyby ? flyby.Body.Id + " → " : "") + (Planner?.Target?.Id ?? "—")
                    : recovery ? landing.TargetName : $"{Format(profile.TargetPeriapsisKm)} × {Format(profile.TargetApoapsisKm)} km";
                string phase = rendezvous ? RendezvousPage.PhaseName(Rendezvous?.Phase ?? DockingPhase.Idle) : transfer ? Planner?.IsExecuting == true ? Planner.DisplayMessage : L10n.Get(Planner?.IsSearching == true ? "transfer.calculating" : "transfer.ready")
                    : clock.IsCountingDown ? L10n.Get("clock.countdown") : recovery && !guidance.IsActive ? landing.Phase
                    : GuidancePhase(profile, guidance, flight);
                // Status changes must not wrap the header and move every control below it.
                float headerWidth = ImGui.GetContentRegionAvail().X;
                float phaseWidth = Math.Min(ImGui.CalcTextSize(phase).X, headerWidth * 0.42f);
                ImGui.BeginGroup();
                try
                {
                    ConsoleStyle.PushLabelFont(0.8f);
                    try { ImGui.TextColored(GameUiColors.Muted, L10n.Get(rendezvous ? "rendezvous.target" : transfer ? "transfer.route" : recovery ? "landing.target" : "readout.target_orbit")); }
                    finally { ConsoleStyle.PopFont(); }
                    ImGui.TextColored(GameUiColors.Text, FitHeaderText(target, headerWidth - phaseWidth - 8 * scale));
                }
                finally { ImGui.EndGroup(); }
                ModWindowStyle.Hint(target);
                ImGui.SameLine();
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowSize().X - phaseWidth - 16 * scale));
                bool phasePending = rendezvous ? Rendezvous?.IsActive == true
                    : transfer ? Planner?.IsSearching == true || Planner?.IsChecking == true || Planner?.IsExecuting == true
                    : clock.IsCountingDown || landing?.IsActive == true || (flight.IsPlanning && !flight.HasCurrentPlan(profile));
                ImGui.TextColored(phasePending ? GameUiColors.Pending : GameUiColors.Muted, FitHeaderText(phase, phaseWidth));
                ModWindowStyle.Hint(phase);
                Actions(profile, guidance, flight, clock, hookReady, actionPending, landing);
                ImGui.Dummy(new float2(0, 4 * scale));
                // Only the page scrolls: navigation and abort controls always remain accessible.
                ImGui.BeginChild("kitn-page", new float2(0, 0));
                try
                {
                    bool locked = guidance.IsActive || clock.IsCountingDown || landing?.IsActive == true || Rendezvous?.IsActive == true || Planner?.IsExecuting == true;
                    switch (_tab)
                    {
                        case 0: FlightTab(profile, guidance, flight, clock, actionPending); break;
                        case 1: PlanTab(profile, flight, locked || actionPending || _action == "countdown"); break;
                        case 2: AutomationTab(profile, locked); break;
                        case 3:
                            string rendezvousAction = _rendezvousPage.Draw(Rendezvous, locked || actionPending);
                            if (rendezvousAction != null) _action = rendezvousAction;
                            break;
                        case 4: LandingTab(landing, guidance, clock); break;
                        case 5:
                            _transferPage.Settings = Settings;
                            string transferAction = _transferPage.Draw(Planner, locked || actionPending);
                            if (transferAction != null) _action = transferAction;
                            break;
                    }
                }
                finally { ImGui.EndChild(); }
            }
            finally { ModWindowStyle.End(); }
            return _action;
        }

        public void OnVisibilityChanged(bool visible)
        {
            if (!visible) _wasOpen = false;
        }

        private void OpenPage(int page)
        {
            _homeOpen = page == AppNavigation.Home;
            _settingsOpen = page == AppNavigation.Settings || page == 6;
            _diagnosticsOpen = page == 6;
            if (page >= 0 && page < 6) _tab = page;
        }

        private static string FitHeaderText(string text, float width)
        {
            if (ImGui.CalcTextSize(text).X <= width) return text;
            int length = text.Length;
            while (length > 0 && ImGui.CalcTextSize(text.Substring(0, length) + "…").X > width) length--;
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length) + "…";
        }

        private void Actions(LaunchProfile profile, AscentGuidance guidance, FlightDisplay flight, MissionClock clock,
            bool hookReady, bool pending, LandingController landing)
        {
            ImGui.BeginDisabled(!flight.HasVehicle || !hookReady || pending);
            try
            {
                if (Rendezvous?.IsActive == true)
                {
                    if (DangerButton("rendezvous.stop")) _action = "rendezvous_stop";
                }
                else if (Planner?.IsExecuting == true)
                {
                    if (DangerButton("transfer.stop")) _action = "transfer_stop";
                }
                else if (clock.IsCountingDown)
                {
                    if (DangerButton("action.cancel_countdown")) _action = "cancel_countdown";
                }
                else if (landing?.IsActive == true)
                {
                    if (DangerButton("landing.abort")) _action = "landing_abort";
                }
                else if (guidance.IsActive)
                {
                    if (DangerButton("action.abort")) _action = "abort";
                }
                else if (_tab == 3)
                {
                    ImGui.BeginDisabled(Rendezvous?.CanDock != true);
                    try { if (PrimaryButton("rendezvous.dock")) _action = "rendezvous_dock"; }
                    finally { ImGui.EndDisabled(); }
                }
                else if (_tab == 5)
                {
                    ImGui.BeginDisabled(Planner == null || Planner.IsChecking || (Planner.NeedsBetterRoute ? !Planner.CanImproveRoute && !Planner.IsSearching : !Planner.CanSearch));
                    try
                    {
                        if (Planner?.IsSearching == true)
                        { if (Button("transfer.cancel")) _action = "transfer_cancel"; }
                        else if (Planner?.NeedsBetterRoute == true)
                        {
                            if (PrimaryButton("transfer.improve")) _action = "transfer_improve";
                            Hint(Planner.SuggestedPeriapsisKm > 0
                                ? L10n.Format("transfer.improve_altitude_hint", Planner.SuggestedPeriapsisKm.ToString("N1", L10n.Culture))
                                : L10n.Get(Planner.HasTransferApsisAdjustment ? "transfer.improve_apsides_hint" : "transfer.improve_hint"));
                        }
                        else if (PrimaryButton("transfer.calculate")) _action = "transfer_search";
                    }
                    finally { ImGui.EndDisabled(); }
                }
                else if (_tab == 4 && landing != null)
                {
                    ImGui.BeginDisabled(!landing.CanStart);
                    try
                    {
                        if (PrimaryButton("landing.start")) _action = "landing_start";
                        Hint(L10n.Get(landing.CanStart ? "landing.start_hint" : landing.UnavailableReason));
                    }
                    finally { ImGui.EndDisabled(); }
                }
                else
                {
                    ImGui.BeginDisabled(!ValidFlightPlan(profile) || !flight.CanLaunch(profile));
                    try
                    {
                        if (PrimaryButton("action.countdown"))
                        {
                            _action = "countdown";
                            _fitNext = true;
                        }
                        Hint(flight.CanLaunch(profile) ? L10n.Format("action.countdown_hint", profile.LaunchCountdownSeconds) : flight.PlanningMessage(profile));
                    }
                    finally { ImGui.EndDisabled(); }
                }
            }
            finally { ImGui.EndDisabled(); }
            if (!guidance.IsActive && !clock.IsCountingDown && landing?.IsActive != true
                && Rendezvous?.IsActive != true && Planner?.IsExecuting != true
                && (guidance.CurrentState != AscentGuidance.AscentState.Idle || flight.Track.Count > 0 || clock.HasLaunched))
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(pending || !flight.HasVehicle || !hookReady);
                try
                {
                    if (Button("action.reset")) { _action = "reset"; _fitNext = true; }
                    Hint(L10n.Get("action.reset_hint"));
                }
                finally { ImGui.EndDisabled(); }
            }
        }

        private void LandingTab(LandingController landing, AscentGuidance guidance, MissionClock clock)
        {
            Section("landing.section", "landing.description");
            if (landing == null) { ImGui.TextColored(GameUiColors.Muted, L10n.Get("landing.require_vehicle")); return; }
            bool locked = landing.IsActive || guidance.IsActive || clock.IsCountingDown;
            ImGui.BeginDisabled(locked);
            try
            {
                ConsoleWidgets.BeginRow(L10n.Get("landing.target").AsSpan());
                try
                {
                    if (ConsoleWidgets.BeginComboControl("landing-pad".AsSpan(), landing.TargetName.AsSpan(), landing.Pads.Count == 0))
                    {
                        try
                        {
                            for (int i = 0; i < landing.Pads.Count; i++)
                                if (ImGui.Selectable(landing.Pads[i].Key, i == landing.SelectedPadIndex)) landing.SelectedPadIndex = i;
                        }
                        finally { ConsoleWidgets.EndComboControl(); }
                    }
                }
                finally { ConsoleWidgets.EndRow(); }
                bool previousWarp = landing.AutoWarp;
                Checkbox("landing.auto_warp", ref landing.AutoWarp, "landing.auto_warp_hint");
                if (previousWarp != landing.AutoWarp)
                {
                    var options = landing.Settings.Copy(); options.AutoWarp = landing.AutoWarp;
                    landing.Settings = options; Settings?.SetLanding(options, Environment.TickCount64);
                }
            }
            finally { ImGui.EndDisabled(); }
            if (landing.Pads.Count > 0)
            {
                LandingPad pad = landing.Pads[Math.Clamp(landing.SelectedPadIndex, 0, landing.Pads.Count - 1)];
                ConsoleWidgets.Readout(L10n.Get("landing.coordinates").AsSpan(),
                    $"{Format(pad.Latitude * FlightAttitude.RAD_TO_DEG, "N4")}° / {Format(pad.Longitude * FlightAttitude.RAD_TO_DEG, "N4")}°".AsSpan());
            }
            ConsoleWidgets.Readout(L10n.Get("landing.available_dv").AsSpan(), $"{Format(landing.AvailableDeltaV, "N0")} m/s".AsSpan());
            if (double.IsFinite(landing.RequiredDeltaV))
                ConsoleWidgets.Readout(L10n.Get("landing.required_dv").AsSpan(), $"{Format(landing.RequiredDeltaV, "N0")} m/s".AsSpan());
            LandingSettingsPage.Draw(landing, Settings, locked || Planner?.IsExecuting == true || Rendezvous?.IsActive == true);
            ConsoleWidgets.Rule();
            if (landing.IsActive || landing.State == LandingState.Landed || landing.State == LandingState.Aborted)
                ImGui.TextWrapped(landing.Message);
            else
                ImGui.TextColored(GameUiColors.Muted, L10n.Get(guidance.IsActive || clock.IsCountingDown ? "landing.require_idle" : landing.UnavailableReason));
            Hint(L10n.Get("landing.status_hint"));
        }

        private void FlightTab(LaunchProfile profile, AscentGuidance guidance, FlightDisplay flight, MissionClock clock, bool pending)
        {
            ImGui.Checkbox(L10n.Id("route.live_scale"), ref _liveScale);
            Hint(L10n.Get("route.live_scale_hint"));
            ImGui.SameLine();
            if (Button("action.fit")) _fitNext = true;
            DrawGraph(profile, flight);
            ImGui.TextWrapped(flight.LaunchPlan != null || flight.IsPlanning ? flight.PlanningMessage(profile) : flight.Prediction.Message);
            string routeHint = L10n.Get("route.hint");
            if (flight.Track.Count == 0) routeHint += "\n\n" + L10n.Get("route.trace_starts");
            if (flight.Track.Count > 0 && !guidance.IsActive) routeHint += "\n\n" + L10n.Get("route.frozen_plan");
            Hint(routeHint);
            if (flight.Warning != null) ImGui.TextColored(GameUiColors.Pending, flight.Warning);
            if (guidance.CurrentState == AscentGuidance.AscentState.CoastToApoapsis)
            {
                ImGui.BeginDisabled(pending);
                try { if (Button("action.warp")) _action = "warp"; }
                finally { ImGui.EndDisabled(); }
            }
        }

        private void DrawGraph(LaunchProfile profile, FlightDisplay flight)
        {
            ImPlotContextPtr previous = ImPlot.GetCurrentContext();
            if (previous.IsNull())
            {
                if (_ownedPlotContext.IsNull())
                {
                    _ownedPlotContext = ImPlot.CreateContext();
                    ImPlot.SetImGuiContext(ImGui.GetCurrentContext());
                }
                ImPlot.SetCurrentContext(_ownedPlotContext);
            }
            ImPlot.PushStyleColor(ImPlotCol.FrameBg, GameUiColors.Panel);
            ImPlot.PushStyleColor(ImPlotCol.PlotBg, GameUiColors.Inset);
            ImPlot.PushStyleColor(ImPlotCol.PlotBorder, GameUiColors.Hairline);
            ImPlot.PushStyleColor(ImPlotCol.AxisText, GameUiColors.Muted);
            ImPlot.PushStyleColor(ImPlotCol.AxisGrid, GameUiColors.Frame);
            ImPlot.PushStyleColor(ImPlotCol.LegendBg, GameUiColors.Panel);
            ImPlot.PushStyleColor(ImPlotCol.LegendText, GameUiColors.Text);
            try
            {
                float scale = ConsoleStyle.EffectiveInterfaceScale;
                var planned = flight.Prediction;
                var measured = flight.Track;
                bool hasTarget = float.IsFinite(profile.TargetPeriapsisKm) && profile.TargetPeriapsisKm > 0;
                float legendWidth = ImGui.CalcTextSize(L10n.Get("route.planned")).X
                    + ImGui.CalcTextSize(L10n.Get("route.actual")).X + 80 * scale;
                if (hasTarget) legendWidth += ImGui.CalcTextSize(L10n.Get("route.target")).X + 32 * scale;
                if (measured.Count > 0) legendWidth += ImGui.CalcTextSize(L10n.Get("route.rocket")).X + 32 * scale;
                bool horizontalLegend = ImGui.GetContentRegionAvail().X >= legendWidth;
                float height = Math.Clamp(ImGui.GetContentRegionAvail().Y - 100 * scale,
                    (horizontalLegend ? 180 : 260) * scale, 350 * scale);
                if (ImPlot.BeginPlot("###route", new float2(-1, height), ImPlotFlags.NoTitle | ImPlotFlags.Crosshairs))
                {
                    try
                    {
                        double maxX = Math.Max(10, Maximum(planned.Distances, 0));
                        maxX = Math.Max(maxX, measured.DistanceKm);
                        double target = float.IsFinite(profile.TargetApoapsisKm) ? Math.Max(0, profile.TargetApoapsisKm) : 200;
                        double maxY = Math.Max(1, Math.Max(target, Maximum(planned.Altitudes, 0)));
                        maxY = Math.Max(maxY, Maximum(measured.Altitudes, 0));
                        double minY = Math.Min(0, Minimum(measured.Altitudes, 0));
                        ImPlot.SetupAxes(L10n.Get("route.distance_axis"), L10n.Get("route.height_axis"));
                        ImPlot.SetupAxesLimits(0, maxX * 1.08, minY - (minY < 0 ? 5 : 0), maxY * 1.12,
                            _liveScale || _fitNext ? ImPlotCond.Always : ImPlotCond.Once);
                        _fitNext = false;
                        ImPlot.SetupLegend(ImPlotLocation.South,
                            ImPlotLegendFlags.Outside | ImPlotLegendFlags.NoButtons
                            | (horizontalLegend ? ImPlotLegendFlags.Horizontal : ImPlotLegendFlags.None));
                        if (hasTarget)
                        {
                            Span<double> targetX = stackalloc double[] { 0, maxX * 1.08 };
                            Span<double> targetY = stackalloc double[] { profile.TargetPeriapsisKm, profile.TargetPeriapsisKm };
                            ImPlot.SetNextLineStyle(GameUiColors.Muted, 1);
                            ImPlot.PlotLine(L10n.Id("route.target"), (ReadOnlySpan<double>)targetX, targetY);
                        }
                        ImPlot.SetNextLineStyle(RouteRed, 2);
                        ImPlot.PlotLine(L10n.Id("route.planned"), (ReadOnlySpan<double>)planned.Distances, planned.Altitudes);
                        ImPlot.SetNextLineStyle(White, 2);
                        ImPlot.PlotLine(L10n.Id("route.actual"), measured.Distances, measured.Altitudes);
                        if (measured.Count > 0)
                        {
                            ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 4, White, 1, GameUiColors.Inset);
                            ImPlot.PlotScatter(L10n.Id("route.rocket"), measured.Distances.Slice(measured.Count - 1),
                                measured.Altitudes.Slice(measured.Count - 1));
                        }
                    }
                    finally { ImPlot.EndPlot(); }
                }
            }
            finally
            {
                ImPlot.PopStyleColor(7);
                if (previous.IsNull()) ImPlot.SetCurrentContext(previous);
            }
        }

        private static string GuidancePhase(LaunchProfile profile, AscentGuidance guidance, FlightDisplay flight)
        {
            if (guidance.CurrentState == AscentGuidance.AscentState.Idle && flight.HasVehicle)
            {
                if (!flight.HasCurrentPlan(profile)) return L10n.Get("planning.phase_pending");
                if (!flight.LaunchPlan.Success) return L10n.Get("planning.phase_failed");
            }
            return StateName(guidance.CurrentState);
        }

        private void PlanTab(LaunchProfile profile, FlightDisplay flight, bool locked)
        {
            if (locked) ImGui.TextColored(GameUiColors.Pending, L10n.Get("plan.locked"));
            PresetControls(profile, locked);
            if (flight.HasVehicle)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, flight.HasCurrentPlan(profile) && flight.LaunchPlan.Success ? GameUiColors.Muted : GameUiColors.Pending);
                try { ImGui.TextWrapped(flight.PlanningMessage(profile)); }
                finally { ImGui.PopStyleColor(); }
            }
            ImGui.BeginDisabled(locked);
            try
            {
                Section("plan.launch", "plan.launch_hint");
                ConsoleWidgets.BeginRow(L10n.Get("plan.countdown").AsSpan());
                try
                {
                    ImGui.SetNextItemWidth(ConsoleWidgets.RowControlWidth);
                    ImGui.InputInt("##countdown-duration", ref profile.LaunchCountdownSeconds, 1, 10);
                    Hint(L10n.Get("plan.countdown_hint"));
                }
                finally { ConsoleWidgets.EndRow(); }
                if (profile.LaunchCountdownSeconds < 1 || profile.LaunchCountdownSeconds > 3600)
                    ImGui.TextColored(GameUiColors.Pending, L10n.Get("plan.countdown_invalid"));
                Section("readout.target_orbit", "plan.orbit_hint");
                if (Button("plan.leo"))
                    profile.TargetApoapsisKm = profile.TargetPeriapsisKm = 200;
                Input("plan.apoapsis", ref profile.TargetApoapsisKm, "plan.apoapsis_hint");
                Input("plan.periapsis", ref profile.TargetPeriapsisKm, "plan.periapsis_hint");
                if (!ValidOrbit(profile)) ImGui.TextColored(GameUiColors.Pending, L10n.Get("plan.orbit_invalid"));
                Section("plan.turn", "plan.turn_hint");
                Input("plan.turn_start", ref profile.GravityTurnStartM, "plan.turn_start_hint");
                Input("plan.turn_end", ref profile.GravityTurnEndM, "plan.turn_end_hint");
                Input("plan.min_speed", ref profile.GravityTurnMinSpeedMps, "plan.min_speed_hint");
                Input("plan.heading", ref profile.LaunchHeadingDeg, "plan.heading_hint");
                if (!ValidTurn(profile)) ImGui.TextColored(GameUiColors.Pending, L10n.Get("plan.turn_invalid"));
                Slider("plan.shape", ref profile.GravityTurnShape, 0.8f, 2, "plan.shape_hint");
                Slider("plan.pitch_rate", ref profile.MaxPitchRateDegS, 0.1f, 1.5f, "plan.pitch_rate_hint");
            }
            finally { ImGui.EndDisabled(); }
        }

        private void PresetControls(LaunchProfile profile, bool locked)
        {
            Section("preset.section", "preset.description");
            FlightPreset selected = Presets?.Find(_selectedPresetName);
            string preview = selected?.Settings.Matches(profile) == true ? selected.Name
                : DefaultPlan.Matches(profile) ? L10n.Get("preset.default") : L10n.Get("preset.custom");
            ImGui.BeginDisabled(locked);
            try
            {
                ConsoleWidgets.BeginRow(L10n.Get("preset.list").AsSpan());
                try
                {
                    if (ConsoleWidgets.BeginComboControl("flight-preset".AsSpan(), preview.AsSpan(), false))
                    {
                        try
                        {
                            ImGui.PushID("built-in-flight-preset");
                            bool loadDefault;
                            try { loadDefault = ImGui.Selectable(L10n.Get("preset.default"), DefaultPlan.Matches(profile)); }
                            finally { ImGui.PopID(); }
                            if (loadDefault)
                            {
                                DefaultPlan.Apply(profile); _selectedPresetName = null; _presetName = "";
                                _presetMessage = new LocalizedMessage("preset.loaded", new object[] { new LocalizedMessage("preset.default", Array.Empty<object>()) });
                                _presetError = false; _action = "preset_loaded"; _fitNext = true;
                                Diag.Info("PRESET", "event=selected name=default_leo");
                            }
                            if (Presets != null)
                                foreach (FlightPreset preset in Presets.Presets)
                                {
                                    ImGui.PushID(preset.Name);
                                    bool loadPreset;
                                    try { loadPreset = ImGui.Selectable(preset.Name, ReferenceEquals(preset, selected) && preset.Settings.Matches(profile)); }
                                    finally { ImGui.PopID(); }
                                    if (loadPreset)
                                    {
                                        preset.Settings.Apply(profile);
                                        _selectedPresetName = _presetName = preset.Name;
                                        _presetMessage = new LocalizedMessage("preset.loaded", new object[] { preset.Name });
                                        _presetError = false; _action = "preset_loaded"; _fitNext = true;
                                        Diag.Info("PRESET", $"event=selected name=\"{preset.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                                    }
                                }
                        }
                        finally { ConsoleWidgets.EndComboControl(); }
                    }
                    Hint(L10n.Get("preset.load_hint"));
                }
                finally { ConsoleWidgets.EndRow(); }
            }
            finally { ImGui.EndDisabled(); }
            ConsoleWidgets.BeginRow(L10n.Get("preset.name").AsSpan());
            bool submitted;
            try
            {
                submitted = ConsoleWidgets.TextLine("flight-preset-name".AsSpan(), ref _presetName, false, out _);
                Hint(L10n.Get("preset.name_hint"));
            }
            finally { ConsoleWidgets.EndRow(); }
            bool canSave = Presets?.IsLoaded == true && FlightPresetStore.ValidName(_presetName)
                && FlightPlanSettings.Capture(profile).IsValid;
            bool existing = Presets?.Find(_presetName?.Trim()) != null;
            ImGui.BeginDisabled(!canSave);
            try
            {
                if ((Button(existing ? "preset.update" : "preset.save") || submitted) && canSave)
                {
                    if (Presets.Save(_presetName, profile))
                    {
                        _selectedPresetName = _presetName = _presetName.Trim().Normalize();
                        _presetError = false;
                        _presetMessage = new LocalizedMessage("preset.saved", new object[] { _presetName });
                        Diag.Info("PRESET", $"event=saved name=\"{_presetName.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" updated={existing.ToString().ToLowerInvariant()} count={Presets.Presets.Count}");
                    }
                    else
                    {
                        _presetError = true;
                        _presetMessage = new LocalizedMessage("preset.save_failed", Array.Empty<object>());
                        Diag.Error("PRESET", Presets.LastError, "Saving the flight plan failed");
                    }
                }
                Hint(L10n.Get(existing ? "preset.update_hint" : "preset.save_hint"));
            }
            finally { ImGui.EndDisabled(); }
            if (Presets?.IsLoaded == false)
            {
                ImGui.TextWrapped(L10n.Get("preset.load_failed"));
                if (Button("preset.retry"))
                {
                    if (Presets.Load()) { _presetError = false; _presetMessage = null; }
                    else Diag.Error("PRESET", Presets.LastError, "Reloading saved flight plans failed");
                }
            }
            else if (_presetMessage != null)
                ImGui.TextColored(_presetError ? GameUiColors.Pending : GameUiColors.Muted, _presetMessage.ToString());
        }

        private static void AutomationTab(LaunchProfile profile, bool locked)
        {
            if (locked) ImGui.TextColored(GameUiColors.Pending, L10n.Get("automation.locked"));
            ImGui.BeginDisabled(locked);
            try
            {
                Section("automation.stages", "automation.stages_hint");
                Checkbox("automation.auto_stage", ref profile.AutoStaging);
                ImGui.BeginDisabled(!profile.AutoStaging);
                try
                {
                    Checkbox("automation.hot_stage", ref profile.HotStaging, "automation.hot_stage_hint");
                    if (profile.HotStaging) Slider("automation.fuel_threshold", ref profile.HotStagingFuelPercent, 1, 25);
                }
                finally { ImGui.EndDisabled(); }
                Section("automation.warp", "automation.warp_hint");
                Checkbox("automation.skip", ref profile.AutoTimeskipToApoapsis);
            }
            finally { ImGui.EndDisabled(); }
        }

        private static void DiagnosticsTab(LaunchProfile profile, FlightDisplay flight)
        {
            Section("tab.diagnostics", "diagnostics.hint");
            ConsoleWidgets.Readout(L10n.Get("diagnostics.vertical_speed").AsSpan(), $"{Format(flight.VerticalSpeed, "N0")} m/s".AsSpan());
            ConsoleWidgets.Readout(L10n.Get("diagnostics.pressure").AsSpan(), $"{Format(flight.DynamicPressureKpa)} kPa".AsSpan());
            ConsoleWidgets.Readout(L10n.Get("diagnostics.acceleration").AsSpan(), $"{Format(flight.AccelerationG, "N2")} g".AsSpan());
            ConsoleWidgets.Readout(L10n.Get("diagnostics.pitch").AsSpan(), $"{Format(profile.CurrentPitchDeg)}° / {Format(profile.DesiredPitchDeg)}°".AsSpan());
            ConsoleWidgets.Readout(L10n.Get("diagnostics.trace").AsSpan(), L10n.Format("diagnostics.points", flight.Track.Count).AsSpan());
            Section("diagnostics.logs", "diagnostics.logs_hint");
            Checkbox("diagnostics.telemetry", ref profile.DebugLoggingEnabled);
            ImGui.BeginDisabled(!profile.DebugLoggingEnabled);
            try
            {
                Slider("diagnostics.interval", ref profile.DebugLogIntervalSeconds, 0.1f, 5);
                Checkbox("diagnostics.csv", ref profile.WriteTelemetryCsv);
            }
            finally { ImGui.EndDisabled(); }
        }

        private static void Section(string title, string explanation)
        {
            ImGui.Dummy(new float2(0, 6 * ConsoleStyle.EffectiveInterfaceScale));
            ImGui.TextColored(GameUiColors.Label, L10n.Get(title));
            Hint(L10n.Get(explanation));
        }

        private static void Input(string label, ref float value, string explanation)
        {
            ConsoleWidgets.BeginRow(L10n.Get(label).AsSpan());
            try
            {
                ImGui.SetNextItemWidth(ConsoleWidgets.RowControlWidth);
                ImGui.InputFloat("##" + label, ref value);
                Hint(L10n.Get(explanation));
            }
            finally { ConsoleWidgets.EndRow(); }
        }

        private static void Checkbox(string label, ref bool value, string explanation = null)
        {
            ConsoleWidgets.BeginRow(L10n.Get(label).AsSpan());
            try
            {
                ConsoleWidgets.Checkbox(label.AsSpan(), ref value, false);
                if (explanation != null) Hint(L10n.Get(explanation));
            }
            finally { ConsoleWidgets.EndRow(); }
        }

        private static void Slider(string label, ref float value, float min, float max, string explanation = null)
        {
            ConsoleWidgets.BeginRow(L10n.Get(label).AsSpan());
            try
            {
                ConsoleWidgets.SliderFloat(label.AsSpan(), ref value, min, max, Format(value).AsSpan(), false);
                if (explanation != null) Hint(L10n.Get(explanation));
            }
            finally { ConsoleWidgets.EndRow(); }
        }

        private static void Hint(string text)
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

        // ConsoleWidgets draws its label directly. Its separate ID argument keeps IDs out
        // of the visible text and preserves focus when the game language changes.
        private static bool Button(string key) => UiIcons.Button(key);
        private static bool PrimaryButton(string key) => UiIcons.Button(key, primary: true);
        private static bool DangerButton(string key) => UiIcons.Button(key, danger: true);

        private static bool ValidOrbit(LaunchProfile p) => float.IsFinite(p.TargetApoapsisKm)
            && float.IsFinite(p.TargetPeriapsisKm) && p.TargetPeriapsisKm >= 100 && p.TargetApoapsisKm >= p.TargetPeriapsisKm;
        private static bool ValidTurn(LaunchProfile p) => float.IsFinite(p.GravityTurnStartM) && p.GravityTurnStartM >= 0
            && float.IsFinite(p.GravityTurnEndM) && p.GravityTurnEndM > p.GravityTurnStartM
            && float.IsFinite(p.GravityTurnMinSpeedMps) && p.GravityTurnMinSpeedMps >= 0
            && float.IsFinite(p.LaunchHeadingDeg) && p.LaunchHeadingDeg >= 0 && p.LaunchHeadingDeg <= 360
            && float.IsFinite(p.GravityTurnShape) && float.IsFinite(p.MaxPitchRateDegS);
        private static bool ValidFlightPlan(LaunchProfile p) => ValidOrbit(p) && ValidTurn(p)
            && p.LaunchCountdownSeconds >= 1 && p.LaunchCountdownSeconds <= 3600;
        private static string Format(double value, string format = "N1") => double.IsFinite(value) ? value.ToString(format, L10n.Culture) : "—";
        internal static string StateName(AscentGuidance.AscentState state) => state switch
        {
            AscentGuidance.AscentState.Idle => L10n.Get("state.idle"),
            AscentGuidance.AscentState.VerticalClimb => L10n.Get("state.vertical"),
            AscentGuidance.AscentState.GravityTurn => L10n.Get("state.turn"),
            AscentGuidance.AscentState.CoastToApoapsis => L10n.Get("state.coast"),
            AscentGuidance.AscentState.Circularizing => L10n.Get("state.insertion"),
            AscentGuidance.AscentState.OrbitAchieved => L10n.Get("state.orbit"),
            AscentGuidance.AscentState.Aborted => L10n.Get("state.aborted"),
            _ => "—"
        };
        private static double Maximum(ReadOnlySpan<double> values, double initial)
        {
            foreach (double value in values) if (double.IsFinite(value)) initial = Math.Max(initial, value);
            return initial;
        }
        private static double Minimum(ReadOnlySpan<double> values, double initial)
        {
            foreach (double value in values) if (double.IsFinite(value)) initial = Math.Min(initial, value);
            return initial;
        }

        public void Dispose()
        {
            if (!_ownedPlotContext.IsNull())
            {
                ImPlotContextPtr previous = ImPlot.GetCurrentContext();
                bool ownedWasCurrent = previous.Equals(_ownedPlotContext);
                ImPlot.DestroyContext(_ownedPlotContext);
                ImPlot.SetCurrentContext(ownedWasCurrent ? default : previous);
                _ownedPlotContext = default;
            }
        }
    }
}
