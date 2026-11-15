using System;
using KSA;

namespace KITN
{
    internal sealed partial class RendezvousController
    {
        private double _dockCommandSentAt = double.NaN;

        private bool TryRequestDocking(Vehicle vehicle, double now, DockingCommand command)
        {
            if (command.Phase != DockingPhase.Docking || command.Issue != DockingIssue.None
                || OwnPort?.Connector == null || TargetPort?.Connector == null
                || OwnPort.Docked || TargetPort.Docked || Target == null || ReferenceEquals(vehicle, Target)
                || !OwnPort.Connector.CanConnect(TargetPort.Connector)
                || !TargetPort.Connector.CanConnect(OwnPort.Connector)
                || !_relativePosition.IsFinite() || !double.IsFinite(PointingError)
                || _relativePosition.X < -0.1
                || !OwnPort.WithinPositionTolerance(_relativePosition)
                || !OwnPort.WithinAngleTolerance(Math.PI - PointingError * FlightAttitude.DEG_TO_RAD))
                return false;

            double speed = _inertialRelativeVelocity.Length();
            double contactSpeed = Math.Min(DockingOptions.MaximumSpeedMps,
                Math.Min(DockingOptions.NearSpeedMps, DockingOptions.ContactSpeedMps));
            // Allow a minimum-impulse excursion without accepting a fast flyby.
            double speedLimit = Math.Min(0.25, contactSpeed + Math.Max(0.005, _minimumDeltaV.Length()));
            if (!double.IsFinite(speed) || !double.IsFinite(speedLimit) || speed > speedLimit) return false;

            // Use the context menu's input event, rather than merging vehicles here.
            // KSA applies it next frame after joining physics workers and updates
            // the controlled craft, camera and orbit view along with the connection.
            InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData
            {
                Vehicle = vehicle,
                DockingPort = OwnPort,
                NearbyVehicle = Target,
                NearbyDockingPort = TargetPort,
                Undock = false
            });
            _dockCommandSentAt = now;
            HoldDockingConnection(vehicle);
            Diag.Info("DOCKING", $"event=dock_requested distance_m={_relativePosition.Length():F3} port_angle_deg={PointingError:F2} relative_speed_mps={speed:F3}");
            return true;
        }

        private bool WaitForDockingConnection(Vehicle vehicle, double now)
        {
            if (!double.IsFinite(_dockCommandSentAt)) return false;
            HoldDockingConnection(vehicle);
            if (now - _dockCommandSentAt >= 5)
            {
                Stop();
                SetDockingMessage(new LocalizedMessage("dock.connection_failed", Distance, PointingError));
                Diag.Warn("DOCKING", $"event=dock_unconfirmed reason=\"{MessageText}\"");
            }
            return true;
        }

        private void HoldDockingConnection(Vehicle vehicle)
        {
            SetInputs(vehicle, ThrusterMapFlags.None);
            FlightAttitude.StopTracking(vehicle.FlightComputer);
            Phase = DockingPhase.Docking;
            DockingPreview.Connecting(Vector(_relativePosition));
            SetDockingMessage(new LocalizedMessage("dock.connecting"));
        }

        private void CancelDockingRequest()
        {
            if (!double.IsFinite(_dockCommandSentAt)) return;
            // Keep other input events, including manual docking and undocking.
            var buffer = InputEvents.VehicleDockingInputBuffer.Get().ToArray();
            InputEvents.VehicleDockingInputBuffer.Reset();
            foreach (var input in buffer)
                if (input.Undock || !ReferenceEquals(input.Vehicle, _activeVehicle)
                    || !ReferenceEquals(input.DockingPort, OwnPort)
                    || !ReferenceEquals(input.NearbyVehicle, Target)
                    || !ReferenceEquals(input.NearbyDockingPort, TargetPort))
                    InputEvents.VehicleDockingInputBuffer.Add(input);
            _dockCommandSentAt = double.NaN;
        }
    }
}
