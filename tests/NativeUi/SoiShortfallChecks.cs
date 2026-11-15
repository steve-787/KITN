using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static void SoiShortfallChecks()
    {
        const double mu = 3.986004418e14, r1 = 7e6, r2 = 42.164e6;
        var central = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        ((PlannerBodyProxy)central).Mu = mu;
        double axis = (r1 + r2) / 2;
        double arrival = Math.PI * Math.Sqrt(axis * axis * axis / mu);
        double nominal = Math.Sqrt(mu * (2 / r1 - 1 / axis));
        var outgoing = Orbit.CreateFromStateCci(central, new UniverseTime(0), new double3(r1, 0, 0), new double3(0, nominal - 100, 0), byte4.Zero);
        var target = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
        Set(target, "Id", "Test destination"); Set(target, "SphereOfInfluence", 100000.0);
        double phase = Math.PI - Math.Sqrt(mu / (r2 * r2 * r2)) * arrival;
        target.Orbit = Orbit.CreateFromStateCci(central, new UniverseTime(0), new double3(r2 * Math.Cos(phase), r2 * Math.Sin(phase), 0),
            new double3(-Math.Sqrt(mu / r2) * Math.Sin(phase), Math.Sqrt(mu / r2) * Math.Cos(phase), 0), byte4.Zero);
        var plan = FlightPlan.CreateUninitialized(KeyHash.Zero);
        plan.Patches.Add(new PatchedConic(new UniverseTime(0), UniverseTime.EndOfTime, PatchTransition.Burn, PatchTransition.Final, outgoing, KeyHash.Zero));
        object Vector(double x, double y, double z) => New("LandingVector", x, y, z);
        object solution = New("TransferSolution", 300.0, arrival - 300, Vector(r1, 0, 0), Vector(0, 100, 0), Vector(0, 1, 0), 0.0, false);
        var type = Mod.GetType("KITN.ManeuverPlanner");
        var estimate = type.GetMethod("EstimateSoiCorrection", BindingFlags.Static | BindingFlags.NonPublic);
        double missing = (double)estimate.Invoke(null, new[] { (object)plan, solution, target });
        Check(double.IsFinite(missing) && missing > 0 && missing < 101, "The native post-burn prediction does not recover the known SOI underburn");
        var uncomputed = FlightPlan.CreateUninitialized(KeyHash.Zero);
        uncomputed.Patches.Add(new PatchedConic(new UniverseTime(0), UniverseTime.EndOfTime, PatchTransition.Final, PatchTransition.Final, outgoing, KeyHash.Zero));
        Check(double.IsNaN((double)estimate.Invoke(null, new[] { (object)uncomputed, solution, target })), "A pre-burn coast invents a missing delta-v");
        var interrupted = FlightPlan.CreateUninitialized(KeyHash.Zero);
        interrupted.Patches.Add(new PatchedConic(new UniverseTime(0), new UniverseTime(arrival / 2), PatchTransition.Burn, PatchTransition.Encounter, outgoing, KeyHash.Zero));
        Check(double.IsNaN((double)estimate.Invoke(null, new[] { (object)interrupted, solution, target })),
            "A correction estimate extrapolates through an intervening SOI transition");

        object planner = New("ManeuverPlanner"); Set(planner, "Target", target); Set(planner, "UnavailableReason", null);
        object request = New("TransferRequest");
        Set(request, "Earliest", 300.0); Set(request, "Latest", 1000.0); Set(request, "MinimumDuration", 10000.0); Set(request, "MaximumDuration", 30000.0);
        Set(request, "Width", 2); Set(request, "Height", 2);
        object grid = New("TransferGrid", request); ((Array)grid.GetType().GetField("Cells").GetValue(grid)).SetValue(solution, 0);
        Set(planner, "Grid", grid); Set(Window, "Planner", planner); Set(Window, "_homeOpen", false); Set(Window, "_tab", 5);
        var offer = type.GetMethod("OfferBetterRoute", BindingFlags.Instance | BindingFlags.NonPublic);
        offer.Invoke(planner, new[] { (object)"transfer.no_encounter", plan, solution });
        Check((double)type.GetProperty("MissingSoiDeltaV").GetValue(planner) == missing, "Route rejection loses its computed delta-v estimate");
        foreach (string locale in new[] { "en-US", "de-DE" })
        {
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            for (int frame = 0; frame < 45; frame++) Frame(true);
            uint red = ImGui.ColorConvertFloat4ToU32(ConsoleStyle.Danger);
            int warningInk = DrawLists("/kitn-card-transfer-status").SelectMany(list => list.VtxBuffer.Span.ToArray()).Count(vertex => vertex.col == red);
            Check(warningInk > 100, "The SOI error and estimated delta-v are not shown in red");
            Save("missing-soi-dv-" + locale + ".png", 800, 670, 12, 12);
        }
        Call(planner, "Select", 0);
        Check(double.IsNaN((double)type.GetProperty("MissingSoiDeltaV").GetValue(planner))
            && !(bool)type.GetProperty("NeedsBetterRoute").GetValue(planner), "Selecting a new route leaves the previous missing delta-v visible");
        offer.Invoke(planner, new[] { (object)"transfer.no_encounter", null, Activator.CreateInstance(solution.GetType()) });
        Check(double.IsNaN((double)type.GetProperty("MissingSoiDeltaV").GetValue(planner)), "Unavailable prediction data produces a fabricated numeric shortfall");
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("missing-soi-dv-unknown.png", 800, 670, 12, 12);
        ((IDisposable)planner).Dispose();
        Console.WriteLine("PASS SOI warning: native prediction estimate, red localized display, unavailable-data handling and reset on route selection");
    }
}
