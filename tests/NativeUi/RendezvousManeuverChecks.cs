using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static void RendezvousManeuverChecks()
    {
        object Vector(double3 value) => New("LandingVector", value.X, value.Y, value.Z);
        double Number(object value, string name) => (double)(value.GetType().GetField(name)?.GetValue(value)
            ?? value.GetType().GetProperty(name).GetValue(value));
        double3 Native(object value) => new double3(Number(value, "X"), Number(value, "Y"), Number(value, "Z"));
        var parent = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        ((PlannerBodyProxy)parent).AtmosphereRadius = parent.GetNearSurfaceRadius() + 100800;
        var meshes = typeof(ModLibrary).GetField("AllMeshes", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        foreach (string id in new[] { "Sphere", "ConeWithTailMesh", "ConeMesh" })
        {
            var mesh = new MeshReference { Id = id }; mesh.SetHash();
            meshes.GetType().GetMethod("Register").Invoke(meshes, new object[] { mesh });
        }
        RendezvousPropulsionChecks(parent);
        Orbit Circle(double radius, double phase, double inclination = 0)
        {
            double speed = Math.Sqrt(parent.Mu / radius);
            return Orbit.CreateFromStateCci(parent, new UniverseTime(0),
                new double3(radius * Math.Cos(phase), radius * Math.Sin(phase) * Math.Cos(inclination), radius * Math.Sin(phase) * Math.Sin(inclination)),
                new double3(-speed * Math.Sin(phase), speed * Math.Cos(phase) * Math.Cos(inclination), speed * Math.Cos(phase) * Math.Sin(inclination)), byte4.Zero);
        }
        object Ephemeris(Orbit orbit)
        {
            var state = orbit.GetStateVectorsAt(new UniverseTime(0));
            return New("TransferOrbit", parent.Mu, 0.0, Vector(state.PositionCci), Vector(state.VelocityCci));
        }
        var phases = new[] { new FiniteBurnPhase(955522.3125, 222.89703369140625, 52813.765625, 49474.73046875 / 222.89703369140625) };
        FiniteBurnPredictor.Duration(phases, 1e6, out double budget);
        Check(double.IsFinite(budget) && budget > 1000, "Native staged burn does not provide a finite rendezvous budget");
        var own = Circle(7e6, 0);
        object firstSolution = null; Orbit firstCoast = null; Orbit firstTarget = null;
        foreach (var scenario in new[] { (7e6, 0.5, 0.0), (7.2e6, 1.0, 0.0), (7.4e6, 0.8, 0.1), (7e6, 0.0004, 0.0) })
        {
            var target = Circle(scenario.Item1, scenario.Item2, scenario.Item3);
            var request = New("RendezvousRequest");
            Set(request, "Own", Ephemeris(own)); Set(request, "Target", Ephemeris(target));
            Set(request, "Mu", parent.Mu); Set(request, "SafetyRadius", parent.GetNearSurfaceRadius() + 100800);
            Set(request, "Soi", parent.SphereOfInfluence); Set(request, "Budget", budget); Set(request, "Close", scenario.Item2 < 0.01);
            var burnRequest = New("TransferRequest");
            Set(burnRequest, "Parking", request.GetType().GetField("Own").GetValue(request));
            Set(burnRequest, "CentralMu", parent.Mu); Set(burnRequest, "DepartureSafetyRadius", parent.GetNearSurfaceRadius() + 100800);
            Mod.GetType("KITN.TransferBurnRefiner").GetMethod("Attach").Invoke(null, new object[] { burnRequest, phases });
            Set(request, "PredictBurn", burnRequest.GetType().GetField("PredictBurn").GetValue(burnRequest));
            object solution = Mod.GetType("KITN.RendezvousMath").GetMethod("Search").Invoke(null, new object[] { request, CancellationToken.None });
            Check((bool)solution.GetType().GetField("Valid").GetValue(solution), "Native finite rendezvous has no route for " + scenario);
            double departure = Number(solution, "Departure"), arrival = Number(solution, "Arrival");
            var delta = Native(solution.GetType().GetField("DepartureDelta").GetValue(solution));
            var epoch = new UniverseTime(departure);
            var burn = FiniteBurnPredictor.Predict(own, epoch, OrbitalTransfers.CciToVlf(own, epoch, delta), phases);
            var coast = Orbit.CreateFromStateCci(parent, burn.Cutoff, burn.PositionCci, burn.VelocityCci, byte4.Zero);
            var arrived = coast.GetStateVectorsAt(new UniverseTime(arrival));
            var targetAtArrival = target.GetStateVectorsAt(new UniverseTime(arrival));
            var aimed = targetAtArrival.PositionCci * (1 + 500 / targetAtArrival.PositionCci.Length());
            double miss = (arrived.PositionCci - aimed).Length();
            Check(miss < 100, $"Native finite departure misses stand-off by {miss:F2} m for {scenario}");
            // Recompute arrival with native finite burn and mass consumed in departure, then verify local approach is reachable.
            double elapsedBurn = burn.Cutoff.Seconds() - burn.Ignition.Seconds();
            var remaining = new[] { new FiniteBurnPhase(955522.3125, 222.89703369140625,
                52813.765625 - elapsedBurn * 222.89703369140625, 49474.73046875 / 222.89703369140625 - elapsedBurn) };
            var match = targetAtArrival.VelocityCci - arrived.VelocityCci;
            var brake = FiniteBurnPredictor.Predict(coast, new UniverseTime(arrival), OrbitalTransfers.CciToVlf(coast, new UniverseTime(arrival), match), remaining);
            var targetAtCutoff = target.GetStateVectorsAt(brake.Cutoff);
            double distance = (brake.PositionCci - targetAtCutoff.PositionCci).Length();
            double relativeSpeed = (brake.VelocityCci - targetAtCutoff.VelocityCci).Length();
            Check(distance > 30 && distance < 2000 && relativeSpeed < 2, $"Native braking cannot enter safe local approach: {distance:F2} m, {relativeSpeed:F3} m/s");
            for (int i = 0; i <= 32; i++)
            {
                double time = brake.Ignition.Seconds() + (brake.Cutoff.Seconds() - brake.Ignition.Seconds()) * i / 32;
                Check((brake.Sample(i).PositionCci - target.GetStateVectorsAt(new UniverseTime(time)).PositionCci).Length() > 30,
                    "Finite rendezvous braking passes through target");
            }
            Console.WriteLine($"PASS native finite rendezvous {scenario}: dv={Number(solution, "Cost"):F2} m/s, miss={miss:F3} m, after braking={distance:F2} m / {relativeSpeed:F3} m/s");
            if (firstSolution == null) { firstSolution = solution; firstCoast = coast; firstTarget = target; }
        }
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        var targetVehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        Set(vehicle, "Id", "Rendezvous test rocket"); Set(targetVehicle, "Id", "Station");
        var fc = new FlightComputer { BurnMode = FlightComputerBurnMode.Manual, RCSMode = FlightComputerRCSMode.Enabled };
        Set(vehicle, "FlightComputer", fc); Set(vehicle, "FlightPlan", new FlightPlan(own, KeyHash.Zero));
        Set(targetVehicle, "FlightPlan", new FlightPlan(firstTarget, KeyHash.Zero));
        object controller = New("RendezvousController"); var type = controller.GetType();
        object Invoke(string name, params object[] arguments) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, arguments);
        Set(controller, "Target", targetVehicle); Set(controller, "_activeVehicle", vehicle); Set(controller, "_rendezvousParent", parent);
        var snapshot = Invoke("SnapshotRendezvous", vehicle, 0.0, phases);
        Check((bool)snapshot.GetType().GetProperty("Valid").GetValue(snapshot) && Number(snapshot, "Budget") > 1000,
            "Native orbital snapshot loses the target or staged fuel budget");
        var predict = (Delegate)snapshot.GetType().GetField("PredictBurn").GetValue(snapshot);
        var rushed = predict.DynamicInvoke(240.0, Vector(new double3(5000, 0, 0)), CancellationToken.None);
        Check(!(bool)rushed.GetType().GetField("Valid").GetValue(rushed),
            "Finite departure starts before enough alignment time remains");
        Set(controller, "_rendezvousPlan", firstSolution);
        double firstTime = Number(firstSolution, "Departure");
        var firstDelta = Native(firstSolution.GetType().GetField("DepartureDelta").GetValue(firstSolution));
        var node = (Burn)Invoke("CreateRendezvousNode", vehicle, vehicle.FlightPlan, firstTime, firstDelta);
        Set(controller, "_rendezvousDeparture", node);
        Check((bool)Invoke("CreateArrivalNode", vehicle, new FlightPlan(firstCoast, KeyHash.Zero)), "Native arrival node cannot be created");
        Check(fc.BurnPlan.BurnCount == 2 && ReferenceEquals(fc.BurnPlan.FindFirstExecutableBurn(), node), "Rendezvous node order is wrong");
        Check(!(bool)Invoke("HasForeignNodes", vehicle), "Owned nodes mistaken for foreign maneuvers");
        var foreign = (Burn)Invoke("CreateRendezvousNode", vehicle, vehicle.FlightPlan, firstTime + 60, firstDelta);
        Check((bool)Invoke("HasForeignNodes", vehicle), "Foreign maneuver is not detected");
        using var cancellation = new CancellationTokenSource();
        Set(controller, "_rendezvousCancellation", cancellation); Set(controller, "_rendezvousRunning", true);
        Set(controller, "_savedComputer", fc); Set(controller, "_savedRate", 0.2f); Set(controller, "_savedDeadband", 0.02f);
        Set(controller, "_savedRoll", FlightComputerRollMode.Decoupled); Set(controller, "_savedThrust", FlightComputerManualThrustMode.Direct);
        Set(controller, "_savedRcs", FlightComputerRCSMode.Disabled);
        var token = cancellation.Token; Call(controller, "Stop", false);
        Check(token.IsCancellationRequested && !(bool)type.GetProperty("IsActive").GetValue(controller), "Stop leaves rendezvous planning active");
        Check(fc.BurnPlan.BurnCount == 1 && fc.BurnPlan.TryGetBurn(foreign), "Stop deletes unrelated maneuvers");
        Check(fc.BurnMode == FlightComputerBurnMode.Manual && fc.RCSMode == FlightComputerRCSMode.Disabled && fc.RateLimit == 0.2f
            && fc.ManualThrustMode == FlightComputerManualThrustMode.Direct, "Stop does not restore flight controls");
        var inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(vehicle);
        Check(!inputs.EngineOn && inputs.EngineThrottle == 0 && inputs.ThrusterCommandFlags == ThrusterMapFlags.None, "Stop leaves thrust or translation commands active");
        fc.RemoveBurn(foreign);
        Console.WriteLine("PASS native rendezvous node order, ownership, cancellation and saved control restoration");
        var error = type.GetMethod("RendezvousPredictionError", BindingFlags.Static | BindingFlags.NonPublic);
        Check(error.Invoke(null, new object[] { new FlightPlan(firstCoast, KeyHash.Zero), parent, firstTarget, Number(firstSolution, "Arrival"), 30.0 }) == null,
            "Safe coast rejected by native validator");
        var impact = FlightPlan.CreateUninitialized(KeyHash.Zero);
        impact.Patches.Add(new PatchedConic(new UniverseTime(0), new UniverseTime(100), PatchTransition.Initial, PatchTransition.Impact, own, KeyHash.Zero));
        Check((string)error.Invoke(null, new object[] { impact, parent, firstTarget, 200.0, 30.0 }) == "rendezvous.impact", "Impact accepted or lacks a specific error");
        RendezvousLifecycleChecks(controller, vehicle, targetVehicle, own, firstCoast, firstSolution, firstDelta);
        RendezvousButtonChecks(controller, targetVehicle);
    }

    private static void RendezvousLifecycleChecks(object controller, Vehicle vehicle, Vehicle target, Orbit own, Orbit coast, object solution, double3 delta)
    {
        var type = controller.GetType(); var fc = vehicle.FlightComputer;
        object Invoke(string name, params object[] arguments) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, arguments);
        double departure = (double)solution.GetType().GetField("Departure").GetValue(solution);
        Set(vehicle, "Body2Cce", doubleQuat.Identity); Set(vehicle, "BodyRates", double3.Zero);
        Set(target, "Body2Cce", doubleQuat.Identity); Set(target, "BodyRates", double3.Zero);
        Set(vehicle, "FlightPlan", new FlightPlan(own, KeyHash.Zero));
        Set(controller, "_activeVehicle", vehicle); Set(controller, "_rendezvousRunning", true);
        Set(controller, "_standOff", 100.0); Set(controller, "_acceleration", 0.01);
        Set(controller, "Distance", 10000.0);
        var node = (Burn)Invoke("CreateRendezvousNode", vehicle, vehicle.FlightPlan, departure, delta);
        node.FlightPlan = new FlightPlan(coast, KeyHash.Zero);
        Set(controller, "_rendezvousDeparture", node); Set(controller, "_rendezvousNode", node);
        Check((bool)Invoke("CreateArrivalNode", vehicle, node.FlightPlan), "Lifecycle has no braking node");
        var arrivalNode = (Burn)type.GetField("_rendezvousArrival", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
        arrivalNode.FlightPlan = new FlightPlan(coast, KeyHash.Zero); fc.BurnPlan.FlightPlansOutOfDate = false;
        fc.Burn = new BurnTarget { ImpulsiveInstant = node.Time, IgnitionTime = new UniverseTime(1000), BurnDuration = 10,
            BurnBody2Cci = doubleQuat.Identity, DeltaVTargetCci = new float3(100, 0, 0) };
        fc.RollMode = FlightComputerRollMode.Decoupled; fc.RateBit = float3.Zero;
        var controlOverride = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.NonPublic | BindingFlags.Static);
        bool previous = (bool)controlOverride.GetValue(null); controlOverride.SetValue(null, true);
        var rcs = New("RcsController");
        try
        {
            Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            Invoke("UpdateRendezvousBurn", vehicle, 500.0);
            Check(Universe.IsAutoWarpActive && fc.BurnMode == FlightComputerBurnMode.Manual,
                "Rendezvous does not coast with engines off before ignition");
            Universe.SetSimulationSpeed(100, false);
            Invoke("UpdateRendezvousBurn", vehicle, 700.0);
            Check(fc.BurnMode == FlightComputerBurnMode.Manual && (bool)type.GetProperty("IsRendezvousCoasting").GetValue(controller),
                "Rendezvous enables thrust or attitude correction during warp");
            Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            Set(vehicle, "Body2Cce", doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2));
            Invoke("UpdateRendezvousBurn", vehicle, 900.0);
            Check((bool)type.GetProperty("IsExecutingManeuver").GetValue(controller)
                && !(bool)type.GetProperty("IsRendezvousCoasting").GetValue(controller), "Post-warp alignment keeps RCS forbidden");
            Call(rcs, "Update", vehicle, true, true, true, true);
            Check(fc.RCSMode == FlightComputerRCSMode.Enabled && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.PositiveDv,
                $"RCS does not resume burn alignment after warp: mode={fc.RCSMode}, track={fc.AttitudeTrackTarget}, frame={fc.AttitudeFrame}, radius={vehicle.GetPositionCci().Length()}, atmosphere={vehicle.Parent.GetAtmosphereRadius()}");
            Set(controller, "_rendezvousSettled", 999.0);
            Invoke("UpdateRendezvousBurn", vehicle, 1000.0);
            Check(fc.BurnMode == FlightComputerBurnMode.Manual, "Misaligned rendezvous ignites");
            Set(vehicle, "Body2Cce", doubleQuat.Identity); Set(vehicle, "BodyRates", new double3(0, 0.01, 0));
            Set(controller, "_rendezvousSettled", 999.0);
            Invoke("UpdateRendezvousBurn", vehicle, 1000.0);
            Check(fc.BurnMode == FlightComputerBurnMode.Manual, "Rendezvous ignites while rotating");
            Set(vehicle, "BodyRates", double3.Zero); Set(controller, "_rendezvousSettled", 998.0);
            Invoke("UpdateRendezvousBurn", vehicle, 999.0);
            Check(fc.BurnMode == FlightComputerBurnMode.Manual, "Rendezvous ignites early");
            Invoke("UpdateRendezvousBurn", vehicle, 1000.0);
            Check(fc.BurnMode == FlightComputerBurnMode.Auto, "Settled rendezvous does not ignite");
            fc.Burn.StartedAt = new UniverseTime(1000); fc.Burn.DeltaVAccumCci = fc.Burn.DeltaVTargetCci;
            Invoke("UpdateRendezvousBurn", vehicle, 1010.0);
            Check(fc.BurnMode == FlightComputerBurnMode.Manual && fc.BurnPlan.BurnCount == 0
                && (bool)type.GetField("_rendezvousArrivalPending", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller),
                "Completed departure does not replace the predicted arrival using the actual coast");
            Set(vehicle, "FlightPlan", new FlightPlan(coast, KeyHash.Zero)); fc.BurnPlan.FlightPlansOutOfDate = false;
            Set(controller, "_lastTime", 1010.0);
            Call(controller, "Update", vehicle, 1011.0, true);
            Check((bool)type.GetProperty("IsActive").GetValue(controller) && fc.BurnPlan.BurnCount == 1
                && fc.BurnMode == FlightComputerBurnMode.Manual, "A stale departure target aborts or ignites the new arrival node");
            var updatedArrival = fc.BurnPlan.FirstBurn;
            updatedArrival.FlightPlan = new FlightPlan(coast, KeyHash.Zero); fc.BurnPlan.FlightPlansOutOfDate = false;
            fc.Burn = new BurnTarget { ImpulsiveInstant = updatedArrival.Time,
                IgnitionTime = updatedArrival.Time - 1, BurnDuration = 2, BurnBody2Cci = doubleQuat.Identity,
                DeltaVTargetCci = new float3(50, 0, 0) };
            Set(controller, "Distance", 10000.0);
            Invoke("UpdateRendezvousBurn", vehicle, 1012.0);
            Check(Universe.IsAutoWarpActive && fc.BurnMode == FlightComputerBurnMode.Manual,
                "Arrival cannot request its own coast warp after departure");
        }
        finally
        {
            Call(controller, "Stop", false); Call(rcs, "Restore");
            controlOverride.SetValue(null, previous); Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
        }
        Console.WriteLine("PASS rendezvous coast/warp, RCS restoration, alignment and ignition guards, actual arrival replacement and stale native target handling");
    }

    private static void RendezvousButtonChecks(object controller, Vehicle target)
    {
        Set(controller, "CanRendezvous", true); Set(controller, "CanMatch", true); Set(controller, "CanDock", false);
        Set(controller, "RendezvousUnavailableReason", null); Set(controller, "Message", null);
        var targets = (System.Collections.IList)controller.GetType().GetField("Targets").GetValue(controller);
        targets.Add(target);
        var other = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(other); Set(other, "Id", "Rocket Bravo"); targets.Add(other);
        DockingPort Port(string name)
        {
            var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part)); Set(part, "DisplayName", name);
            var port = (DockingPort)RuntimeHelpers.GetUninitializedObject(typeof(DockingPort)); GC.SuppressFinalize(port);
            Set(port, "Parent", part); return port;
        }
        var ownPorts = (System.Collections.IList)controller.GetType().GetField("OwnPorts").GetValue(controller);
        var targetPorts = (System.Collections.IList)controller.GetType().GetField("TargetPorts").GetValue(controller);
        DockingPort ownA = Port("Nose port"), ownB = Port("Side port"), targetA = Port("Station front port"), targetB = Port("Station rear port");
        ownPorts.Add(ownA); ownPorts.Add(ownB); targetPorts.Add(targetA); targetPorts.Add(targetB);
        Set(Window, "Rendezvous", controller); Set(Window, "_homeOpen", false); Set(Window, "_settingsOpen", false); Set(Window, "_tab", 3);
        foreach (bool small in new[] { false, true })
        foreach (string locale in new[] { "en-US", "de-DE" })
        {
            Set(Window, "_tab", 3);
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            for (int frame = 0; frame < 45; frame++) Frame(true, small);
            Check(DrawLists("kitn-card-rendezvous-docking").Length == 0 && DrawLists("kitn-card-docking-controls").Length == 0,
                "Docking port controls remain in the Rendezvous tab");
            Save($"rendezvous-{locale}-{(small ? "small" : "normal")}.png", small ? 580 : 800, small ? 420 : 670, 12, 12);
            // The primary Rendezvous action belongs to the fixed top control card.
            string action = null;
            for (int y = 115; y <= 160 && action == null; y += 4) action = Click(95, y, "autopilot");
            Check(action == "rendezvous_start", "Rendezvous button does not reach the action queue in " + locale + " / small=" + small + " action=" + action);
            Set(controller, "CanRendezvous", false);
            Set(controller, "RendezvousUnavailableReason", "rendezvous.require_rcs");
            string disabledAction = null;
            for (int y = 115; y <= 150 && disabledAction == null; y += 4) disabledAction = Click(95, y, "autopilot");
            Check(disabledAction == null, "Unavailable rendezvous button starts a maneuver");
            Set(controller, "CanRendezvous", true);
            Set(controller, "RendezvousUnavailableReason", null);
            Click(small ? 450 : 650, 242, "autopilot");
            var io = ImGui.GetIO();
            io.AddKeyEvent(ImGuiKey.DownArrow, true); Frame(); io.AddKeyEvent(ImGuiKey.DownArrow, false); Frame();
            io.AddKeyEvent(ImGuiKey.Enter, true); Frame(); io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
            Check(ReferenceEquals(controller.GetType().GetProperty("Target").GetValue(controller), other),
                "The rendezvous selector does not select the other spacecraft without a docking port");
            Call(controller, "SelectTarget", target);

            // Select Docking through its real topbar icon while labels are collapsed.
            Set(Window, "_settingsOpen", true);
            for (int frame = 0; frame < 75; frame++) Frame();
            object bounds = Call(Store, "Find", "autopilot");
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            Click(Value(bounds, "X") + (16 + 60 + 5 * 32 + 15) * scale, Value(bounds, "Y") + 31 * scale, "autopilot");
            Check((int)Window.GetType().GetField("_tab", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window) == 6,
                "Docking topbar icon does not open the separate page");
            for (int frame = 0; frame < 45; frame++) Frame();
            Check(DrawLists("kitn-card-docking-controls").Sum(list => list.VtxBuffer.Span.Length) > 200
                && DrawLists("kitn-card-rendezvous-controls").Length == 0, "Separate docking page does not expose its target and port selectors");
            Set(controller, "OwnPort", ownA); Set(controller, "TargetPort", targetA); Frame();
            void SelectNext(int y)
            {
                Click(small ? 450 : 650, y, "autopilot");
                io.AddKeyEvent(ImGuiKey.DownArrow, true); Frame(); io.AddKeyEvent(ImGuiKey.DownArrow, false); Frame();
                io.AddKeyEvent(ImGuiKey.Enter, true); Frame(); io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
            }
            SelectNext(306);
            Check(ReferenceEquals(controller.GetType().GetProperty("OwnPort").GetValue(controller), ownB), "Docking page cannot select the own port");
            SelectNext(338);
            Check(ReferenceEquals(controller.GetType().GetProperty("TargetPort").GetValue(controller), targetB), "Docking page cannot select the target port");
            Set(controller, "CanDock", true);
            string dock = null;
            for (int y = 115; y <= 160 && dock == null; y += 4) dock = Click(95, y, "autopilot");
            Check(dock == "rendezvous_dock", "Docking primary button does not reach the action queue in " + locale);
            io.AddMousePosEvent(-1000, -1000); Frame(); Frame();
            Save($"docking-{locale}-{(small ? "small" : "normal")}.png", small ? 580 : 800, small ? 420 : 670, 12, 12);
            Set(controller, "CanDock", false);
            string disabledDock = null;
            for (int y = 115; y <= 160 && disabledDock == null; y += 4) disabledDock = Click(95, y, "autopilot");
            Check(disabledDock == null, "Unavailable docking button starts docking");
        }
        Set(Window, "_tab", 3);
        Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { "en-US" });
        var fail = controller.GetType().GetMethod("FailRendezvous", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(object[]) }, null);
        fail.Invoke(controller, new object[] { "rendezvous.insufficient_dv", new object[] { 1200.0, 900.0, 300.0 } });
        string detail = (string)controller.GetType().GetProperty("MessageText").GetValue(controller);
        Check(detail.Contains("1,200") && detail.Contains("900") && detail.Contains("300"), "Missing delta-v error loses the required and available budget");
        Frame(true, true); Frame();
        uint red = ImGui.ColorConvertFloat4ToU32(ConsoleStyle.Danger);
        Check(DrawLists("kitn-card-rendezvous-state").SelectMany(list => list.VtxBuffer.Span.ToArray()).Count(vertex => vertex.col == red) > 20,
            "Specific rendezvous error is not displayed in red");
        Save("rendezvous-missing-delta-v.png", 580, 420, 12, 12);
        Set(controller, "Message", null);
        Frame(true);
        Set(controller, "_activeVehicle", target); Set(Window, "_settingsOpen", true);
        for (int frame = 0; frame < 5; frame++) Frame(true);
        string stop = null;
        for (int y = 80; y < 180 && stop == null; y += 8) stop = Click(95, y, "autopilot");
        Check(stop == "rendezvous_stop", "Global rendezvous stop is hidden in Settings");
        Set(controller, "_activeVehicle", null); Set(Window, "Rendezvous", null); Set(Window, "_settingsOpen", false);
        Console.WriteLine("PASS separate Rendezvous/Docking topbar pages, primary start/disabled actions and global stop in English/German at both widths");
    }

    private static void RendezvousPropulsionChecks(IParentBody parent)
    {
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(vehicle);
        Set(vehicle, "Id", "dock_1_2");
        var tree = (PartTree)RuntimeHelpers.GetUninitializedObject(typeof(PartTree)); GC.SuppressFinalize(tree);
        tree.Modules = new ModuleList(); vehicle.Parts = tree;
        var massField = typeof(Vehicle).GetField("_props", BindingFlags.Instance | BindingFlags.NonPublic);
        object props = Activator.CreateInstance(massField.FieldType);
        var offsetField = props.GetType().GetField("TotalMassPropsAsmb"); object offset = Activator.CreateInstance(offsetField.FieldType);
        var bodyField = offset.GetType().GetField("Props"); object bodyProps = Activator.CreateInstance(bodyField.FieldType);
        Set(bodyProps, "Mass", 42263.765625f); bodyField.SetValue(offset, bodyProps); offsetField.SetValue(props, offset); massField.SetValue(vehicle, props);
        var fc = new FlightComputer { BurnMode = FlightComputerBurnMode.Manual, RCSMode = FlightComputerRCSMode.Enabled };
        fc.ActiveEnginePerformanceMax = new ActiveEnginePerformance { Thrust = 955522.3125f, MassFlowRate = 222.89703369140625f };
        Set(vehicle, "FlightComputer", fc);
        var orbit = Orbit.CreateFromStateCci(parent, new UniverseTime(0), new double3(7e6, 0, 0), new double3(0, Math.Sqrt(parent.Mu / 7e6), 0), byte4.Zero);
        Set(vehicle, "FlightPlan", new FlightPlan(orbit, KeyHash.Zero));
        var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part)); part.Modules = new ModuleList(); Set(part, "Tree", tree);
        var fuel = new Liquid { Id = "rendezvous-test-fuel", Hash = new KeyHash(1u), Name = "Test fuel", Substance = null, StorageDensity = 1, StorageTemperature = 300 };
        var oxidizer = new Liquid { Id = "rendezvous-test-oxidizer", Hash = new KeyHash(2u), Name = "Test oxidizer", Substance = null, StorageDensity = 1, StorageTemperature = 300 };
        var remote = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part)); remote.Modules = new ModuleList(); Set(remote, "Tree", tree);
        var mole = new Mole(fuel, 1, 1) { Parent = remote }; Set(mole, "StatesIdx", 0);
        var oxidizerMole = new Mole(oxidizer, 1, 1) { Parent = remote }; Set(oxidizerMole, "StatesIdx", 1);
        var states = new System.Collections.Generic.List<MoleState> { new MoleState { Mass = 11200 }, new MoleState { Mass = 26000 } };
        var molesField = typeof(PartTree).GetField("Moles"); object moles = RuntimeHelpers.GetUninitializedObject(molesField.FieldType);
        Set(moles, "_states", states); molesField.SetValue(tree, moles);
        var tank = (Tank)RuntimeHelpers.GetUninitializedObject(typeof(Tank)); GC.SuppressFinalize(tank);
        tank.Parent = remote; tank.Moles = new System.Collections.Generic.List<Mole> { mole }; Set(tank, "PropellantUseEnabled", true);
        var oxidizerTank = (Tank)RuntimeHelpers.GetUninitializedObject(typeof(Tank)); GC.SuppressFinalize(oxidizerTank);
        oxidizerTank.Parent = remote; oxidizerTank.Moles = new System.Collections.Generic.List<Mole> { oxidizerMole }; Set(oxidizerTank, "PropellantUseEnabled", true);
        var engine = (EngineController)RuntimeHelpers.GetUninitializedObject(typeof(EngineController)); GC.SuppressFinalize(engine);
        engine.Parent = part; Set(engine, "IsActive", true);
        var core = (ThermalCore)RuntimeHelpers.GetUninitializedObject(typeof(ThermalCore)); GC.SuppressFinalize(core);
        core.Mix = new ReactantMix(new[] { new Reactant { SubstancePhase = fuel, MassFraction = 0.3f }, new Reactant { SubstancePhase = oxidizer, MassFraction = 0.7f } }, "Rendezvous test mix");
        Set(core, "Parent", part);
        // Actual KSA craft may have no directly bound tank: connector flow reaches the remote tank.
        core.FeedTanks = Array.Empty<Tank>();
        var manager = (ResourceManager)RuntimeHelpers.GetUninitializedObject(typeof(ResourceManager)); GC.SuppressFinalize(manager);
        manager.Entries = new[] { tank, oxidizerTank }; manager.LevelStarts = new[] { 0, 2 }; manager.SameStageCounts = new[] { 2 };
        typeof(ResourceManager).GetField("<Consumer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, core);
        manager.FlowRule = FlowRule.NearestToFurtherest; core.ResourceManager = manager;
        engine.Cores = new RocketCore[] { core }; engine.MinimumThrottle = core.MinimumThrottle = 0.1f; tree.Modules.Add(engine);
        Set(tree, "_engineThrottleMin", 0.1f);
        var dirty = typeof(PartTree).GetField("_derivedDirty", BindingFlags.Instance | BindingFlags.NonPublic);
        dirty.SetValue(tree, Activator.CreateInstance(dirty.FieldType));
        var read = Mod.GetType("KITN.RendezvousController").GetMethod("ReadRendezvousPhases", BindingFlags.Static | BindingFlags.NonPublic);
        (FiniteBurnPhase[] phases, string problem) Read()
        {
            object[] arguments = { vehicle, null }; var phases = (FiniteBurnPhase[])read.Invoke(null, arguments);
            return (phases, (string)arguments[1]);
        }
        Check(fc.StagedBurn.Length == 0, "Fixture does not reproduce the empty native burn cache");
        var result = Read();
        Check(core.ComputePropellantAvailable(tree.Moles.States, true), "KSA itself cannot access the fixture's connected remote tanks");
        Check(result.problem == null && result.phases.Length == 1, "Active fueled engine with empty native cache is rejected: " + result.problem);
        FiniteBurnPredictor.Duration(result.phases, 1e6, out double available);
        double usableFuel = Math.Min(11200 / (double)core.Mix.Reactants[0].MassFraction, 26000 / (double)core.Mix.Reactants[1].MassFraction);
        double expected = fc.ActiveEnginePerformanceMax.Thrust / (double)fc.ActiveEnginePerformanceMax.MassFlowRate
            * Math.Log(vehicle.TotalMass / (vehicle.TotalMass - usableFuel));
        Check(double.IsFinite(available) && Math.Abs(available - expected) < 0.01, "Connected fuel/oxidizer mixture produces the wrong rendezvous delta-v budget");
        object propulsion = Mod.GetType("KITN.LandingPropulsion").GetMethod("Read").Invoke(null, new object[] { vehicle });
        Check((bool)propulsion.GetType().GetField("FuelValid").GetValue(propulsion)
            && Math.Abs((double)propulsion.GetType().GetMethod("AvailableDeltaV").Invoke(propulsion, new object[] { (double)vehicle.TotalMass }) - expected) < 0.01,
            "Landing loses the same connected mixture-limited fuel budget");
        Set(engine, "IsActive", false); Check(Read().problem == "rendezvous.engines_inactive", "Inactive engines report generic missing fuel");
        Set(engine, "IsActive", true); states[0] = new MoleState { Mass = 0 };
        Check(Read().problem == "rendezvous.no_fuel", "Empty fuel feed does not report exact propellant problem");
        states[0] = new MoleState { Mass = 11200 }; states[1] = new MoleState { Mass = 0 };
        Check(!core.ComputePropellantAvailable(tree.Moles.States, true) && Read().problem == "rendezvous.no_fuel", "Missing oxidizer is counted as usable fuel");
        states[1] = new MoleState { Mass = 26000 }; Set(tank, "PropellantUseEnabled", false);
        Check(Read().problem == "rendezvous.no_fuel", "Disabled tank is counted as usable fuel");
        Set(tank, "PropellantUseEnabled", true);
        Set(oxidizerTank, "PropellantUseEnabled", false);
        Check(Read().problem == "rendezvous.no_fuel", "Disabled oxidizer tank is counted as usable fuel");
        Set(oxidizerTank, "PropellantUseEnabled", true);
        manager.SameStageCounts = new[] { 1 }; manager.FlowRule = FlowRule.NearestToFurtherestSameStage;
        Check(!core.ComputePropellantAvailable(tree.Moles.States, true) && Read().problem == "rendezvous.no_fuel", "Same-stage flow permits the other-stage oxidizer tank");
        manager.FlowRule = FlowRule.FurtherestToNearest;
        Check(core.ComputePropellantAvailable(tree.Moles.States, true) && Read().problem == null, "Permitted cross-stage flow is rejected");
        var controller = New("RendezvousController"); var target = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(target);
        Set(target, "Id", "dock");
        var targetOrbit = Orbit.CreateFromStateCci(parent, new UniverseTime(0), new double3(7e6 * Math.Cos(0.5), 7e6 * Math.Sin(0.5), 0),
            new double3(-Math.Sqrt(parent.Mu / 7e6) * Math.Sin(0.5), Math.Sqrt(parent.Mu / 7e6) * Math.Cos(0.5), 0), byte4.Zero);
        Set(target, "FlightPlan", new FlightPlan(targetOrbit, KeyHash.Zero));
        Set(controller, "Target", target); Set(controller, "_activeVehicle", vehicle); Set(controller, "_rendezvousRunning", true);
        controller.GetType().GetMethod("QueueRendezvousSearch", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { vehicle, 0.0 });
        Check((bool)controller.GetType().GetProperty("IsActive").GetValue(controller) && fc.StagedBurn.Length == 1
            && controller.GetType().GetField("_rendezvousSearch", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller) != null,
            "Rendezvous immediately stops when the native burn cache was initially empty");
        Check(fc.BurnMode == FlightComputerBurnMode.Manual && fc.BurnPlan.BurnCount == 0, "Preparing a fueled rendezvous ignites engines before planning");
        object worker = controller.GetType().GetField("_rendezvousSearch", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
        object awaiter = worker.GetType().GetMethod("GetAwaiter").Invoke(worker, null);
        object solution = awaiter.GetType().GetMethod("GetResult").Invoke(awaiter, null);
        Check((bool)solution.GetType().GetField("Valid").GetValue(solution), "Empty-cache rocket starts but cannot calculate a finite rendezvous route");
        RendezvousContinuationChecks(controller, vehicle, target, solution);
        Call(controller, "Stop", false);
        Console.WriteLine("PASS native connected fuel/oxidizer tanks with empty direct bindings and burn cache: mixture delta-v, landing fuel, missing/disabled reactants and stage flow rules; full rendezvous search");
    }

    private static void RendezvousContinuationChecks(object controller, Vehicle vehicle, Vehicle target, object original)
    {
        var type = controller.GetType(); var fc = vehicle.FlightComputer; var parent = vehicle.Parent;
        double Number(object value, string name) => (double)value.GetType().GetField(name).GetValue(value);
        double3 Native(object vector) => new double3(Number(vector, "X"), Number(vector, "Y"), Number(vector, "Z"));
        var clockField = typeof(Universe).GetField("_lastSimStep", BindingFlags.Static | BindingFlags.NonPublic);
        object previousClock = clockField.GetValue(null);
        var controlOverride = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.Static | BindingFlags.NonPublic);
        bool previousOverride = (bool)controlOverride.GetValue(null);
        void At(double now)
        {
            object step = Activator.CreateInstance(clockField.FieldType); Set(step, "NextTime", new UniverseTime(now)); clockField.SetValue(null, step);
            vehicle.Orbit.UpdatePosition(vehicle.Orbit.GetStateVectorsAt(new UniverseTime(now)));
            target.Orbit.UpdatePosition(target.Orbit.GetStateVectorsAt(new UniverseTime(now)));
        }
        void InBubble(Vehicle craft)
        {
            var state = (VehicleUpdateState)RuntimeHelpers.GetUninitializedObject(typeof(VehicleUpdateState)); GC.SuppressFinalize(state);
            var bubble = (PhysicsBubble)RuntimeHelpers.GetUninitializedObject(typeof(PhysicsBubble)); GC.SuppressFinalize(bubble);
            Set(state, "Bubble", bubble); Set(craft, "_threadWorkerUpdateState", state);
            Set(craft, "Body2Cce", doubleQuat.Identity); Set(craft, "BodyRates", double3.Zero);
        }
        void Consume(double amount)
        {
            // Update the independent craft mass and both reactants after each simulated native burn.
            var massField = typeof(Vehicle).GetField("_props", BindingFlags.Instance | BindingFlags.NonPublic);
            object props = massField.GetValue(vehicle); var offsetField = props.GetType().GetField("TotalMassPropsAsmb");
            object offset = offsetField.GetValue(props); var bodyField = offset.GetType().GetField("Props"); object bodyProps = bodyField.GetValue(offset);
            Set(bodyProps, "Mass", (float)(vehicle.TotalMass - amount)); bodyField.SetValue(offset, bodyProps); offsetField.SetValue(props, offset); massField.SetValue(vehicle, props);
            var moles = typeof(PartTree).GetField("Moles").GetValue(vehicle.Parts);
            var states = (System.Collections.Generic.List<MoleState>)moles.GetType().GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(moles);
            states[0] = new MoleState { Mass = (float)(states[0].Mass - amount * 0.3) };
            states[1] = new MoleState { Mass = (float)(states[1].Mass - amount * 0.7) };
        }
        Orbit Coast(double time, double3 r, double3 v) => Orbit.CreateFromStateCci(parent, new UniverseTime(time), r, v, byte4.Zero);
        var scale = type.GetMethod("ThrottlePhases", BindingFlags.Static | BindingFlags.NonPublic);
        FiniteBurnPhase[] Phases(float throttle) => (FiniteBurnPhase[])scale.Invoke(null, new object[] { fc.StagedBurn.ToArray(), (double)throttle });
        try
        {
            controlOverride.SetValue(null, true); InBubble(vehicle); InBubble(target);
            vehicle.Parts.States = new ModuleStateList();
            var thrusters = vehicle.Parts.States.InitializeHotPathList<ThrusterController, ThrusterControllerState, ThrusterControllerGlobalState, EmptyStruct>();
            ref var global = ref thrusters.GetMutableGlobalStateForInitialization();
            global.Authority = new ThrusterAuthority { TotalTorque = new float3(100), TotalForce = new float3(vehicle.TotalMass * 0.1f),
                MinRotationalImpulse = new float3(0.01f), MinLinearImpulse = new float3(vehicle.TotalMass * 0.004f) };
            Set(controller, "_matching", true); Set(controller, "_rendezvousParent", parent); Set(controller, "_standOff", 100.0);
            Set(controller, "_acceleration", 0.01); Set(controller, "_minimumDeltaV", new float3(0.004f));
            Set(controller, "_savedComputer", fc); Set(controller, "_savedRcs", FlightComputerRCSMode.Disabled);
            Set(controller, "_savedRate", 0.2f); Set(controller, "_savedDeadband", 0.02f); Set(controller, "_savedRoll", FlightComputerRollMode.Decoupled);
            Set(controller, "_savedThrust", FlightComputerManualThrustMode.Direct);
            object request = type.GetField("_rendezvousRequest", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            var predict = (Delegate)request.GetType().GetField("PredictBurn").GetValue(request);
            object firstBurn = predict.DynamicInvoke(Number(original, "Departure"), original.GetType().GetField("DepartureDelta").GetValue(original), CancellationToken.None);
            double firstCutoff = Number(firstBurn, "Cutoff");
            var burnR = Native(firstBurn.GetType().GetField("Position").GetValue(firstBurn));
            var burnV = Native(firstBurn.GetType().GetField("Velocity").GetValue(firstBurn));
            // A small real cutoff error grows into a multi-kilometer miss during the long coast.
            var actual = Coast(firstCutoff, burnR, burnV + burnV.NormalizeOrZero() * 0.15);
            double arrival = Number(original, "Arrival");
            double missed = (actual.GetStateVectorsAt(new UniverseTime(arrival)).PositionCci - target.Orbit.GetStateVectorsAt(new UniverseTime(arrival)).PositionCci).Length();
            Check(missed > 2000, "Continuation fixture does not reproduce a post-departure arrival miss");
            Set(vehicle, "FlightPlan", new FlightPlan(actual, KeyHash.Zero)); Set(controller, "_rendezvousPlan", original);
            Set(controller, "_rendezvousSearch", null);
            var cancellation = (CancellationTokenSource)type.GetField("_rendezvousCancellation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            cancellation.Dispose(); Set(controller, "_rendezvousCancellation", null);
            Set(controller, "_rendezvousArrivalPending", true); Set(controller, "_lastTime", firstCutoff);
            fc.BurnPlan.FlightPlansOutOfDate = false;
            At(firstCutoff + 1); Call(controller, "Update", vehicle, firstCutoff + 1, true);
            Check((bool)type.GetProperty("IsActive").GetValue(controller)
                && (int)type.GetField("_rendezvousCourseCorrections", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller) == 1,
                "A correctable post-departure arrival miss stops rendezvous instead of correcting the course");
            object worker = type.GetField("_rendezvousSearch", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            object awaiter = worker.GetType().GetMethod("GetAwaiter").Invoke(worker, null);
            object corrected = awaiter.GetType().GetMethod("GetResult").Invoke(awaiter, null);
            Check((bool)corrected.GetType().GetField("Valid").GetValue(corrected) && Math.Abs(Number(corrected, "Arrival") - arrival) < 0.01,
                "Course correction abandons the existing encounter time");
            At(firstCutoff + 1.1); Call(controller, "Update", vehicle, firstCutoff + 1.1, true);
            var correctionNode = (Burn)type.GetField("_rendezvousNode", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            var correctionDelta = Native(corrected.GetType().GetField("DepartureDelta").GetValue(corrected));
            Check(correctionNode.PlannedThrottle < 1 && correctionNode.PlannedThrottle >= 0.1,
                "Short correction uses full thrust or ignores engine minimum throttle");
            double correctionTime = Number(corrected, "Departure");
            var correctionPhases = Phases(correctionNode.PlannedThrottle.Value);
            var correctionArc = FiniteBurnPredictor.Predict(actual, new UniverseTime(correctionTime),
                OrbitalTransfers.CciToVlf(actual, new UniverseTime(correctionTime), correctionDelta), correctionPhases);
            var correctedCoast = Coast(correctionArc.Cutoff.Seconds(), correctionArc.PositionCci, correctionArc.VelocityCci);
            correctionNode.FlightPlan = new FlightPlan(correctedCoast, KeyHash.Zero); fc.BurnPlan.FlightPlansOutOfDate = false;
            At(firstCutoff + 1.2); Call(controller, "Update", vehicle, firstCutoff + 1.2, true);
            Check(type.GetField("_rendezvousArrival", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller) != null,
                "Correction does not reserve a braking node before ignition");
            void Execute(Burn node, FiniteBurnResult arc, double3 delta, Orbit coast)
            {
                fc.BurnPlan.FlightPlansOutOfDate = false;
                var brakingNode = (Burn)type.GetField("_rendezvousArrival", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                if (brakingNode != null) brakingNode.FlightPlan = new FlightPlan(coast, KeyHash.Zero);
                var burn = new BurnTarget { ImpulsiveInstant = node.Time, IgnitionTime = arc.Ignition, BurnDuration = (float)(arc.Cutoff.Seconds() - arc.Ignition.Seconds()),
                    BurnBody2Cci = doubleQuat.Identity, DeltaVTargetCci = float3.Pack(delta) };
                fc.Burn = burn; fc.RateBit = float3.Zero;
                At(arc.Ignition.Seconds() - 2); Call(controller, "Update", vehicle, arc.Ignition.Seconds() - 2, true);
                Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
                At(arc.Ignition.Seconds()); Call(controller, "Update", vehicle, arc.Ignition.Seconds(), true);
                Check(fc.BurnMode == FlightComputerBurnMode.Auto, "Continuation cannot automatically ignite its next maneuver");
                burn.StartedAt = arc.Ignition; burn.DeltaVAccumCci = burn.DeltaVTargetCci;
                Set(vehicle, "FlightPlan", new FlightPlan(coast, KeyHash.Zero));
                Consume((arc.Cutoff.Seconds() - arc.Ignition.Seconds()) * Phases(node.PlannedThrottle.Value)[0].MassFlowRate);
                At(arc.Cutoff.Seconds() + 0.01); Call(controller, "Update", vehicle, arc.Cutoff.Seconds() + 0.01, true);
            }
            Execute(correctionNode, correctionArc, correctionDelta, correctedCoast);
            fc.BurnPlan.FlightPlansOutOfDate = false;
            double correctedCutoff = correctionArc.Cutoff.Seconds();
            At(correctedCutoff + 0.02); Call(controller, "Update", vehicle, correctedCutoff + 0.02, true);
            var arrivalNode = (Burn)type.GetField("_rendezvousNode", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            Check(arrivalNode != null && (bool)type.GetProperty("IsActive").GetValue(controller), "Correction does not continue to braking from the actual coast");
            var ownAtArrival = correctedCoast.GetStateVectorsAt(new UniverseTime(arrival)); var targetAtArrival = target.Orbit.GetStateVectorsAt(new UniverseTime(arrival));
            var match = targetAtArrival.VelocityCci - ownAtArrival.VelocityCci;
            var brakingArc = FiniteBurnPredictor.Predict(correctedCoast, new UniverseTime(arrival),
                OrbitalTransfers.CciToVlf(correctedCoast, new UniverseTime(arrival), match), Phases(arrivalNode.PlannedThrottle.Value));
            var finalCoast = Coast(brakingArc.Cutoff.Seconds(), brakingArc.PositionCci, brakingArc.VelocityCci);
            arrivalNode.FlightPlan = new FlightPlan(finalCoast, KeyHash.Zero);
            fc.CustomAttitudeTarget = new double3(1.58, 0, 0.155);
            Execute(arrivalNode, brakingArc, match, finalCoast);
            Check((bool)type.GetField("_rendezvousLocal", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller)
                && (bool)type.GetProperty("IsActive").GetValue(controller), "Automatic braking does not enter local RCS approach");
            Check(fc.CustomAttitudeTarget.Length() < 1e-10 && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None,
                "Braking-to-approach transition retains the logged stale spin command");
            Set(fc, "Ctrl2Asmb", doubleQuat.Identity); fc.SetManualThrustMode(FlightComputerManualThrustMode.Pulse);
            double now = brakingArc.Cutoff.Seconds() + 0.02;
            var ownState = finalCoast.GetStateVectorsAt(new UniverseTime(now)); var targetState = target.Orbit.GetStateVectorsAt(new UniverseTime(now));
            var ownR = ownState.PositionCci; var ownV = ownState.VelocityCci; var targetR = targetState.PositionCci; var targetV = targetState.VelocityCci;
            double3 Gravity(double3 r) => r * (-parent.Mu / Math.Pow(r.Length(), 3));
            for (int step = 0; step < 60000 && (bool)type.GetProperty("IsActive").GetValue(controller); step++, now += 0.1)
            {
                At(now); Set(vehicle, "FlightPlan", new FlightPlan(Coast(now, ownR, ownV), KeyHash.Zero));
                Set(target, "FlightPlan", new FlightPlan(Coast(now, targetR, targetV), KeyHash.Zero));
                Call(controller, "Update", vehicle, now, true);
                if ((bool)type.GetProperty("IsActive").GetValue(controller))
                    Check(fc.CustomAttitudeTarget.Length() < 1e-10 && fc.AttitudeFrame == VehicleReferenceFrame.EclBody
                        && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None, "Local translation changes its inertial zero rotation target");
                var inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
                Check(!inputs.EngineOn && inputs.EngineThrottle == 0 && (ownR - targetR).Length() > 30, "RCS approach commands main thrust or violates clearance");
                if (step % 2 == 0)
                {
                    double Sign(ThrusterMapFlags positive, ThrusterMapFlags negative) => ((inputs.ThrusterCommandFlags & positive) != 0 ? 1 : 0) - ((inputs.ThrusterCommandFlags & negative) != 0 ? 1 : 0);
                    ownV += new double3(Sign(ThrusterMapFlags.TranslateForward, ThrusterMapFlags.TranslateBackward),
                        Sign(ThrusterMapFlags.TranslateRight, ThrusterMapFlags.TranslateLeft), Sign(ThrusterMapFlags.TranslateDown, ThrusterMapFlags.TranslateUp)) * 0.004;
                }
                var ga = Gravity(ownR); var gb = Gravity(targetR);
                ownR += ownV * 0.1 + ga * 0.005; targetR += targetV * 0.1 + gb * 0.005;
                ownV += (ga + Gravity(ownR)) * 0.05; targetV += (gb + Gravity(targetR)) * 0.05;
            }
            Check(type.GetProperty("Phase").GetValue(controller).ToString() == "Rendezvoused"
                && (ownR - targetR).Length() < 106 && (ownV - targetV).Length() <= 0.02,
                $"Continuation never finishes RCS approach/velocity matching: phase={type.GetProperty("Phase").GetValue(controller)} distance={(ownR - targetR).Length():F2} speed={(ownV - targetV).Length():F3}");
            Check(fc.BurnPlan.BurnCount == 0 && fc.BurnMode == FlightComputerBurnMode.Manual && fc.RCSMode == FlightComputerRCSMode.Disabled,
                "Completed rendezvous leaves maneuvers or automatic controls active");
            Console.WriteLine($"PASS full native continuation: {missed:F0} m actual miss -> course correction -> automatic braking -> RCS stand-off {(ownR - targetR).Length():F2} m at {(ownV - targetV).Length():F3} m/s");
        }
        finally
        {
            Call(controller, "Stop", false); Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            clockField.SetValue(null, previousClock); controlOverride.SetValue(null, previousOverride);
        }
    }
}
