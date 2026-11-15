using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static void RemoteControlChecks()
    {
        var parent = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var bubble = (PhysicsBubble)RuntimeHelpers.GetUninitializedObject(typeof(PhysicsBubble)); GC.SuppressFinalize(bubble);
        Vehicle Craft(string name)
        {
            var craft = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(craft);
            Set(craft, "Id", name);
            Set(craft, "FlightPlan", new FlightPlan(Orbit.CreateFromStateCci(parent, new UniverseTime(100),
                new double3(7e6, 0, 0), new double3(0, Math.Sqrt(parent.Mu / 7e6), 0), byte4.Zero), KeyHash.Zero));
            Set(craft, "FlightComputer", new FlightComputer());
            var parts = (PartTree)RuntimeHelpers.GetUninitializedObject(typeof(PartTree)); GC.SuppressFinalize(parts);
            Set(craft, "Parts", parts);
            var state = (VehicleUpdateState)RuntimeHelpers.GetUninitializedObject(typeof(VehicleUpdateState)); GC.SuppressFinalize(state);
            Set(state, "Bubble", bubble); Set(craft, "_threadWorkerUpdateState", state);
            return craft;
        }
        var a = Craft("Remote Alpha"); var b = Craft("Player Beta"); var debris = Craft("Uncontrolled Debris");
        Set(debris, "FlightComputer", null);
        Set(a, "_bubbleLeader", a); Set(b, "_bubbleLeader", a); Set(debris, "_bubbleLeader", a);
        object Property(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
        bool Flag(object value, string name) => (bool)Property(value, name);
        var fleet = New("RemoteControlFleet", Store);
        object system = new object();
        var controlOverride = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.Static | BindingFlags.NonPublic);
        bool previousOverride = (bool)controlOverride.GetValue(null);
        void Observe(Vehicle player, double time, bool remote = true) => Call(fleet, "Observe", player, new[] { a, b, debris }, remote, system, time);
        var inputs = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.Instance | BindingFlags.NonPublic);
        object alpha = null;
        try
        {
            controlOverride.SetValue(null, true);
            Observe(a, 100);
            alpha = Property(fleet, "Selected");
            var profile = (KITN.LaunchProfile)Property(alpha, "Profile");
            profile.DebugLoggingEnabled = false;
            profile.TargetApoapsisKm = 0;
            var clock = Property(alpha, "Clock"); Call(clock, "BeginCountdown", 10, 100.0); Call(clock, "Advance", 104.0);
            Observe(b, 104);
            Check(ReferenceEquals(Property(Property(fleet, "Selected"), "Vehicle"), b) && Flag(clock, "IsCountingDown"),
                "Player vessel switching cancels the previous craft or fails to follow the new craft");
            profile.TargetApoapsisKm = 200;
            Check((bool)Call(fleet, "Select", a) && ReferenceEquals(Property(fleet, "Selected"), alpha), "Remote selection lost per-vessel state");
            Check(!((System.Collections.Generic.IEnumerable<Vehicle>)Property(fleet, "Available")).Contains(debris), "Uncontrollable vessel appears in Remote Control");
            a.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Auto; a.FlightComputer.RCSMode = FlightComputerRCSMode.Enabled;
            inputs.SetValue(a, new ManualControlInputs { EngineOn = true, EngineThrottle = 0.3f, ThrusterCommandFlags = ThrusterMapFlags.TranslateForward });
            var state = typeof(Vehicle).GetField("_threadWorkerUpdateState", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a);
            Set(state, "Bubble", null); Observe(b, 104);
            var cleared = (ManualControlInputs)inputs.GetValue(a);
            Check(Flag(alpha, "IsSuspended") && Flag(clock, "IsCountingDown") && !cleared.EngineOn && cleared.EngineThrottle == 0
                && cleared.ThrusterCommandFlags == ThrusterMapFlags.None && a.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Manual,
                "Loss of physics range cancels the operation or leaves stale native control commands");
            Check(!(bool)Call(fleet, "Select", a), "Out-of-range craft can be newly remote-controlled");
            Observe(b, 304); Set(state, "Bubble", bubble); Observe(b, 304);
            Check(!Flag(alpha, "IsSuspended") && Flag(clock, "IsCountingDown") && !(bool)Call(clock, "Advance", 304.0)
                && (double)Property(clock, "Seconds") == 6 && a.FlightComputer.AttitudeMode == FlightComputerAttitudeMode.Auto,
                "Range recovery lost the operation, native settings, or remaining countdown");
            var resumedInputs = (ManualControlInputs)inputs.GetValue(a);
            Check(!resumedInputs.EngineOn && resumedInputs.ThrusterCommandFlags == ThrusterMapFlags.None,
                "Range recovery replays stale throttle or translation commands");
            Call(clock, "CancelCountdown");
            var ascent = Property(alpha, "Guidance"); Call(ascent, "Start");
            Observe(a, 304); Observe(b, 304);
            Check(Flag(ascent, "IsActive"), "Switching craft aborts a running ascent");
            Set(state, "Bubble", null); Observe(b, 304); Observe(b, 504);
            Check(Flag(alpha, "IsSuspended") && Flag(ascent, "IsActive"), "Leaving physics range aborts a running ascent");
            Set(state, "Bubble", bubble); Observe(b, 504);
            Check(!Flag(alpha, "IsSuspended") && Flag(ascent, "IsActive"), "A suspended ascent is lost on range recovery");
            Call(ascent, "Abort", a, "user_abort");
            var rendezvous = Property(alpha, "Rendezvous");
            Set(rendezvous, "_activeVehicle", a); Set(rendezvous, "Target", b);
            Set(rendezvous, "Phase", Enum.Parse(Mod.GetType("KITN.DockingPhase"), "Aligning"));
            var options = Property(rendezvous, "DockingOptions"); Set(options, "ShowGuidanceOverlay", true);
            var preview = Property(rendezvous, "DockingPreview");
            Call(preview, "Holding", New("LandingVector", 50.0, 1.0, 0.0));
            var step = Property(preview, "Current");
            Observe(a, 505); Observe(b, 506);
            Check(Flag(rendezvous, "IsAutoDocking") && Equals(Property(preview, "Current"), step)
                && Flag(options, "ShowGuidanceOverlay"), "Player switching cancels docking or disables its path rendering");
            var targetState = typeof(Vehicle).GetField("_threadWorkerUpdateState", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);
            Set(targetState, "Bubble", null); Observe(a, 507); Observe(a, 600);
            Check(Flag(alpha, "IsSuspended") && Flag(rendezvous, "IsAutoDocking") && Equals(Property(preview, "Current"), step),
                "Target range loss cancels docking or clears the path preview");
            Set(targetState, "Bubble", bubble); Observe(a, 601);
            Check(!Flag(alpha, "IsSuspended") && Flag(rendezvous, "IsAutoDocking"), "Docking does not recover after prolonged target range loss");
            Observe(b, 602); Call(fleet, "Select", a);
            var remote = New("RemoteControlWindow");
            Set(remote, "Layout", New("PersistentWindowLayout", Store, "remote_control"));
            foreach (string locale in new[] { "en-US", "de-DE" })
            {
                Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
                for (int frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame(); object[] args = { true, fleet, b }; Call(remote, "Draw", args); ImGui.Render();
                }
                Check(ImGui.GetDrawData().TotalVtxCount > 1000, "Remote Control widget did not render in " + locale);
                Save("remote-control-" + locale + ".png", 1600, 1000);
                Set(Window, "ControlSession", alpha); Set(Window, "Rendezvous", rendezvous); Set(Window, "_settingsOpen", true);
                for (int frame = 0; frame < 4; frame++) Frame(true, true);
                Save("remote-settings-" + locale + ".png", 580, 420, 12, 12);
            }
            Observe(b, 603, false);
            Check(!Flag(rendezvous, "IsActive") && ReferenceEquals(Property(Property(fleet, "Selected"), "Vehicle"), b),
                "Disabling remote control does not cancel background automation and return selection to the player");
            Observe(a, 604); Call(clock, "BeginCountdown", 10, 604.0); Set(state, "Bubble", null); Observe(b, 605);
            Set(alpha, "PendingAction", "remote_cancel");
            Check((bool)Call(alpha, "HandleCancellation") && !Flag(clock, "IsCountingDown"), "Explicit cancellation cannot stop an out-of-range operation");
            Set(state, "Bubble", bubble); Observe(a, 606); Call(clock, "BeginCountdown", 10, 606.0);
            Observe(a, 1);
            Check(!Flag(clock, "IsCountingDown") && !ReferenceEquals(alpha, Property(fleet, "Selected")), "Save reversion retained stale vessel automation");

            var warp = Mod.GetType("KITN.PhysicsWarp");
            void Warp(bool enabled, double dt, double speed)
            {
                Universe.SetSimulationSpeed(speed, false);
                warp.GetMethod("Configure").Invoke(null, new object[] { enabled, dt });
                warp.GetMethod("LimitForControl").Invoke(null, new object[] { a, false });
            }
            Warp(true, 1.0 / 60, 4); Check(Universe.SimulationSpeed == 4, "Healthy native physics warp is forcibly cancelled");
            var stepField = typeof(Universe).GetField("_lastSimStep", BindingFlags.Static | BindingFlags.NonPublic);
            object previousStep = stepField.GetValue(null);
            try
            {
                object slowStep = Activator.CreateInstance(stepField.FieldType); Set(slowStep, "DeltaTime", 0.005);
                stepField.SetValue(null, slowStep);
                Warp(true, 1.0 / 60, 4);
                Check(Universe.SimulationSpeed == 1, "Physics falling behind does not lower warp speed");
            }
            finally { stepField.SetValue(null, previousStep); }
            Warp(true, 0.06, 4); Check(Universe.SimulationSpeed == 1, "Slow native frames retain unsafe warp");
            Warp(false, 1.0 / 60, 2); Check(Universe.SimulationSpeed == 1, "Disabled physics warp permits accelerated control");
            Console.WriteLine("PASS native vessel switching, remote selection, prolonged range loss/recovery, docking path state, paused cancellation, save reversion, EN/DE widget and physics-warp limits");
        }
        finally
        {
            ((IDisposable)fleet).Dispose(); controlOverride.SetValue(null, previousOverride);
            Universe.SetSimulationSpeed(1, false);
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { "en-US" });
        }
    }
}
