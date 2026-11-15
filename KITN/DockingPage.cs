using System;
using Brutal.ImGuiApi;
using KSA;

namespace KITN
{
    internal sealed class DockingPage
    {
        public UiLayoutStore Settings { get; set; }
        public void Draw(RendezvousController controller, bool locked)
        {
            if (controller == null) { GameUiColors.MessageText(L10n.Get("rendezvous.require_vehicle")); return; }
            bool overlay = controller.DockingOptions.ShowGuidanceOverlay;
            ConsoleWidgets.BeginRow(L10n.Get("dock.overlay").AsSpan());
            try
            {
                if (ConsoleWidgets.Checkbox("dock.overlay".AsSpan(), ref overlay, false))
                {
                    var settings = controller.DockingOptions.Copy(); settings.ShowGuidanceOverlay = overlay;
                    controller.DockingOptions = settings; Settings?.SetDocking(settings, Environment.TickCount64);
                }
            }
            finally { ConsoleWidgets.EndRow(); }
            ModWindowStyle.Hint(L10n.Get("dock.overlay_hint"));
            using (PanelLayout.Section("docking-controls", "panel.docking_setup", "circle-plus"))
            {
                RendezvousPage.DrawTarget(controller, locked, "docking");
                ImGui.BeginDisabled(locked || controller.IsActive);
                try
                {
                    RendezvousPage.Row("rendezvous.own_port", () =>
                    {
                        if (!ImGui.BeginCombo("##docking-own-port", RendezvousPage.PortLabel(controller.OwnPort, controller.OwnPorts), ImGuiComboFlags.HeightLarge)) return;
                        try
                        {
                            for (int i = 0; i < controller.OwnPorts.Count; i++)
                            {
                                var port = controller.OwnPorts[i];
                                if (ImGui.Selectable(RendezvousPage.PortLabel(port, controller.OwnPorts) + "##own-port-" + i, ReferenceEquals(port, controller.OwnPort))) controller.SelectOwnPort(port);
                            }
                        }
                        finally { ImGui.EndCombo(); }
                    });
                    RendezvousPage.Row("rendezvous.target_port", () =>
                    {
                        if (!ImGui.BeginCombo("##docking-target-port", RendezvousPage.PortLabel(controller.TargetPort, controller.TargetPorts), ImGuiComboFlags.HeightLarge)) return;
                        try
                        {
                            for (int i = 0; i < controller.TargetPorts.Count; i++)
                            {
                                var port = controller.TargetPorts[i];
                                if (ImGui.Selectable(RendezvousPage.PortLabel(port, controller.TargetPorts) + "##target-port-" + i, ReferenceEquals(port, controller.TargetPort))) controller.SelectTargetPort(port);
                            }
                        }
                        finally { ImGui.EndCombo(); }
                    });
                }
                finally { ImGui.EndDisabled(); }
            }
            using (PanelLayout.Section("docking-state", "panel.relative_state", "circle-plus"))
            {
                if (controller.IsAutoDocking)
                {
                    if (controller.DockingOptions.EngineAssist)
                        ConsoleWidgets.Readout(L10n.Get("dock.engine_assist").AsSpan(), controller.DockingEngineStatus.AsSpan());
                    GameUiColors.MessageText(controller.DockingCurrentStep + " → " + controller.DockingNextStep);
                    ConsoleWidgets.Readout(L10n.Get("dock.eta").AsSpan(), controller.DockingEta.AsSpan());
                    RendezvousPage.Readout("dock.checkpoint_distance", controller.DockingCheckpointDistance, "m");
                    RendezvousPage.Readout("dock.closing_speed", controller.DockingClosingSpeed, "m/s");
                    RendezvousPage.Readout("dock.remaining_rotation", controller.DockingRotationRemaining, "°");
                    RendezvousPage.Readout("dock.spin_rate", controller.DockingAngularRate, "°/s");
                }
                RendezvousPage.Readout("rendezvous.distance", controller.Distance, "m");
                RendezvousPage.Readout("rendezvous.relative_speed", controller.RelativeSpeed, "m/s");
                RendezvousPage.Readout("rendezvous.alignment", controller.PointingError, "°");
                RendezvousPage.Readout("rendezvous.lateral", controller.LateralError, "m");
                if (controller.Message != null) GameUiColors.MessageText(controller.MessageText, MessageSeverity.IsError(controller.Message));
                else if (controller.CanDock) GameUiColors.MessageText(L10n.Get("docking.ready"));
                else if (controller.UnavailableReason != null)
                    GameUiColors.MessageText(L10n.Get(controller.UnavailableReason), MessageSeverity.IsError(controller.UnavailableReason));
                if (controller.LastDockingRecoveryText != null && controller.LastDockingRecoveryText != controller.MessageText)
                    GameUiColors.MessageText(controller.LastDockingRecoveryText, true);
            }
            ProximitySettingsPage.Docking(controller, Settings, locked);
        }
    }
}
