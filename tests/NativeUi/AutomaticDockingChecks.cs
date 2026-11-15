using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static void AutomaticDockingChecks()
    {
        var parent = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(vehicle);
        Set(vehicle, "FlightPlan", new FlightPlan(Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(),
            new double3(7e6, 0, 0), new double3(0, Math.Sqrt(parent.Mu / 7e6), 0), byte4.Zero), KeyHash.Zero));
        var fc = new FlightComputer { Helm2Asmb = doubleQuat.CreateFromAxisAngle(double3.UnitX, 0.3),
            Ctrl2Asmb = doubleQuat.CreateFromAxisAngle(double3.UnitY, 0.6), RCSMode = FlightComputerRCSMode.Disabled,
            AttitudeMode = FlightComputerAttitudeMode.Manual };
        Set(vehicle, "FlightComputer", fc);
        var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part));
        Set(part, "Asmb2ParentAsmb", doubleQuat.Identity);
        var port = (DockingPort)RuntimeHelpers.GetUninitializedObject(typeof(DockingPort)); GC.SuppressFinalize(port);
        Set(port, "Parent", part);
        port.Connector = (Part.Connector)RuntimeHelpers.GetUninitializedObject(typeof(Part.Connector));
        port.Connector.Parent = part;
        var controller = New("RendezvousController"); Set(controller, "OwnPort", port);
        var apply = controller.GetType().GetMethod("ApplyDockingAttitude", BindingFlags.Instance | BindingFlags.NonPublic);
        var targetUpdate = typeof(FlightComputer).GetMethod("UpdateAttitudeTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        var errorUpdate = typeof(FlightComputer).GetMethod("UpdateAttitudeTrackError", BindingFlags.Instance | BindingFlags.NonPublic);
        var trackAxis = typeof(FlightComputer).GetMethod("ComputeRcsTrackAxis", BindingFlags.Instance | BindingFlags.NonPublic);
        var deadbandType = trackAxis.GetParameters()[7].ParameterType.GetElementType();
        const float authority = 0.002f, inertia = 1000, quantum = 0.0001f;
        const double dt = 0.05;
        foreach (var scenario in new[] { (0.0, 0.0), (Math.PI / 2, 0.0), (Math.PI / 2, Math.PI / 2) })
        {
            port.Connector.Asmb2ParentAsmb = doubleQuat.CreateFromAxisAngle(double3.UnitY, scenario.Item1);
            var targetPort = doubleQuat.CreateFromAxisAngle(double3.UnitZ, 0.8);
            Set(controller, "_targetPort2Cci", targetPort); Set(controller, "_dockingRollAngle", scenario.Item2);
            var rotation = doubleQuat.Identity; var rates = double3.Zero;
            object[] deadbands = { Activator.CreateInstance(deadbandType), Activator.CreateInstance(deadbandType), Activator.CreateInstance(deadbandType) };
            double lastError = 180; bool settled = false;
            for (int step = 0; step < 18000; step++)
            {
                Set(vehicle, "Body2Cce", rotation); Set(vehicle, "BodyRates", rates);
                var desired = (doubleQuat)apply.Invoke(controller, new object[] { vehicle });
                Check(fc.RCSMode == FlightComputerRCSMode.Enabled && fc.AttitudeMode == FlightComputerAttitudeMode.Auto
                    && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.Custom
                    && Math.Abs(doubleQuat.Dot(fc.Ctrl2Asmb, fc.Helm2Asmb)) > 0.999999, "Autodocking does not enable automatic RCS or clear the old control frame");
                var nav = new FlightComputerNavigation { Parent = parent, PositionCci = vehicle.GetPositionCci(),
                    VelocityCci = vehicle.GetVelocityCci(), Body2Cci = rotation, Helm2Body = fc.Helm2Asmb,
                    BodyRates = rates, Time = Universe.GetElapsedTime(), Target = null };
                targetUpdate.Invoke(fc, new object[] { nav });
                Check(Math.Abs(doubleQuat.Dot(desired, fc.AttitudeTarget.Target2Cci)) > 0.999999,
                    $"Native Euler conversion changes the automatic docking orientation: desired={desired}, actual={fc.AttitudeTarget.Target2Cci}, angles={fc.CustomAttitudeTarget}, dot={doubleQuat.Dot(desired, fc.AttitudeTarget.Target2Cci)}");
                var currentControl = doubleQuat.Concatenate(fc.Ctrl2Asmb, rotation);
                errorUpdate.Invoke(fc, new object[] { currentControl, rates.Transform(fc.Ctrl2Asmb.Inverse()) });
                lastError = 2 * Math.Acos(Math.Clamp(Math.Abs(doubleQuat.Dot(currentControl, desired)), 0, 1)) * 180 / Math.PI;
                if (lastError < 1.5 && rates.Length() < 0.001) { settled = true; break; }
                double Axis(float angle, float rate, int axis)
                {
                    object[] args = { angle, rate, quantum, quantum * 2, quantum * quantum * 4 / authority,
                        inertia, authority, deadbands[axis], 0f, float.PositiveInfinity };
                    trackAxis.Invoke(fc, args); deadbands[axis] = args[7];
                    return Math.Clamp((float)args[8] / inertia, -authority * dt, authority * dt);
                }
                var change = new double3(Axis(fc.ErrorAngles.X, fc.ErrorRates.X, 0), Axis(fc.ErrorAngles.Y, fc.ErrorRates.Y, 1),
                    Axis(fc.ErrorAngles.Z, fc.ErrorRates.Z, 2)).Transform(fc.Ctrl2Asmb);
                rates += change;
                if (rates.Length() > 1e-12)
                    rotation = doubleQuat.Normalize(doubleQuat.Concatenate(rotation,
                        doubleQuat.CreateFromAxisAngle(rates.Normalized().Transform(rotation), rates.Length() * dt)));
            }
            Check(settled, $"Native RCS cannot automatically align initially misaligned port {scenario}; remaining={lastError:F3} degrees");
            var actualPort = doubleQuat.Concatenate(port.Connector.Asmb2VehicleAsmb, rotation);
            Check((double3.UnitX.Transform(actualPort) + double3.UnitX.Transform(targetPort)).Length() < 0.03,
                "Automatic alignment settles with ports facing the wrong way");
            Console.WriteLine($"PASS native automatic docking orientation: mount={scenario.Item1 * 180 / Math.PI:F0}°, roll={scenario.Item2 * 180 / Math.PI:F0}°, error={lastError:F3}°");
        }
        var rollOffset = controller.GetType().GetMethod("DockingRollOffset", BindingFlags.Static | BindingFlags.NonPublic);
        var currentPort = doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, 0.9), FlightComputerAttitudeTrackTargetEx.YAW_FLIP);
        var retainedRoll = (double)rollOffset.Invoke(null, new object[] { currentPort, doubleQuat.Identity });
        var retainedTarget = doubleQuat.Concatenate(FlightComputerAttitudeTrackTargetEx.YAW_FLIP,
            doubleQuat.CreateFromAxisAngle(double3.UnitX, retainedRoll));
        Check(Math.Abs(doubleQuat.Dot(currentPort, retainedTarget)) > 0.999999,
            "Disabling forced roll changes the initially aligned port roll");
        Console.WriteLine("PASS disabling forced roll retains the initial port roll without repeated rotation");
        // Exercise the actual controller update: a port can face the target while roll is
        // still reversed. Reject this large error, then resume translation by itself.
        port.Connector.Asmb2ParentAsmb = doubleQuat.Identity;
        var targetVehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(targetVehicle);
        Set(targetVehicle, "FlightPlan", vehicle.FlightPlan); Set(targetVehicle, "Body2Cce", doubleQuat.Identity);
        Set(controller, "Target", targetVehicle); Set(controller, "_activeVehicle", vehicle);
        Set(controller, "CanMatch", true); Set(controller, "CanDock", true);
        Set(controller, "_relativePosition", new double3(100, 0, 0)); Set(controller, "_relativeVelocity", double3.Zero);
        Set(controller, "_targetPort2Cci", doubleQuat.Identity); Set(controller, "_dockingRollAngle", 0.0);
        Set(controller, "_standOff", 45.0); Set(controller, "_acceleration", 0.02);
        Set(controller, "_minimumDeltaV", new float3(0.004f, 0.004f, 0.004f)); Set(controller, "PointingError", 0.0);
        Set(controller, "_lastTime", 0.0); Set(vehicle, "BodyRates", double3.Zero);
        var desiredBody = FlightComputerAttitudeTrackTargetEx.YAW_FLIP;
        Set(vehicle, "Body2Cce", doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, Math.PI), desiredBody));
        Call(controller, "Update", vehicle, 1.0, true);
        var inputsField = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(controller.GetType().GetProperty("Phase").GetValue(controller).ToString() == "Correcting"
            && ((ManualControlInputs)inputsField.GetValue(vehicle)).ThrusterCommandFlags == ThrusterMapFlags.None,
            "Autodocking closes before correcting a reversed port roll");
        Set(vehicle, "Body2Cce", desiredBody); Call(controller, "Update", vehicle, 2.0, true);
        Check(controller.GetType().GetProperty("Phase").GetValue(controller).ToString() == "Approaching"
            && ((ManualControlInputs)inputsField.GetValue(vehicle)).ThrusterCommandFlags != ThrusterMapFlags.None,
            "Autodocking does not resume translation automatically after full alignment");
        Set(controller, "_activeVehicle", null);
        Console.WriteLine("PASS actual Autodock update rejects reversed roll and resumes RCS approach automatically");
        Set(controller, "_activeVehicle", vehicle);
        fc.RateBit = new float3(0.009f, 0.009f, 0.009f);
        Set(vehicle, "BodyRates", new double3(0.005, 0, 0));
        Call(controller, "Update", vehicle, 3.0, true);
        Check(fc.AttitudeMode == FlightComputerAttitudeMode.Manual && fc.RCSMode == FlightComputerRCSMode.Enabled,
            "Aligned coarse RCS does not coast independently of translation");
        Set(vehicle, "BodyRates", new double3(0.1, 0, 0));
        Call(controller, "Update", vehicle, 4.0, true);
        Check(fc.AttitudeMode == FlightComputerAttitudeMode.Auto, "Rotation coasting suppresses required high-rate braking");
        Console.WriteLine("PASS coarse docking rotation coasts within tolerance and immediately resumes native braking for excessive rates");
        Set(controller, "_activeVehicle", null);
        ResidualNativeDockingChecks(controller, vehicle, targetVehicle, port, fc);
        DockingRecoveryFeedbackChecks(controller, vehicle);
        DockingConnectionChecks();
        RendezvousRateHoldChecks();
        ProximityPageChecks(controller);
        DockingEngineChecks();
    }

    private static void DockingRecoveryFeedbackChecks(object controller, Vehicle vehicle)
    {
        Set(controller, "_activeVehicle", vehicle); Set(controller, "_lastTime", 0.0);
        Set(controller, "_dockingRetrying", false); Set(controller, "CanMatch", true); Set(controller, "CanDock", true);
        Set(controller, "_relativePosition", new double3(37.6, 0.38, 0)); Set(controller, "_relativeVelocity", new double3(-0.3, 0, 0));
        Set(controller, "Distance", 37.6); Set(controller, "PointingError", 0.1); Set(vehicle, "BodyRates", double3.Zero);
        Call(controller, "Update", vehicle, 5.0, true);
        Check((bool)controller.GetType().GetProperty("IsActive").GetValue(controller)
            && controller.GetType().GetProperty("Message").GetValue(controller).ToString() == "dock.correcting_lateral",
            "A small offset does not explain its in-place correction");
        Call(controller, "Update", vehicle, 20.0, true); Call(controller, "Update", vehicle, 40.0, true);
        Check(!(bool)controller.GetType().GetField("_dockingRetrying", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller),
            "Docking retries before trying a correctable approach long enough");
        Call(controller, "Update", vehicle, 110.0, true);
        Check((bool)controller.GetType().GetProperty("IsActive").GetValue(controller)
            && controller.GetType().GetProperty("Message").GetValue(controller).ToString() == "dock.retry_lateral",
            "A stalled correction retries without a specific lateral reason");
        foreach (string locale in new[] { "en-US", "de-DE" })
        {
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            string text = (string)controller.GetType().GetProperty("MessageText").GetValue(controller);
            Check(text.Contains(locale == "de-DE" ? "Seitenversatz" : "lateral offset") && text.Contains("105"),
                "Retry reason omits the issue or failed correction duration in " + locale);
        }
        Set(controller, "_relativePosition", new double3(45, 0.38, 0)); Set(controller, "_relativeVelocity", double3.Zero);
        Call(controller, "Update", vehicle, 111.0, true);
        Check(controller.GetType().GetProperty("LastDockingRecoveryText").GetValue(controller) != null,
            "Retry explanation disappears when the new approach resumes");
        Set(controller, "CanMatch", false); Set(controller, "UnavailableReason", "rendezvous.require_physics");
        Call(controller, "Update", vehicle, 112.0, true);
        Check((bool)controller.GetType().GetProperty("IsActive").GetValue(controller), "Transient control loss immediately aborts docking");
        Set(controller, "CanMatch", true); Call(controller, "Update", vehicle, 114.0, true);
        Set(controller, "CanMatch", false); Call(controller, "Update", vehicle, 115.0, true); Call(controller, "Update", vehicle, 121.0, true);
        string problem = (string)Mod.GetType("KITN.L10n").GetMethod("Get").Invoke(null, new object[] { "rendezvous.require_physics" });
        Check(!(bool)controller.GetType().GetProperty("IsActive").GetValue(controller)
            && ((string)controller.GetType().GetProperty("MessageText").GetValue(controller)).Contains(problem),
            "Persistent loss of control stops without naming its real cause");
        Console.WriteLine("PASS native docking corrects first, waits before retry, preserves EN/DE retry reasons, and explains persistent control loss");
    }

    private static void ResidualNativeDockingChecks(object controller, Vehicle vehicle, Vehicle target, DockingPort port, FlightComputer fc)
    {
        ((PlannerBodyProxy)vehicle.Parent).AtmosphereRadius = vehicle.Parent.MeanRadius + 100800;
        var rcs = New("RcsController");
        var targetUpdate = typeof(FlightComputer).GetMethod("UpdateAttitudeTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        var errorUpdate = typeof(FlightComputer).GetMethod("UpdateAttitudeTrackError", BindingFlags.Instance | BindingFlags.NonPublic);
        var trackAxis = typeof(FlightComputer).GetMethod("ComputeRcsTrackAxis", BindingFlags.Instance | BindingFlags.NonPublic);
        var deadbandType = trackAxis.GetParameters()[7].ParameterType.GetElementType();
        var inputsField = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
        const double dt = 0.05; const float authority = 1.38f, inertia = 1000;
        var targetPort = DockingTestPort();
        Set(controller, "TargetPort", targetPort);
        foreach (double mount in new[] { 0.0, Math.PI / 2 })
        foreach (double translationQuantum in new[] { 0.004, 0.04, 0.1 })
        {
            float quantum = translationQuantum > 0.004 ? 0.009f : 0.003f;
            port.Connector.Asmb2ParentAsmb = doubleQuat.CreateFromAxisAngle(double3.UnitY, mount);
            var initialPort = doubleQuat.Concatenate(doubleQuat.Concatenate(
                doubleQuat.CreateFromAxisAngle(double3.UnitX, 25 * Math.PI / 180),
                doubleQuat.CreateFromAxisAngle(double3.UnitY, 4 * Math.PI / 180)), FlightComputerAttitudeTrackTargetEx.YAW_FLIP);
            var rotation = doubleQuat.Concatenate(port.Connector.Asmb2VehicleAsmb.Inverse(), initialPort);
            var rates = double3.Zero; var position = new double3(55, 0.08, 0); var velocity = double3.Zero;
            Set(controller, "_activeVehicle", vehicle); Set(controller, "_lastTime", 0.0);
            Set(controller, "_dockingSafeDistance", 35.0); Set(controller, "_dockingEntryCleared", false);
            Set(controller, "_minimumDeltaV", new float3((float)translationQuantum, (float)translationQuantum, (float)translationQuantum));
            fc.LastThrusterPulseTime = new UniverseTime(0);
            Set(controller, "Phase", Enum.Parse(Mod.GetType("KITN.DockingPhase"), "Idle"));
            Set(controller, "_targetPort2Cci", doubleQuat.Identity); Set(controller, "_dockingRollAngle", 0.0);
            fc.RcsTorqueAuthority = new float3(authority, authority, authority);
            fc.TvcTorqueAuthority = float3.Zero;
            object[] deadbands = { Activator.CreateInstance(deadbandType), Activator.CreateInstance(deadbandType), Activator.CreateInstance(deadbandType) };
            bool contact = false, admittedOutside = false; double largestFinalAngle = 0;
            int translationPulses = 0;
            for (int step = 0; step < 80000; step++)
            {
                Set(vehicle, "Body2Cce", rotation); Set(vehicle, "BodyRates", rates);
                Set(controller, "_relativePosition", position); Set(controller, "_relativeVelocity", velocity);
                Set(controller, "_inertialRelativeVelocity", velocity);
                Set(controller, "Distance", position.Length());
                var portToCci = doubleQuat.Concatenate(port.Connector.Asmb2VehicleAsmb, rotation);
                double angle = Math.Acos(Math.Clamp(double3.Dot(double3.UnitX.Transform(portToCci), -double3.UnitX), -1, 1)) * 180 / Math.PI;
                Set(controller, "PointingError", angle);
                // Match KSA's coarse pulse switch-line windows, unlike the fine-jet fixture.
                fc.RateBit = new float3(quantum, quantum, quantum);
                fc.AngleTurnaround = new float3(10 * quantum, 10 * quantum, 10 * quantum);
                Call(controller, "Update", vehicle, 5 + step * dt, true);
                if (InputEvents.VehicleDockingInputBuffer.Count > 0)
                {
                    var request = InputEvents.VehicleDockingInputBuffer.Get()[0];
                    Check(ReferenceEquals(request.Vehicle, vehicle) && ReferenceEquals(request.NearbyVehicle, target)
                        && ReferenceEquals(request.DockingPort, port) && ReferenceEquals(request.NearbyDockingPort, targetPort)
                        && !request.Undock && position.Length() < 0.5 && angle < 10 && velocity.Length() < 0.15,
                        "Coarse native RCS sends docking input for the wrong ports or outside safe contact");
                    contact = true; break;
                }
                Call(rcs, "Update", vehicle, true, true, false, true);
                var control = doubleQuat.Concatenate(fc.Ctrl2Asmb, rotation);
                var nav = new FlightComputerNavigation { Parent = vehicle.Parent, PositionCci = vehicle.GetPositionCci(),
                    VelocityCci = vehicle.GetVelocityCci(), Body2Cci = rotation, Helm2Body = fc.Helm2Asmb, BodyRates = rates,
                    Time = Universe.GetElapsedTime(), Target = null };
                targetUpdate.Invoke(fc, new object[] { nav });
                errorUpdate.Invoke(fc, new object[] { control, rates.Transform(fc.Ctrl2Asmb.Inverse()) });
                if (step % 3 == 0 && fc.AttitudeMode == FlightComputerAttitudeMode.Auto)
                {
                    double Axis(float error, float rate, int axis)
                    {
                        object[] args = { error, rate, quantum, quantum, 10 * quantum, inertia, authority, deadbands[axis], 0f, float.PositiveInfinity };
                        trackAxis.Invoke(fc, args); deadbands[axis] = args[7];
                        double change = (float)args[8] / inertia;
                        return Math.Sign(change) * Math.Ceiling(Math.Abs(change) / quantum) * quantum;
                    }
                    rates += new double3(Axis(fc.ErrorAngles.X, fc.ErrorRates.X, 0), Axis(fc.ErrorAngles.Y, fc.ErrorRates.Y, 1),
                        Axis(fc.ErrorAngles.Z, fc.ErrorRates.Z, 2)).Transform(fc.Ctrl2Asmb);
                }
                if (rates.Length() > 1e-12) rotation = doubleQuat.Normalize(doubleQuat.Concatenate(rotation,
                    doubleQuat.CreateFromAxisAngle(rates.Normalized().Transform(rotation), rates.Length() * dt)));
                var inputs = (ManualControlInputs)inputsField.GetValue(vehicle);
                if (step % 4 == 0)
                {
                    double Sign(ThrusterMapFlags positive, ThrusterMapFlags negative) => ((inputs.ThrusterCommandFlags & positive) != 0 ? 1 : 0)
                        - ((inputs.ThrusterCommandFlags & negative) != 0 ? 1 : 0);
                    velocity += new double3(Sign(ThrusterMapFlags.TranslateForward, ThrusterMapFlags.TranslateBackward),
                        Sign(ThrusterMapFlags.TranslateRight, ThrusterMapFlags.TranslateLeft),
                        Sign(ThrusterMapFlags.TranslateDown, ThrusterMapFlags.TranslateUp)).Transform(control) * translationQuantum;
                    if (inputs.ThrusterCommandFlags != ThrusterMapFlags.None)
                    {
                        translationPulses++;
                        fc.LastThrusterPulseTime = new UniverseTime(5 + step * dt);
                    }
                }
                double fullError = 2 * Math.Acos(Math.Clamp(Math.Abs(doubleQuat.Dot(control, fc.AttitudeTarget.Target2Cci)), 0, 1)) * 180 / Math.PI;
                admittedOutside |= position.X >= 44.75 && fullError < 5.5
                    && (bool)controller.GetType().GetField("_dockingEntryCleared", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                if (position.Length() < 35)
                    Check((bool)controller.GetType().GetField("_dockingEntryCleared", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller)
                        && Math.Sqrt(position.Y * position.Y + position.Z * position.Z) < 0.3 && fullError < 6,
                        $"Native RCS enters station clearance before outside alignment or leaves the docking corridor: mount={mount}, quantum={translationQuantum}, position={position}, fullError={fullError}, velocity={velocity}");
                if (position.X < 3) largestFinalAngle = Math.Max(largestFinalAngle, angle);
                position += velocity * dt;
                Check(!inputs.EngineOn && inputs.EngineThrottle == 0, "Residual docking commands the main engines");
                Check(position.X > -0.5, "Coarse native pulses fly past the docking connector");
            }
            Call(controller, "Stop", false); Call(rcs, "Restore");
            Check(contact && admittedOutside, $"Coarse native RCS approach failed: mount={mount}, contact={contact}, admittedOutside={admittedOutside}, distance={position.Length():F2} m");
            Check(largestFinalAngle < 10, "Residual native RCS enters contact outside KSA's port-angle tolerance");
            Console.WriteLine($"PASS coupled native coarse RCS attitude/translation sends Dock input: mount={mount * 180 / Math.PI:F0}°, pulse={translationQuantum:F3} m/s, contact={position.Length():F3} m, final angle <= {largestFinalAngle:F2}°, translation pulses={translationPulses}");
        }
        Set(controller, "_dockingSafeDistance", 0.0);
        Set(controller, "TargetPort", null);
    }

    private static void ProximityPageChecks(object controller)
    {
        var page = Mod.GetType("KITN.ProximitySettingsPage");
        foreach (string locale in new[] { "en-US", "de-DE" })
        foreach (string mode in new[] { "Rendezvous", "Docking" })
        {
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            Set(controller, mode == "Rendezvous" ? "RendezvousOptions" : "DockingOptions", New(mode + "Settings"));
            var io = ImGui.GetIO(); io.AddMousePosEvent(-1000, -1000);
            void Draw()
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new float2(12, 12), ImGuiCond.Always); ImGui.SetNextWindowSize(new float2(580, 950), ImGuiCond.Always);
                ImGui.SetNextWindowFocus();
                ImGui.Begin("proximity-settings-test");
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                page.GetMethod(mode).Invoke(null, new object[] { controller, Store, false });
                ImGui.End(); ImGui.Render();
            }
            Draw(); Draw();
            Save($"settings-{mode}-{locale}.png", 580, 950, 12, 12);
            Check(ImGui.GetDrawData().TotalVtxCount > 500, "Proximity settings panel has no controls");
            // First slider is in the row immediately after the header.
            string property = mode == "Rendezvous" ? "RendezvousOptions" : "DockingOptions";
            string setting = mode == "Rendezvous" ? "FinalDistanceM" : "MaximumSpeedMps";
            float Before() => (float)controller.GetType().GetProperty(property).GetValue(controller).GetType().GetProperty(setting)
                .GetValue(controller.GetType().GetProperty(property).GetValue(controller));
            float before = Before();
            io.AddMousePosEvent(locale == "en-US" ? 460 : 480, 73); Draw(); io.AddMouseButtonEvent(0, true); Draw(); io.AddMouseButtonEvent(0, false); Draw();
            Check(Before() != before, "Settings slider cannot be changed at minimum width in " + mode + " / " + locale);
            if (mode == "Docking")
            {
                var options = controller.GetType().GetProperty(property).GetValue(controller);
                Check(!(bool)options.GetType().GetProperty("EngineAssist").GetValue(options), "Engine assist UI is on by default");
                io.AddMousePosEvent(538, 139); Draw(); io.AddMouseButtonEvent(0, true); Draw(); io.AddMouseButtonEvent(0, false); Draw();
                options = controller.GetType().GetProperty(property).GetValue(controller);
                Check((bool)options.GetType().GetProperty("EngineAssist").GetValue(options), "Engine assist toggle is not clickable in " + locale);
                Save($"engine-assist-enabled-{locale}.png", 580, 950, 12, 12);
            }
            Check(Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "UI-edited proximity settings are not persisted");
            // Verify the same edit is rejected while automation owns the craft.
            Set(controller, "_activeVehicle", (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)));
            before = Before(); io.AddMousePosEvent(420, 73); Draw(); io.AddMouseButtonEvent(0, true); Draw(); io.AddMouseButtonEvent(0, false); Draw();
            Check(Before() == before, "Active automation settings remain editable"); Set(controller, "_activeVehicle", null);
            Console.WriteLine("PASS clickable and persistent " + mode + " settings at minimum width in " + locale + "; locked while active");
        }
    }
}
