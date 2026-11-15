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
    private static void HohmannChecks()
    {
        var earth = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        Orbit Circle(double radius, double phase, double inclination = 0)
        {
            double speed = Math.Sqrt(earth.Mu / radius);
            return Orbit.CreateFromStateCci(earth, new UniverseTime(0),
                new double3(radius * Math.Cos(phase), radius * Math.Sin(phase) * Math.Cos(inclination), radius * Math.Sin(phase) * Math.Sin(inclination)),
                new double3(-speed * Math.Sin(phase), speed * Math.Cos(phase) * Math.Cos(inclination), speed * Math.Cos(phase) * Math.Sin(inclination)), byte4.Zero);
        }
        var moon = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
        Set(moon, "Id", "Moon"); Set(moon, "Mass", 4.9049e12 / 6.6743e-11); Set(moon, "SphereOfInfluence", 66e6);
        typeof(Celestial).GetField("_meanRadius").SetValue(moon, 1737100.0);
        typeof(Celestial).GetField("_cci2Cce", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(moon, doubleQuat.Identity);
        moon.Orbit = Circle(384.4e6, 2.2, 0.08);
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle)); GC.SuppressFinalize(vehicle);
        var controllable = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.NonPublic | BindingFlags.Static);
        bool previous = (bool)controllable.GetValue(null); controllable.SetValue(null, true);
        try
        {
            Set(vehicle, "FlightPlan", new FlightPlan(Circle(7e6, 0.4), KeyHash.Zero));
            var fc = new FlightComputer { BurnMode = FlightComputerBurnMode.Manual };
            Set(vehicle, "FlightComputer", fc);
            var planner = New("ManeuverPlanner");
            Set(planner, "_vehicle", vehicle); Set(planner, "_parent", earth); Set(planner, "Target", moon);
            Set(planner, "Now", 100.0); Set(planner, "OriginName", "Earth"); Set(planner, "UnavailableReason", null);
            Set(planner, "HohmannMode", true);
            var settings = New("HohmannSettings");
            Set(settings, "DepartureLeadSeconds", 900f); Set(settings, "SynodicWindows", 2);
            Set(settings, "IncludeCapture", false); Set(settings, "AutoWarp", false);
            Set(planner, "HohmannOptions", settings);
            Check(!(bool)planner.GetType().GetProperty("AutoWarp").GetValue(planner)
                && !(bool)planner.GetType().GetProperty("IncludeCapture").GetValue(planner), "Planner ignores Hohmann capture or warp preferences");
            var snapshot = planner.GetType().GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(planner, new[] { vehicle });
            Check((bool)Mod.GetType("KITN.TransferHohmann").GetMethod("Configure").Invoke(null, new[] { snapshot, settings }),
                "Native orbit snapshot cannot configure Hohmann search");
            Set(snapshot, "Width", 9); Set(snapshot, "Height", 9);
            var phases = new[] { new FiniteBurnPhase(955522.3125, 222.89703369140625, 52813.765625, 220) };
            Mod.GetType("KITN.TransferBurnRefiner").GetMethod("Attach").Invoke(null, new object[] { snapshot, phases });
            var grid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null, new object[] { snapshot, CancellationToken.None, null });
            Set(planner, "Grid", grid); Call(planner, "SelectBest", false);
            var selected = planner.GetType().GetProperty("Selected").GetValue(planner);
            Check((bool)selected.GetType().GetField("Valid").GetValue(selected), "Native finite-burn Hohmann search has no valid route");
            double Number(string name) => (double)selected.GetType().GetField(name).GetValue(selected);
            var deltaObject = selected.GetType().GetField("DeltaV").GetValue(selected);
            var delta = new double3((double)deltaObject.GetType().GetField("X").GetValue(deltaObject),
                (double)deltaObject.GetType().GetField("Y").GetValue(deltaObject), (double)deltaObject.GetType().GetField("Z").GetValue(deltaObject));
            var departure = new UniverseTime(Number("Departure"));
            var burn = FiniteBurnPredictor.Predict(vehicle.Orbit, departure, OrbitalTransfers.CciToVlf(vehicle.Orbit, departure, delta), phases);
            var coast = Orbit.CreateFromStateCci(earth, burn.Cutoff, burn.PositionCci, burn.VelocityCci, byte4.Zero);
            var arrival = new UniverseTime(Number("Departure") + Number("Duration"));
            Check((coast.GetStateVectorsAt(arrival).PositionCci - moon.Orbit.GetStateVectorsAt(arrival).PositionCci).Length() < moon.SphereOfInfluence,
                "Native finite-burn propagation misses the target SOI");
            Check(fc.BurnMode == FlightComputerBurnMode.Manual && vehicle.BurnPlan.BurnCount == 0, "Calculation starts an unsolicited burn");

            Set(Window, "Planner", planner); Set(Window, "_homeOpen", false); Set(Window, "_tab", 5);
            var page = Window.GetType().GetField("_transferPage", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
            foreach (string locale in new[] { "en-US", "de-DE" })
            {
                Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
                for (int frame = 0; frame < 4; frame++) Frame(true);
                Check(((string[])page.GetType().GetField("_modes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(page)).Length == 4,
                    "Hohmann transfer mode is missing");
                Save("hohmann-transfer-" + locale + ".png", 800, 670, 12, 12);
                for (int frame = 0; frame < 3; frame++) Frame(true, true);
                Save("hohmann-transfer-minimum-" + locale + ".png", 580, 420, 12, 12);
            }
            for (int frame = 0; frame < 3; frame++) Frame(true);
            Click(110, 162, "autopilot");
            Check(!(bool)planner.GetType().GetField("HohmannMode").GetValue(planner)
                && planner.GetType().GetProperty("Grid").GetValue(planner) == null
                && (bool)planner.GetType().GetProperty("AutoWarp").GetValue(planner), "Mode switch retains a Hohmann grid or leaks Hohmann warp preferences");
            Click(700, 162, "autopilot");
            Check((bool)planner.GetType().GetField("HohmannMode").GetValue(planner), "Hohmann mode cannot be selected");

            var options = page.GetType().GetMethod("DrawSearchOptions", BindingFlags.NonPublic | BindingFlags.Instance);
            var io = ImGui.GetIO(); bool locked = false; float2 reset = default, lastMin = default, lastMax = default;
            void OptionsFrame()
            {
                ImGui.NewFrame(); bool open = true;
                ImGui.SetNextWindowPos(new float2(20, 20), ImGuiCond.Always); ImGui.SetNextWindowSize(new float2(780, 650), ImGuiCond.Always);
                ImGui.SetNextWindowFocus(); ImGui.Begin("Hohmann settings test", ref open, ImGuiWindowFlags.NoSavedSettings);
                options.Invoke(page, new object[] { planner, locked });
                lastMin = ImGui.GetItemRectMin(); lastMax = ImGui.GetItemRectMax();
                reset = new float2(70, ImGui.GetItemRectMin().Y - ImGui.GetStyle().ItemSpacing.Y - ImGui.GetFrameHeight() / 2);
                ImGui.End(); ImGui.Render();
            }
            for (int frame = 0; frame < 3; frame++) OptionsFrame();
            io.AddMousePosEvent((lastMin.X + lastMax.X) / 2, (lastMin.Y + lastMax.Y) / 2); OptionsFrame();
            io.AddMouseButtonEvent(0, true); OptionsFrame(); io.AddMouseButtonEvent(0, false); OptionsFrame();
            foreach (string locale in new[] { "en-US", "de-DE" })
            {
                Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
                for (int frame = 0; frame < 4; frame++) OptionsFrame();
                Save("hohmann-settings-" + locale + ".png", 800, 670, 12, 12);
            }
            void ResetClick()
            { io.AddMousePosEvent(reset.X, reset.Y); OptionsFrame(); io.AddMouseButtonEvent(0, true); OptionsFrame(); io.AddMouseButtonEvent(0, false); OptionsFrame(); }
            locked = true; OptionsFrame(); ResetClick();
            Check(!(bool)Call(planner.GetType().GetField("HohmannOptions").GetValue(planner), "Matches", New("HohmannSettings")), "Locked settings can be reset");
            locked = false; OptionsFrame(); ResetClick();
            Check((bool)Call(planner.GetType().GetField("HohmannOptions").GetValue(planner), "Matches", New("HohmannSettings"))
                && (bool)Call(Store.GetType().GetProperty("Hohmann").GetValue(Store), "Matches", New("HohmannSettings")), "Reset settings do not reach the planner and saved preferences");
            ((IDisposable)planner).Dispose();
            Console.WriteLine("PASS native Hohmann: KSA orbit snapshots, finite-burn target encounter, four selectable modes, independent capture/warp settings, localized controls and locked defaults");
        }
        finally { controllable.SetValue(null, previous); }
    }
}
