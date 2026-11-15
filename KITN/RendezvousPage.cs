using System;
using Brutal.ImGuiApi;
using KSA;

namespace KITN
{
    internal sealed class RendezvousPage
    {
        public UiLayoutStore Settings { get; set; }
        public string Draw(RendezvousController controller, bool locked)
        {
            if (controller == null) { GameUiColors.MessageText(L10n.Get("rendezvous.require_vehicle")); return null; }
            string action = null;
            using (PanelLayout.Section("rendezvous-controls", "panel.rendezvous_target", "crosshair"))
            {
                DrawTarget(controller, locked, "rendezvous");
            }
            using (PanelLayout.Section("rendezvous-state", "panel.relative_state", "crosshair"))
            {
                Readout("rendezvous.distance", controller.Distance, "m");
                Readout("rendezvous.relative_speed", controller.RelativeSpeed, "m/s");
                if (controller.IsActive && double.IsFinite(controller.PlannedDeltaV))
                {
                    Readout("rendezvous.delta_v", controller.PlannedDeltaV, "m/s");
                    ConsoleWidgets.Readout(L10n.Get("transfer.arrival").AsSpan(),
                        L10n.Format("transfer.arrival_in", TransferPlannerPage.Duration(Math.Max(0, controller.PlannedArrival - Universe.GetElapsedSeconds()))).AsSpan());
                }
                if (controller.Message != null) GameUiColors.MessageText(controller.MessageText, MessageSeverity.IsError(controller.Message));
                else if (controller.CanRendezvous) GameUiColors.MessageText(L10n.Get("rendezvous.ready"));
                else if (controller.RendezvousUnavailableReason != null) GameUiColors.MessageText(L10n.Get(controller.RendezvousUnavailableReason), MessageSeverity.IsError(controller.RendezvousUnavailableReason));
                ImGui.BeginDisabled(locked || controller.IsActive || !controller.CanMatch);
                try { if (UiIcons.Button("rendezvous.match")) action = "rendezvous_match"; }
                finally { ImGui.EndDisabled(); }
                ModWindowStyle.Hint(L10n.Get("rendezvous.match_hint"));
            }
            ProximitySettingsPage.Rendezvous(controller, Settings, locked);
            return action;
        }

        internal static void DrawTarget(RendezvousController controller, bool locked, string id)
        {
            ImGui.BeginDisabled(locked || controller.IsActive);
            try
            {
                Row("rendezvous.target", () =>
                {
                    if (!ImGui.BeginCombo("##" + id + "-target", controller.TargetName, ImGuiComboFlags.HeightLarge)) return;
                    try
                    {
                        for (int i = 0; i < controller.Targets.Count; i++)
                        {
                            var target = controller.Targets[i];
                            if (ImGui.Selectable(target.Id + "##" + id + "-target-" + i, ReferenceEquals(target, controller.Target))) controller.SelectTarget(target);
                        }
                    }
                    finally { ImGui.EndCombo(); }
                });
            }
            finally { ImGui.EndDisabled(); }
        }

        internal static string PortLabel(DockingPort port, System.Collections.Generic.List<DockingPort> ports)
            => RendezvousController.PortName(port) + (port != null && ports.Count > 1 ? " · " + (ports.IndexOf(port) + 1) : "");

        internal static void Row(string key, Action draw)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try { ImGui.SetNextItemWidth(ConsoleWidgets.RowControlWidth); draw(); }
            finally { ConsoleWidgets.EndRow(); }
        }
        internal static void Readout(string key, double value, string unit)
            => ConsoleWidgets.Readout(L10n.Get(key).AsSpan(), (double.IsFinite(value) ? value.ToString("N2", L10n.Culture) + " " + unit : "—").AsSpan());
        public static string PhaseName(DockingPhase phase) => L10n.Get("rendezvous.phase." + phase.ToString().ToLowerInvariant());
    }
}
