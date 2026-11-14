using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class RendezvousPage
    {
        public string Draw(RendezvousController controller, bool locked)
        {
            if (controller == null) { ImGui.TextWrapped(L10n.Get("rendezvous.require_vehicle")); return null; }
            string action = null;
            using (PanelLayout.Section("rendezvous-controls", "panel.docking_setup", "crosshair"))
            {
                ImGui.BeginDisabled(locked || controller.IsActive);
                try
                {
                    Row("rendezvous.target", () =>
                    {
                        if (!ImGui.BeginCombo("##rendezvous-target", controller.TargetName, ImGuiComboFlags.HeightLarge)) return;
                        try
                        {
                            for (int i = 0; i < controller.Targets.Count; i++)
                            {
                                var target = controller.Targets[i];
                                if (ImGui.Selectable(target.Id + "##rv-target-" + i, ReferenceEquals(target, controller.Target))) controller.SelectTarget(target);
                            }
                        }
                        finally { ImGui.EndCombo(); }
                    });
                    Row("rendezvous.own_port", () =>
                    {
                        if (!ImGui.BeginCombo("##rendezvous-own-port", PortLabel(controller.OwnPort, controller.OwnPorts), ImGuiComboFlags.HeightLarge)) return;
                        try
                        {
                            for (int i = 0; i < controller.OwnPorts.Count; i++)
                            {
                                var port = controller.OwnPorts[i];
                                if (ImGui.Selectable(PortLabel(port, controller.OwnPorts) + "##own-port-" + i, ReferenceEquals(port, controller.OwnPort))) controller.SelectOwnPort(port);
                            }
                        }
                        finally { ImGui.EndCombo(); }
                    });
                    Row("rendezvous.target_port", () =>
                    {
                        if (!ImGui.BeginCombo("##rendezvous-target-port", PortLabel(controller.TargetPort, controller.TargetPorts), ImGuiComboFlags.HeightLarge)) return;
                        try
                        {
                            for (int i = 0; i < controller.TargetPorts.Count; i++)
                            {
                                var port = controller.TargetPorts[i];
                                if (ImGui.Selectable(PortLabel(port, controller.TargetPorts) + "##target-port-" + i, ReferenceEquals(port, controller.TargetPort))) controller.SelectTargetPort(port);
                            }
                        }
                        finally { ImGui.EndCombo(); }
                    });
                }
                finally { ImGui.EndDisabled(); }
                ImGui.BeginDisabled(locked || controller.IsActive || !controller.CanMatch);
                try
                {
                    if (UiIcons.Button("rendezvous.match")) action = "rendezvous_match";
                }
                finally { ImGui.EndDisabled(); }
            }
            using (PanelLayout.Section("rendezvous-state", "panel.relative_state", "crosshair"))
            {
                Readout("rendezvous.distance", controller.Distance, "m");
                Readout("rendezvous.relative_speed", controller.RelativeSpeed, "m/s");
                Readout("rendezvous.alignment", controller.PointingError, "°");
                Readout("rendezvous.lateral", controller.LateralError, "m");
                if (controller.IsActive) ImGui.TextColored(GameUiColors.Pending, PhaseName(controller.Phase));
                else if (controller.Message != null) ImGui.TextWrapped(L10n.Get(controller.Message));
                else if (controller.UnavailableReason != null) ImGui.TextWrapped(L10n.Get(controller.UnavailableReason));
                ModWindowStyle.Hint(L10n.Get("rendezvous.hint"));
            }
            return action;
        }

        private static string PortLabel(DockingPort port, System.Collections.Generic.List<DockingPort> ports)
            => RendezvousController.PortName(port) + (port != null && ports.Count > 1 ? " · " + (ports.IndexOf(port) + 1) : "");

        private static void Row(string key, Action draw)
        {
            ConsoleWidgets.BeginRow(L10n.Get(key).AsSpan());
            try { ImGui.SetNextItemWidth(ConsoleWidgets.RowControlWidth); draw(); }
            finally { ConsoleWidgets.EndRow(); }
        }
        private static void Readout(string key, double value, string unit)
            => ConsoleWidgets.Readout(L10n.Get(key).AsSpan(), (double.IsFinite(value) ? value.ToString("N2", L10n.Culture) + " " + unit : "—").AsSpan());
        public static string PhaseName(DockingPhase phase) => L10n.Get("rendezvous.phase." + phase.ToString().ToLowerInvariant());
    }
}
