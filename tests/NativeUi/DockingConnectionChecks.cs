using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static DockingPort DockingTestPort()
    {
        var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part));
        Set(part, "Asmb2ParentAsmb", doubleQuat.Identity);
        var port = (DockingPort)RuntimeHelpers.GetUninitializedObject(typeof(DockingPort)); GC.SuppressFinalize(port);
        Set(port, "Parent", part);
        port.Connector = (Part.Connector)RuntimeHelpers.GetUninitializedObject(typeof(Part.Connector));
        port.Connector.Parent = part; port.Connector.Asmb2ParentAsmb = doubleQuat.Identity;
        return port;
    }

    private static void DockingConnectionChecks()
    {
        var parent = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(vehicle);
        var target = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(target);
        var plan = new FlightPlan(Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(),
            new double3(7e6, 0, 0), new double3(0, Math.Sqrt(parent.Mu / 7e6), 0), byte4.Zero), KeyHash.Zero);
        Set(vehicle, "FlightPlan", plan); Set(target, "FlightPlan", plan);
        var fc = new FlightComputer { Helm2Asmb = doubleQuat.Identity, Ctrl2Asmb = doubleQuat.Identity };
        Set(vehicle, "FlightComputer", fc); Set(target, "FlightComputer", new FlightComputer());
        Set(vehicle, "Body2Cce", FlightComputerAttitudeTrackTargetEx.YAW_FLIP);
        Set(target, "Body2Cce", doubleQuat.Identity);
        var ownPort = DockingTestPort(); var targetPort = DockingTestPort();
        var controller = New("RendezvousController");
        var inputsField = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
        void Setup(double distance = 0.4, double angle = 0, double speed = 0.08)
        {
            InputEvents.VehicleDockingInputBuffer.Reset();
            Set(controller, "_activeVehicle", vehicle); Set(controller, "Target", target);
            Set(controller, "OwnPort", ownPort); Set(controller, "TargetPort", targetPort);
            Set(controller, "CanMatch", true); Set(controller, "CanDock", true); Set(controller, "_matching", false);
            Set(controller, "_dockingRetrying", false); Set(controller, "_dockCommandSentAt", double.NaN);
            Set(controller, "_lastTime", 0.0); Set(controller, "_standOff", 45.0); Set(controller, "_acceleration", 0.02);
            Set(controller, "_minimumDeltaV", new float3(0.004f, 0.004f, 0.004f));
            Set(controller, "_relativePosition", new double3(distance, 0, 0));
            Set(controller, "_relativeVelocity", new double3(-speed, 0, 0));
            Set(controller, "_inertialRelativeVelocity", new double3(-speed, 0, 0));
            Set(controller, "Distance", distance); Set(controller, "PointingError", angle);
            Set(controller, "_targetPort2Cci", doubleQuat.Identity); Set(controller, "_dockingRollAngle", 0.0);
            Set(controller, "_savedComputer", fc); Set(controller, "_savedRate", 0.73f);
            Set(controller, "_savedAttitudeMode", FlightComputerAttitudeMode.Manual);
            Set(controller, "_savedRcs", FlightComputerRCSMode.Disabled);
        }
        try
        {
            foreach (var unsafeContact in new[] { (1.5, 0.0, 0.08), (0.4, 15.0, 0.08),
                (0.4, 0.0, 2.0), (0.4, 0.0, double.NaN) })
            {
                Setup(unsafeContact.Item1, unsafeContact.Item2, unsafeContact.Item3);
                Call(controller, "Update", vehicle, 1.0, true);
                Check(InputEvents.VehicleDockingInputBuffer.Count == 0, "Unsafe contact sends a forced docking command: " + unsafeContact);
                Call(controller, "Stop", false);
            }
            Setup(); Set(controller, "_matching", true); Call(controller, "Update", vehicle, 1.0, true);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 0, "Match velocity unexpectedly docks the craft");
            Call(controller, "Stop", false);
            Setup(); Call(controller, "Update", vehicle, 1.0, false);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 0, "Disallowed RCS still sends a docking command");
            Setup(); targetPort.Connector.Flags = Part.Connector.Flag.ToSurface;
            Call(controller, "Update", vehicle, 1.0, true);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 0, "Incompatible ports send a docking command");
            targetPort.Connector.Flags = 0; Call(controller, "Stop", false);
            Setup(); var occupiedBy = DockingTestPort();
            Check(Part.Connection.Connect(targetPort.Connector, occupiedBy.Connector), "Occupied port fixture failed");
            Call(controller, "Update", vehicle, 1.0, true);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 0, "An occupied target port receives a docking command");
            targetPort.Connector.Connection.Disconnect(); Call(controller, "Stop", false);

            Setup(); Call(controller, "Update", vehicle, 1.0, true);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 1, "Safe contact never sends the native Dock command");
            var request = InputEvents.VehicleDockingInputBuffer.Get()[0];
            Check(ReferenceEquals(request.Vehicle, vehicle) && ReferenceEquals(request.NearbyVehicle, target)
                && ReferenceEquals(request.DockingPort, ownPort) && ReferenceEquals(request.NearbyDockingPort, targetPort)
                && !request.Undock, "Native Dock command uses the wrong selected craft/ports");
            Call(controller, "Update", vehicle, 2.0, true);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 1
                && (bool)controller.GetType().GetProperty("IsActive").GetValue(controller)
                && controller.GetType().GetProperty("Phase").GetValue(controller).ToString() == "Docking"
                && ((ManualControlInputs)inputsField.GetValue(vehicle)).ThrusterCommandFlags == ThrusterMapFlags.None
                && fc.CustomAttitudeTarget == double3.Zero,
                "Pending docking duplicates input, reports premature success or keeps firing RCS");
            InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData { Vehicle = target, Undock = true });
            Call(controller, "Stop", false);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 1 && InputEvents.VehicleDockingInputBuffer.Get()[0].Undock,
                "Cancelling docking leaves its request queued or removes unrelated manual input");

            Setup(); Call(controller, "Update", vehicle, 1.0, true);
            InputEvents.VehicleDockingInputBuffer.Reset(); // Native input was consumed without connecting.
            Call(controller, "Update", vehicle, 2.0, true);
            Check(InputEvents.VehicleDockingInputBuffer.Count == 0
                && (bool)controller.GetType().GetProperty("IsActive").GetValue(controller),
                "Consumed docking input is sent again or reports unconfirmed success");
            Call(controller, "Update", vehicle, 6.0, true);
            Check(!(bool)controller.GetType().GetProperty("IsActive").GetValue(controller)
                && controller.GetType().GetProperty("Message").GetValue(controller).ToString() == "dock.connection_failed",
                "Unconfirmed native connection does not stop with its actual cause");
            foreach (string locale in new[] { "en-US", "de-DE" })
            {
                Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
                string text = (string)controller.GetType().GetProperty("MessageText").GetValue(controller);
                Check(text.Contains(locale == "de-DE" ? "nicht verbunden" : "did not connect") && text.Contains("5 s"),
                    "Unconfirmed connection lacks a specific localized error in " + locale);
            }
            var severity = Mod.GetType("KITN.MessageSeverity").GetMethod("IsError");
            Check((bool)severity.Invoke(null, new object[] { "dock.connection_failed" })
                && !(bool)severity.Invoke(null, new object[] { "dock.connecting" }), "Docking failure/progress colors are reversed");

            Setup(); Call(controller, "Update", vehicle, 1.0, true);
            InputEvents.VehicleDockingInputBuffer.Reset();
            // Supply the native connection result and combined craft to the controller.
            Check(Part.Connection.Connect(ownPort.Connector, targetPort.Connector), "Native port connection fixture failed");
            var combined = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(combined);
            var combinedFc = new FlightComputer { RateLimit = 9, RCSMode = FlightComputerRCSMode.Enabled };
            Set(combined, "FlightComputer", combinedFc);
            var tree = (PartTree)RuntimeHelpers.GetUninitializedObject(typeof(PartTree)); tree.OwningVehicle = combined;
            ownPort.Parent.FullPart.Tree = tree;
            inputsField.SetValue(combined, new ManualControlInputs { EngineOn = true, EngineThrottle = 1,
                ThrusterCommandFlags = ThrusterMapFlags.TranslateForward });
            Call(controller, "Update", combined, 2.0, true);
            Check(!(bool)controller.GetType().GetProperty("IsActive").GetValue(controller)
                && controller.GetType().GetProperty("Phase").GetValue(controller).ToString() == "Docked"
                && Math.Abs(combinedFc.RateLimit - 0.73f) < 1e-6 && combinedFc.RCSMode == FlightComputerRCSMode.Disabled
                && ((ManualControlInputs)inputsField.GetValue(combined)).ThrusterCommandFlags == ThrusterMapFlags.None
                && !((ManualControlInputs)inputsField.GetValue(combined)).EngineOn,
                "Confirmed connection does not finish docking and restore controls on the combined craft");
            Console.WriteLine("PASS native Dock input: safe selected ports, no duplicates, cancellation, EN/DE failure, confirmed connection and combined-craft restoration");
        }
        finally { InputEvents.VehicleDockingInputBuffer.Reset(); }
    }
}
