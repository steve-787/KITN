using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static void DockingOverlayChecks()
    {
        var parent = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(vehicle);
        var target = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(target);
        var plan = new FlightPlan(Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(),
            new double3(7e6, 0, 0), new double3(0, Math.Sqrt(parent.Mu / 7e6), 0), byte4.Zero), KeyHash.Zero);
        Set(vehicle, "FlightPlan", plan); Set(target, "FlightPlan", plan);
        Set(vehicle, "FlightComputer", new FlightComputer { Helm2Asmb = doubleQuat.Identity, Ctrl2Asmb = doubleQuat.Identity });
        Set(target, "FlightComputer", new FlightComputer());
        Set(vehicle, "Body2Cce", doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, 6 * Math.PI / 180), FlightComputerAttitudeTrackTargetEx.YAW_FLIP));
        Set(target, "Body2Cce", doubleQuat.Identity);
        var controller = New("RendezvousController");
        Set(controller, "_activeVehicle", vehicle); Set(controller, "Target", target);
        Set(controller, "OwnPort", DockingTestPort()); Set(controller, "TargetPort", DockingTestPort());
        Set(controller, "CanMatch", true); Set(controller, "CanDock", true);
        Set(controller, "_lastTime", 0.0); Set(controller, "_standOff", 45.0); Set(controller, "_dockingSafeDistance", 20.0);
        Set(controller, "_acceleration", 0.02); Set(controller, "_minimumDeltaV", new float3(0.004f, 0.004f, 0.004f));
        Set(controller, "_relativePosition", new double3(55, 0.08, 0)); Set(controller, "PointingError", 0.0);
        Set(controller, "Distance", 55.0); Set(controller, "RelativeSpeed", 0.0); Set(controller, "LateralError", 0.08);
        Call(controller, "Update", vehicle, 1.0, true);
        var overlay = New("DockingGuidanceOverlay");
        var scene = overlay.GetType().GetMethod("DrawScene", BindingFlags.Instance | BindingFlags.NonPublic);
        var preview = controller.GetType().GetProperty("DockingPreview").GetValue(controller);
        var position = controller.GetType().GetProperty("DockingRelativePosition").GetValue(controller);
        var camera = new Camera(new int2(1000, 760));
        camera.LookAt(new double3(95, -130, 75), new double3(10, 0, 0), double3.UnitZ); camera.OnFrame(0);
        var page = New("DockingPage"); Set(page, "Settings", Store);
        var io = ImGui.GetIO();
        void Draw(bool showScene = true, double3? sceneOrigin = null)
        {
            ImGui.NewFrame();
            if (showScene) scene.Invoke(overlay, new object[] { ImGui.GetBackgroundDrawList(), camera,
                new float2(0, 0), new float2(1000, 760), sceneOrigin ?? -camera.PositionEcl,
                doubleQuat.Identity, preview, position, 45.0, 0.3 });
            ImGui.SetNextWindowPos(new float2(1010, 20)); ImGui.SetNextWindowSize(new float2(575, 950));
            ConsoleStyle.PushWidgetStyle();
            ImGui.Begin("Docking overlay checks###DockingOverlayChecks", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar);
            Call(page, "Draw", controller, false);
            ImGui.End(); ConsoleStyle.PopWidgetStyle(); ImGui.Render();
            foreach (var list in ImGui.GetDrawData().CmdLists.Span)
                foreach (var vertex in list.VtxBuffer.Span)
                    Check(float.IsFinite(vertex.pos.X) && float.IsFinite(vertex.pos.Y), "3D camera projection generated invalid draw vertices");
        }
        foreach (string locale in new[] { "en-US", "de-DE" })
        {
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            Draw(); Draw(); Draw();
            int lines = (int)overlay.GetType().GetProperty("LastDrawnSegments").GetValue(overlay);
            Check(lines > 100 && lines <= 320, "World overlay has no visible wire geometry or exceeds its drawing budget");
            Save("docking-overlay-" + locale + ".png", 1600, 1000);
            // First row is an independently editable overlay toggle, even during autodocking.
            var options = controller.GetType().GetProperty("DockingOptions").GetValue(controller);
            bool before = (bool)options.GetType().GetProperty("ShowGuidanceOverlay").GetValue(options);
            io.AddMousePosEvent(1535, 40); Draw(); io.AddMouseButtonEvent(0, true); Draw(); io.AddMouseButtonEvent(0, false); Draw();
            options = controller.GetType().GetProperty("DockingOptions").GetValue(controller);
            Check((bool)options.GetType().GetProperty("ShowGuidanceOverlay").GetValue(options) != before,
                "Docking overlay toggle cannot be changed while autopilot is active in " + locale);
            Check(Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "Live overlay toggle is not saved");
            object activity = Mod.GetType("KITN.ModActivity").GetMethod("Read").Invoke(null,
                new object[] { Guidance, Flight, Clock, Landing, controller, null, false, true });
            string ActivityField(string key) => (string)activity.GetType().GetField(key).GetValue(activity);
            Check(ActivityField("Phase") == controller.GetType().GetProperty("DockingCurrentStep").GetValue(controller).ToString()
                && !string.IsNullOrEmpty(ActivityField("Next")) && ActivityField("Detail").Contains("m/s")
                && ActivityField("Extra").Contains("°") && ActivityField("Label").Contains("~"),
                "Tiny widget omits docking current/next activity, speed, remaining rotation or ETA");
            var widget = New("MinimizedWindow", Store); Set(widget, "Enabled", true);
            ImGui.NewFrame(); Call(widget, "Draw", activity); ImGui.Render();
            var bounds = Call(Store, "Find", "minimized_activity");
            Save("docking-widget-" + locale + ".png", 312, 140, (int)Value(bounds, "X") - 16, (int)Value(bounds, "Y") - 16);
        }
        Draw(true, camera.GetForwardEcl() * -1000);
        Check((int)overlay.GetType().GetProperty("LastDrawnSegments").GetValue(overlay) == 0, "Geometry behind the camera draws reflected paths");
        Call(controller, "Stop", false); Call(overlay, "Draw", controller);
        Check((int)overlay.GetType().GetProperty("LastDrawnSegments").GetValue(overlay) == 0, "Stopped docking keeps its overlay visible");
        Console.WriteLine("PASS docking overlay: native 3D camera projection, bounded/finite geometry, behind-camera culling, live EN/DE toggle persistence and compact current/next/motion/ETA");
    }
}
