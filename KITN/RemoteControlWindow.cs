using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    internal sealed class RemoteControlWindow
    {
        public PersistentWindowLayout Layout { get; set; }

        public Vehicle Draw(ref bool open, RemoteControlFleet fleet, Vehicle player)
        {
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            ImGui.SetNextWindowSizeConstraints(new float2(360, 260) * scale, new float2(1200, 1200) * scale);
            ImGuiCond condition = ImGuiCond.FirstUseEver;
            float2 size = new float2(480, 430) * scale;
            if (Layout != null) size = Layout.Prepare(size, scale, out condition);
            if (!ModWindowStyle.Begin("KITNRemoteControl", L10n.Get("remote.window"), ref open, size, condition,
                detached: Layout?.Detached == true)) return null;
            Vehicle selected = null;
            try
            {
                Layout?.Capture(scale);
                PanelLayout.BeginCard("remote-selected", controls: true);
                try
                {
                    ConsoleWidgets.Readout(L10n.Get("remote.vessel").AsSpan(), (fleet.Selected.Vehicle?.Id ?? L10n.Get("vehicle.none")).AsSpan());
                    ImGui.TextColored(GameUiColors.Label, fleet.Selected.OperationStatus);
                    if (fleet.Selected.IsSuspended) GameUiColors.MessageText(L10n.Get(fleet.Selected.SuspensionReason));
                }
                finally { PanelLayout.EndCard(); }
                PanelLayout.BeginBody("remote-vessels");
                try
                {
                    ImGui.TextWrapped(L10n.Get("remote.hint"));
                    if (!fleet.AllowRemote) GameUiColors.MessageText(L10n.Get("remote.disabled"));
                    using (PanelLayout.Section("remote-nearby", "remote.nearby", "crosshair"))
                    {
                        if (fleet.Available.Count == 0) ImGui.TextWrapped(L10n.Get("remote.empty"));
                        foreach (var vessel in fleet.Available)
                        {
                            var session = fleet.Find(vessel);
                            ImGui.PushID(vessel.Id);
                            try
                            {
                                ImGui.TextColored(GameUiColors.Text, AutopilotWindow.FitHeaderText(vessel.Id, ImGui.GetContentRegionAvail().X));
                                ModWindowStyle.Hint(vessel.Id);
                                if (ReferenceEquals(vessel, player)) ImGui.TextColored(GameUiColors.Muted, L10n.Get("remote.player"));
                                ImGui.TextColored(GameUiColors.Label, session?.OperationStatus ?? AutopilotWindow.StateName(AscentGuidance.AscentState.Idle));
                                if (session?.IsSuspended == true) GameUiColors.MessageText(L10n.Get(session.SuspensionReason));
                                bool current = ReferenceEquals(vessel, fleet.Selected.Vehicle);
                                ImGui.BeginDisabled(current || !fleet.AllowRemote);
                                try
                                {
                                    if (ImGui.Button(L10n.Get(current ? "remote.selected" : "remote.control") + "###select", new float2(-1, 0))) selected = vessel;
                                }
                                finally { ImGui.EndDisabled(); }
                                ImGui.Separator();
                            }
                            finally { ImGui.PopID(); }
                        }
                    }
                    bool waitingHeader = false;
                    foreach (var session in fleet.Sessions)
                    {
                        if (!session.IsSuspended || !session.IsActive || fleet.IsAvailable(session.Vehicle)) continue;
                        if (!waitingHeader)
                        { UiIcons.Text("timer", L10n.Get("remote.waiting"), GameUiColors.Label); waitingHeader = true; }
                        ImGui.TextWrapped(session.Vehicle.Id + " · " + session.OperationStatus);
                        GameUiColors.MessageText(L10n.Get(session.SuspensionReason));
                        ImGui.PushID(session.Vehicle.Id);
                        ImGui.BeginDisabled(session.PendingAction != null);
                        try
                        {
                            if (ImGui.Button(L10n.Get("remote.cancel") + "###cancel-operation")) session.PendingAction = "remote_cancel";
                        }
                        finally { ImGui.EndDisabled(); ImGui.PopID(); }
                    }
                }
                finally { PanelLayout.EndBody(); }
            }
            finally { ModWindowStyle.End(); }
            return selected;
        }
    }
}
