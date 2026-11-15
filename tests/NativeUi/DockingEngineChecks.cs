using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private sealed class EngineAssistTestCore : PlumbedCore
    {
        public static float LastThrottle;
        public EngineAssistTestCore() : base("test-core") { }
        public override float MaxChamberPressure => 1;
        public override RocketCoreConditions ComputeConditions(float throttle)
        { LastThrottle = throttle; return RocketCoreConditions.Zero; }
        public override void RecomputeDesignConditions() { }
    }
    private sealed class EngineAssistTestNozzle : RocketNozzle
    {
        public EngineAssistTestNozzle() : base("test-nozzle") { }
        public override float FxExitRadius => 0.1f;
        public override float ThroatArea => 0.01f;
        public override void SetScale(in ScaleFactors scale) { }
        public override NozzlePerformance ComputePerformance(ref readonly NozzleConditions conditions, float ambientPressure)
        {
            var result = NozzlePerformance.Zero;
            result.MassFlowRate = EngineAssistTestCore.LastThrottle; result.ActualExhaustVelocity = 4000;
            return result;
        }
        public override NozzleConditions ComputeConditions(GasProperties gas, GasConditions upstream) => default;
        public override float ComputeDesignMach(GasProperties gas) => 1;
    }

    private static void DockingEngineChecks()
    {
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(vehicle);
        var tree = (PartTree)RuntimeHelpers.GetUninitializedObject(typeof(PartTree)); GC.SuppressFinalize(tree);
        tree.Modules = new ModuleList(); tree.States = new ModuleStateList(); vehicle.Parts = tree;
        var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part)); part.Modules = new ModuleList(); Set(part, "Tree", tree);
        typeof(Part).GetField("<Asmb2ParentAsmb>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(part, doubleQuat.Identity);
        typeof(Part).GetField("<Scale>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(part, new double3(1, 1, 1));
        Set(part, "_matrixAsmb2Parent", double4x4.Identity); Set(part, "_matrixAsmb2VehicleAsmb", double4x4.Identity);
        Set(part, "_asmb2VehicleAsmb", doubleQuat.Identity);
        var propsField = typeof(Vehicle).GetField("_props", BindingFlags.Instance | BindingFlags.NonPublic);
        object props = Activator.CreateInstance(propsField.FieldType), offset = Activator.CreateInstance(propsField.FieldType.GetField("TotalMassPropsAsmb").FieldType);
        object massProps = Activator.CreateInstance(offset.GetType().GetField("Props").FieldType);
        Set(massProps, "Mass", 1000f); Set(offset, "Props", massProps); Set(props, "TotalMassPropsAsmb", offset); propsField.SetValue(vehicle, props);
        var fc = new FlightComputer { Helm2Asmb = doubleQuat.Identity, Tnm2Asmb = doubleQuat.Identity, BurnMode = FlightComputerBurnMode.Manual };
        Set(vehicle, "FlightComputer", fc); Set(vehicle, "Body2Cce", doubleQuat.Identity);
        var parent = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        Set(vehicle, "FlightPlan", new FlightPlan(Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(),
            new double3(7e6, 0, 0), new double3(0, Math.Sqrt(parent.Mu / 7e6), 0), byte4.Zero), KeyHash.Zero));
        var port = DockingTestPort(); port.Connector.Asmb2ParentAsmb = doubleQuat.Identity;
        var core = (EngineAssistTestCore)RuntimeHelpers.GetUninitializedObject(typeof(EngineAssistTestCore)); GC.SuppressFinalize(core);
        core.MinimumThrottle = 0.1f; core.MinimumPulseTime = 0.05f; Set(core, "Parent", part);
        var nozzle = (EngineAssistTestNozzle)RuntimeHelpers.GetUninitializedObject(typeof(EngineAssistTestNozzle)); GC.SuppressFinalize(nozzle);
        nozzle.LocationAsmb = new float3(-3, 0, 0); nozzle.ExhaustDirectionAsmb = -float3.UnitX; Set(nozzle, "Parent", part);
        core.Rocket = (Rocket)RuntimeHelpers.GetUninitializedObject(typeof(Rocket)); GC.SuppressFinalize(core.Rocket);
        core.Rocket.Nozzles = new RocketNozzle[] { nozzle };
        var engine = new EngineController("test-engine") { Parent = part, Cores = new RocketCore[] { core }, MinimumThrottle = 0.1f };
        Set(engine, "IsActive", true); tree.Modules.Add(engine);
        var coreStates = tree.States.InitializeHotPathList<RocketCore, RocketCoreState, RocketCoreGlobalState, EmptyStruct>();
        coreStates.AddNew(core); coreStates.GetModuleAndAllMutableStatesForInitialization(core).State.IsPropellantAvailable = true;
        var reader = Mod.GetType("KITN.DockingEngineReader").GetMethod("Read");
        (bool valid, object data, object reason) Read()
        { object[] args = { vehicle, port, null, null }; bool result = (bool)reader.Invoke(null, args); return (result, args[2], args[3]); }
        var result = Read();
        Check(result.valid, $"Native rear-engine reader rejects a fueled, balanced aft nozzle: {result.reason}");
        var acceleration = result.data.GetType().GetField("Acceleration").GetValue(result.data);
        double ax = (double)acceleration.GetType().GetField("X").GetValue(acceleration);
        Check(Math.Abs(ax + 0.4) < 1e-5, "Native nozzle thrust/minimum throttle/mass do not yield the predicted push");
        nozzle.LocationAsmb = new float3(3, 0, 0); Check(!Read().valid, "Front engine is allowed"); nozzle.LocationAsmb = new float3(-3, 0, 0);
        nozzle.ExhaustDirectionAsmb = float3.UnitY; Check(!Read().valid, "Sideways engine is allowed"); nozzle.ExhaustDirectionAsmb = -float3.UnitX;
        Set(engine, "IsActive", false); Check(!Read().valid, "Inactive engine is automatically selected/activated"); Set(engine, "IsActive", true);
        coreStates.GetModuleAndAllMutableStatesForInitialization(core).State.IsPropellantAvailable = false;
        Check(!Read().valid, "Unavailable native propellant is ignored"); coreStates.GetModuleAndAllMutableStatesForInitialization(core).State.IsPropellantAvailable = true;
        // Exercise KSA's real minimum-duration latch, including cutoff commands.
        var state = RocketCoreState.Zero; state.IsPropellantAvailable = true;
        var command = RocketCoreCommand.Zero; command.Throttle = 0.1f; command.BurnTime = double.PositiveInfinity; command.LastCommandTime = new UniverseTime(1);
        core.UpdateState(0.01, ref state, command, ReadOnlySpan<MoleState>.Empty);
        Check(state.Throttle == 0.1f && state.MinThrustTimeRemaining >= 0.049, "Native minimum engine burn did not latch");
        coreStates.GetModuleAndAllMutableStatesForInitialization(core).State = state;
        Check(!Read().valid, "An already-burning engine is restarted by assist");
        command.Throttle = 0; command.BurnTime = 0; command.LastCommandTime = new UniverseTime(1.01);
        core.UpdateState(0.01, ref state, command, ReadOnlySpan<MoleState>.Empty);
        Check(state.Throttle > 0, "Native cutoff incorrectly bypasses hardware minimum burn time");
        command.LastCommandTime = new UniverseTime(1.07); core.UpdateState(0.06, ref state, command, ReadOnlySpan<MoleState>.Empty);
        Check(state.Throttle == 0 && state.ThrustTimeRemaining == 0 && state.MinThrustTimeRemaining == 0,
            "Small engine pulse does not shut down after the minimum duration");
        // Test actual controller decision and manual inputs without changing staging.
        coreStates.GetModuleAndAllMutableStatesForInitialization(core).State = state;
        var controller = New("RendezvousController"); Set(controller, "OwnPort", port);
        var target = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(target); Set(target, "BodyRates", double3.Zero);
        Set(controller, "Target", target); Set(controller, "_relativePosition", new double3(90, 0.01, 0));
        Set(controller, "_relativeVelocity", new double3(-0.1, 0, 0)); Set(controller, "_targetPort2Cci", FlightComputerAttitudeTrackTargetEx.YAW_FLIP);
        Set(controller, "_acceleration", 0.02); Set(controller, "_dockingSafeDistance", 20.0);
        var settings = controller.GetType().GetProperty("DockingOptions").GetValue(controller);
        settings.GetType().GetProperty("EngineAssist").SetValue(settings, true);
        var vector = New("LandingVector", -0.3, 0.0, 0.0);
        var dockingCommand = Activator.CreateInstance(Mod.GetType("KITN.DockingCommand"),
            Enum.Parse(Mod.GetType("KITN.DockingPhase"), "Approaching"), vector, Enum.Parse(Mod.GetType("KITN.DockingIssue"), "None"), 0.0, New("LandingVector"), true);
        var update = controller.GetType().GetMethod("UpdateEngineAssist", BindingFlags.Instance | BindingFlags.NonPublic);
        double throttle = (double)update.Invoke(controller, new object[] { vehicle, 2.0, 0.02, dockingCommand, 0.1, 0.01 });
        Check(Math.Abs(throttle - 0.1) < 1e-5, "Actual optional assist does not issue a bounded rear-engine pulse");
        var setInputs = controller.GetType().GetMethod("SetInputs", BindingFlags.Static | BindingFlags.NonPublic);
        setInputs.Invoke(null, new object[] { vehicle, ThrusterMapFlags.None, throttle });
        var inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
        Check(inputs.EngineOn && Math.Abs(inputs.EngineThrottle - 0.1) < 1e-5, "Engine push never reaches KSA manual controls");
        Check((double)update.Invoke(controller, new object[] { vehicle, 2.02, 0.02, dockingCommand, 0.1, 0.01 }) == 0,
            "Engine assist keeps the main engine on across guidance frames");
        setInputs.Invoke(null, new object[] { vehicle, ThrusterMapFlags.TranslateBackward, 0.0 });
        inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
        Check(!inputs.EngineOn && inputs.EngineThrottle == 0 && inputs.ThrusterCommandFlags == ThrusterMapFlags.TranslateBackward,
            "Cutoff or RCS braking is suppressed by engine assist");
        // Run the actual coupled Update, not just the planner/helper.
        controller.GetType().GetMethod("ResetEngineAssist", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
        Set(target, "FlightPlan", vehicle.FlightPlan);
        Set(controller, "_activeVehicle", vehicle); Set(controller, "_lastTime", 9.98);
        Set(controller, "_standOff", 35.0); Set(controller, "_minimumDeltaV", new float3(0.01f, 0.01f, 0.01f));
        Set(controller, "PointingError", 0.0); Set(controller, "CanMatch", true); Set(controller, "CanDock", true);
        Set(controller, "Distance", 90.0); Set(controller, "RelativeSpeed", 0.1);
        settings.GetType().GetProperty("EngineAssist").SetValue(settings, false);
        Call(controller, "Update", vehicle, 10.0, true);
        inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
        Check(!inputs.EngineOn && inputs.EngineThrottle == 0, "Default-off assist starts an engine in real Update");
        settings.GetType().GetProperty("EngineAssist").SetValue(settings, true);
        Call(controller, "Update", vehicle, 10.02, true);
        inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
        Check(inputs.EngineOn && inputs.EngineThrottle > 0 && (inputs.ThrusterCommandFlags & ThrusterMapFlags.TranslateForward) == 0,
            "Real Update fails to send the engine push or duplicates it with forward RCS");
        Call(controller, "Update", vehicle, 10.04, true);
        inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
        Check(!inputs.EngineOn && inputs.EngineThrottle == 0, "Real Update does not cut off its one-frame engine push");
        Call(controller, "Stop", false);
        inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
        Check(!inputs.EngineOn && inputs.ThrusterCommandFlags == ThrusterMapFlags.None, "Stop leaves engine-assist/RCS commands active");
        core.MinimumPulseTime = 1;
        Check((double)update.Invoke(controller, new object[] { vehicle, 20.0, 0.02, dockingCommand, 0.1, 0.01 }) == 0,
            "Long minimum engine burn is not rejected by the actual controller");
        foreach (string locale in new[] { "en-US", "de-DE" })
        {
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            string status = (string)controller.GetType().GetProperty("DockingEngineStatus").GetValue(controller);
            Check(!status.Contains("dock.engine.") && status.Contains(locale == "de-DE" ? "zu stark" : "too strong"),
                "Minimum-impulse fallback reason is not localized in " + locale);
        }
        Console.WriteLine("PASS native engine assist: actual aft nozzle/minimum thrust/mass, wrong layouts and fuel rejection, hardware burn latch/cutoff, one-frame push and RCS braking");
    }
}
