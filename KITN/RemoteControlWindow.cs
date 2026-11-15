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
            ImGui.SetNextWindowSizeConstraints(new float2(300, 160) * scale, new float2(1200, 1200) * scale);
            ImGuiCond condition = ImGuiCond.FirstUseEver;
            float2 size = new float2(420, 280) * scale;
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
                    ModWindowStyle.Hint(L10n.Get("remote.hint"));
                    Status(fleet.Selected);
                }
                finally { PanelLayout.EndCard(); }
                PanelLayout.BeginBody("remote-vessels");
                try
                {
                    if (!fleet.AllowRemote) GameUiColors.MessageText(L10n.Get("remote.disabled"));
                    int rows = 0;
                    foreach (var vessel in fleet.Available)
                    {
                        if (ReferenceEquals(vessel, fleet.Selected.Vehicle)) continue;
                        rows++;
                        var session = fleet.Find(vessel);
                        ImGui.PushID(vessel.Id);
                        try
                        {
                            float width = ImGui.GetContentRegionAvail().X;
                            float buttonWidth = Math.Max(80 * scale, ImGui.CalcTextSize(L10n.Get("remote.control")).X + 24 * scale);
                            float x = ImGui.GetCursorPosX();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextColored(GameUiColors.Text, AutopilotWindow.FitHeaderText(vessel.Id, width - buttonWidth - 8 * scale));
                            ModWindowStyle.Hint(ReferenceEquals(vessel, player) ? vessel.Id + " · " + L10n.Get("remote.player") : vessel.Id);
                            ImGui.SameLine(); ImGui.SetCursorPosX(x + width - buttonWidth);
                            ImGui.BeginDisabled(!fleet.AllowRemote);
                            try
                            {
                                if (ImGui.Button(L10n.Get("remote.control") + "###select", new float2(buttonWidth, 0))) selected = vessel;
                            }
                            finally { ImGui.EndDisabled(); }
                            if (session?.IsActive == true || session?.IsSuspended == true) Status(session);
                            ImGui.Separator();
                        }
                        finally { ImGui.PopID(); }
                    }
                    foreach (var session in fleet.Sessions)
                    {
                        if (!session.IsSuspended || !session.IsActive || fleet.IsAvailable(session.Vehicle)
                            || ReferenceEquals(session, fleet.Selected)) continue;
                        rows++;
                        ImGui.TextColored(GameUiColors.Text, AutopilotWindow.FitHeaderText(session.Vehicle.Id, ImGui.GetContentRegionAvail().X));
                        Status(session);
                        ImGui.PushID(session.Vehicle.Id);
                        ImGui.BeginDisabled(session.PendingAction != null);
                        try
                        {
                            if (ImGui.Button(L10n.Get("remote.cancel") + "###cancel-operation")) session.PendingAction = "remote_cancel";
                        }
                        finally { ImGui.EndDisabled(); ImGui.PopID(); }
                    }
                    if (rows == 0) ImGui.TextColored(GameUiColors.Muted, L10n.Get("remote.empty"));
                }
                finally { PanelLayout.EndBody(); }
            }
            finally { ModWindowStyle.End(); }
            return selected;
        }

        private static void Status(VesselControlSession session)
        {
            string status = session.IsSuspended ? L10n.Get("remote.paused") + " · " + session.OperationStatus : session.OperationStatus;
            ImGui.TextColored(session.IsSuspended ? GameUiColors.Pending : GameUiColors.Label,
                AutopilotWindow.FitHeaderText(status, ImGui.GetContentRegionAvail().X));
            if (session.IsSuspended) ModWindowStyle.Hint(L10n.Get(session.SuspensionReason));
        }
    }
}
