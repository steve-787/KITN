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
    private static void TransferRouteChecks()
    {
        const double sunMu = 1.32712440018e20, earthMu = 3.986004418e14, jupiterMu = 1.26686534e17;
        var sun = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var solar = (PlannerBodyProxy)sun; solar.Id = "Sun"; solar.Mu = sunMu; solar.Radius = 7e8; solar.Soi = double.PositiveInfinity;
        TerrestrialBody Body(string id, double mu, double radius, double soi, Orbit orbit)
        {
            var body = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
            Set(body, "Id", id); Set(body, "Mass", mu / 6.6743e-11); Set(body, "SphereOfInfluence", soi);
            typeof(Celestial).GetField("_meanRadius").SetValue(body, radius);
            typeof(Celestial).GetField("_cci2Cce", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(body, doubleQuat.Identity);
            body.Orbit = orbit; return body;
        }
        Orbit Circle(IParentBody parent, double radius, double phase = 0, double inclination = 0)
        {
            double speed = Math.Sqrt(parent.Mu / radius);
            return Orbit.CreateFromStateCci(parent, new UniverseTime(0),
                new double3(radius * Math.Cos(phase), radius * Math.Sin(phase) * Math.Cos(inclination), radius * Math.Sin(phase) * Math.Sin(inclination)),
                new double3(-speed * Math.Sin(phase), speed * Math.Cos(phase) * Math.Cos(inclination), speed * Math.Cos(phase) * Math.Sin(inclination)), byte4.Zero);
        }
        double duration = Math.PI * Math.Sqrt(Math.Pow((149.6e9 + 778.6e9) / 2, 3) / sunMu);
        var earth = Body("Earth", earthMu, 6371000, 924e6, Circle(sun, 149.6e9));
        var jupiter = Body("Jupiter", jupiterMu, 69911000, 48.2e9,
            Circle(sun, 778.6e9, Math.PI - Math.Sqrt(sunMu / Math.Pow(778.6e9, 3)) * duration));
        var jupiterRotation = doubleQuat.CreateFromAxisAngle(double3.UnitX, 0.3);
        typeof(Celestial).GetField("_cci2Cce", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(jupiter, jupiterRotation);
        var himalia = Body("Himalia", 4.5e8, 85000, 16e6, Circle(jupiter, 11.46e9, 1.1, 0.48));
        var moon = Body("Moon", 4.9049e12, 1737100, 66e6, Circle(earth, 384.4e6));
        var type = Mod.GetType("KITN.ManeuverPlanner");
        var resolve = type.GetMethod("ResolveRouteTarget", BindingFlags.Static | BindingFlags.NonPublic);
        Check(ReferenceEquals(resolve.Invoke(null, new object[] { earth, himalia }), jupiter), "Earth -> Himalia has no Jupiter intermediate leg");
        Check(ReferenceEquals(resolve.Invoke(null, new object[] { jupiter, himalia }), himalia), "Jupiter -> Himalia does not resolve the final leg");
        Check(ReferenceEquals(resolve.Invoke(null, new object[] { earth, moon }), moon), "Direct Earth -> Moon transfer changed");
        Check(ReferenceEquals(resolve.Invoke(null, new object[] { earth, jupiter }), jupiter), "Direct Earth -> Jupiter transfer changed");

        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        var fc = new FlightComputer { RCSMode = FlightComputerRCSMode.Enabled, BurnMode = FlightComputerBurnMode.Manual };
        Set(vehicle, "FlightComputer", fc);
        Set(vehicle, "FlightPlan", new FlightPlan(Circle(earth, 7e6), KeyHash.Zero));
        object planner = New("ManeuverPlanner");
        Set(planner, "_vehicle", vehicle); Set(planner, "_parent", earth); Set(planner, "Target", himalia);
        var controllable = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.NonPublic | BindingFlags.Static);
        bool previous = (bool)controllable.GetValue(null); controllable.SetValue(null, true);
        try
        {
            Check(type.GetMethod("ValidateRoute", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new object[] { vehicle }) == null,
                "Multiple hierarchies still block Earth -> Himalia");
            object first = type.GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new object[] { vehicle });
            Check((double)first.GetType().GetField("TargetMu").GetValue(first) == ((IParentBody)himalia).Mu,
                "Final capture uses Jupiter's gravitational mass");
            Check((bool)first.GetType().GetProperty("Ejection").GetValue(first), "First leg does not escape Earth's parking orbit");
            type.GetMethod("SetDefaults", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new[] { first });
            Check((float)type.GetField("PeriapsisKm").GetValue(planner) == 100,
                "Arrival altitude is still a Jupiter parking orbit");
            Check(ReferenceEquals(type.GetProperty("RouteTarget").GetValue(planner), himalia), "Route capture target is Jupiter instead of Himalia");
            var path = (Array)first.GetType().GetField("ArrivalPath").GetValue(first);
            Check(path.Length == 2, "Direct route lacks the parent and moon ephemerides");
            var arguments = new object[] { 0.0, null, null };
            Call(path.GetValue(1).GetType().GetField("Orbit").GetValue(path.GetValue(1)), "State", arguments);
            Check((Native(arguments[1]) - himalia.Orbit.GetStateVectorsAt(new UniverseTime(0)).PositionCci.Transform(jupiterRotation)).Length() < 0.001,
                "Nested moon orbit is not transformed from Jupiter's tilted frame");
            Set(first, "PeriapsisAltitude", 100000.0);
            var phases = new[] { new FiniteBurnPhase(955522.3125, 222.89703369140625, 52813.765625, 49474.73046875 / 222.89703369140625) };
            Mod.GetType("KITN.TransferBurnRefiner").GetMethod("Attach").Invoke(null, new object[] { first, phases });
            object route = SearchLeg(first, duration, "Earth -> Himalia direct");
            double departure = Number(route, "Departure"), arrival = departure + Number(route, "Duration");
            var time = new UniverseTime(departure);
            var delta = Native(route.GetType().GetField("DeltaV").GetValue(route));
            var parking = vehicle.Orbit;
            // Independently recompute the finite burn and every coast with native KSA propagation.
            var burn = FiniteBurnPredictor.Predict(parking, time, OrbitalTransfers.CciToVlf(parking, time, delta), phases);
            var departureOrbit = Orbit.CreateFromStateCci(earth, burn.Cutoff, burn.PositionCci, burn.VelocityCci, byte4.Zero);
            var escapeCoast = New("TransferCoast", earthMu, Vector(burn.PositionCci), Vector(burn.VelocityCci));
            double exitSeconds = (double)Call(escapeCoast, "OutgoingRadiusTime", earth.SphereOfInfluence);
            var exitTime = burn.Cutoff + exitSeconds;
            var escape = departureOrbit.GetStateVectorsAt(exitTime); var origin = earth.Orbit.GetStateVectorsAt(exitTime);
            var heliocentric = Orbit.CreateFromStateCci(sun, exitTime, origin.PositionCci + escape.PositionCci,
                origin.VelocityCci + escape.VelocityCci, byte4.Zero);
            double jupiterEntry = Entry(heliocentric, jupiter.Orbit, exitTime.Seconds(), arrival, jupiter.SphereOfInfluence);
            var shipAtJupiter = heliocentric.GetStateVectorsAt(new UniverseTime(jupiterEntry)); var jupiterAtEntry = jupiter.Orbit.GetStateVectorsAt(new UniverseTime(jupiterEntry));
            var jupiterApproach = Orbit.CreateFromStateCci(jupiter, new UniverseTime(jupiterEntry),
                (shipAtJupiter.PositionCci - jupiterAtEntry.PositionCci).Transform(jupiterRotation.Inverse()),
                (shipAtJupiter.VelocityCci - jupiterAtEntry.VelocityCci).Transform(jupiterRotation.Inverse()), byte4.Zero);
            Check(jupiterApproach.IsUnbound(), "A hidden capture burn inserts a Jupiter parking orbit");
            double himaliaEntry = Entry(jupiterApproach, himalia.Orbit, jupiterEntry, arrival, himalia.SphereOfInfluence);
            var shipAtHimalia = jupiterApproach.GetStateVectorsAt(new UniverseTime(himaliaEntry)); var moonAtEntry = himalia.Orbit.GetStateVectorsAt(new UniverseTime(himaliaEntry));
            var himaliaApproach = Orbit.CreateFromStateCci(himalia, new UniverseTime(himaliaEntry), shipAtHimalia.PositionCci - moonAtEntry.PositionCci,
                shipAtHimalia.VelocityCci - moonAtEntry.VelocityCci, byte4.Zero);
            Console.WriteLine($"  native Himalia Pe={himaliaApproach.Periapsis - himalia.MeanRadius:F2} m, departure={Number(route, "DepartureDv"):F2} m/s, final capture={Number(route, "CaptureDv"):F2} m/s");
            Check(Math.Abs(himaliaApproach.Periapsis - himalia.MeanRadius - 100000) < 2000,
                "Native finite-burn propagation misses the final moon periapsis");
            var pe = himaliaApproach.GetNextPeriapsisTime(new UniverseTime(himaliaEntry)).Value;
            Check(Math.Abs((double)route.GetType().GetProperty("ArrivalTime").GetValue(route) - pe.Seconds()) < 1,
                "Displayed arrival time omits the final moon approach");
            var peState = himaliaApproach.GetStateVectorsAt(pe);
            var captureDelta = peState.VelocityCci * (Math.Sqrt(((IParentBody)himalia).Mu / peState.PositionCci.Length()) / peState.VelocityCci.Length() - 1);
            var captured = Orbit.CreateFromStateCci(himalia, pe, peState.PositionCci, peState.VelocityCci + captureDelta, byte4.Zero);
            Check(captured.IsBound() && Math.Abs(captured.Apoapsis - captured.Periapsis) < 1, "Final burn fails to enter Himalia orbit");
            Check(Math.Abs(captureDelta.Length() - Number(route, "CaptureDv")) < 5, "Capture cost includes a Jupiter burn");
            var plan = FlightPlan.CreateUninitialized(KeyHash.Zero);
            plan.Patches.Add(new PatchedConic(burn.Cutoff, exitTime, PatchTransition.Burn, PatchTransition.Escape, departureOrbit, KeyHash.Zero));
            plan.Patches.Add(new PatchedConic(exitTime, new UniverseTime(jupiterEntry), PatchTransition.Escape, PatchTransition.Encounter, heliocentric, KeyHash.Zero));
            plan.Patches.Add(new PatchedConic(new UniverseTime(jupiterEntry), new UniverseTime(himaliaEntry), PatchTransition.Encounter, PatchTransition.Encounter, jupiterApproach, KeyHash.Zero));
            plan.Patches.Add(new PatchedConic(new UniverseTime(himaliaEntry), pe + 1, PatchTransition.Encounter, PatchTransition.Final, himaliaApproach, KeyHash.Zero));
            var inspect = type.GetMethod("InspectPrediction", BindingFlags.NonPublic | BindingFlags.Static);
            Check(inspect.Invoke(null, new object[] { plan, earth, himalia, false }) == null, "Native validation rejects the complete direct Himalia route");
            var finalPatch = plan.Patches[plan.Patches.Count - 1];
            plan.Patches.RemoveAt(plan.Patches.Count - 1);
            Check((string)inspect.Invoke(null, new object[] { plan, earth, himalia, true }) == "transfer.no_encounter",
                "A Jupiter-only prediction is accepted as a final Himalia encounter");
            Check(vehicle.BurnPlan.BurnCount == 0 && fc.BurnMode == FlightComputerBurnMode.Manual, "Searching performs an unsolicited maneuver");
            Set(vehicle, "FlightPlan", plan);
            Set(planner, "_captureVehicle", vehicle); Set(planner, "_captureTarget", himalia); Set(planner, "_departureFinished", true);
            var updateCapture = type.GetMethod("UpdateCapture", BindingFlags.NonPublic | BindingFlags.Instance);
            updateCapture.Invoke(planner, new object[] { vehicle, burn.Cutoff.Seconds() });
            Check(fc.BurnPlan.BurnCount == 0, "Jupiter-only prediction creates an intermediate capture burn");
            var meshes = typeof(ModLibrary).GetField("AllMeshes", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (string id in new[] { "Sphere", "ConeWithTailMesh", "ConeMesh" })
            {
                var mesh = new MeshReference { Id = id }; mesh.SetHash();
                meshes.GetType().GetMethod("Register").Invoke(meshes, new object[] { mesh });
            }
            plan.Patches.Add(finalPatch);
            updateCapture.Invoke(planner, new object[] { vehicle, burn.Cutoff.Seconds() + 2 });
            Check(fc.BurnPlan.BurnCount == 1 && ReferenceEquals(fc.BurnPlan.FirstBurn.Patch.PrimaryBody, himalia)
                && Math.Abs(fc.BurnPlan.FirstBurn.Time.Seconds() - pe.Seconds()) < 0.01 && fc.BurnMode == FlightComputerBurnMode.Manual,
                "The controller does not create exactly one final Himalia capture node");
            fc.RemoveBurn(fc.BurnPlan.FirstBurn);
            type.GetMethod("ClearCapture", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, null);
            Set(planner, "_node", null); Set(vehicle, "FlightPlan", new FlightPlan(parking, KeyHash.Zero));
            var tinyMoon = Body("Small moon", 7.1e5, 11000, 16000, Circle(jupiter, 1e9));
            Set(planner, "Target", tinyMoon);
            object tinyRequest = type.GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new object[] { vehicle });
            type.GetMethod("SetDefaults", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new[] { tinyRequest });
            double tinyRadius = tinyMoon.MeanRadius + (float)type.GetField("PeriapsisKm").GetValue(planner) * 1000;
            Check(tinyRadius > tinyMoon.MeanRadius && tinyRadius < tinyMoon.SphereOfInfluence, "Small-moon default capture orbit is outside its SOI");
            Check((string)type.GetMethod("ArrivalError", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { tinyMoon, 100000.0 }) == "transfer.arrival_soi",
                "Oversized arrival orbits have no explicit error");
            Set(planner, "Target", moon);
            object direct = type.GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new object[] { vehicle });
            Check(!(bool)direct.GetType().GetProperty("NestedArrival").GetValue(direct), "Direct Earth -> Moon transfers changed");
            Set(planner, "Target", himalia);

            Set(Window, "Planner", planner); Set(Window, "_homeOpen", false); Set(Window, "_tab", 5);
            Set(planner, "_parent", earth); Set(vehicle, "FlightPlan", new FlightPlan(Circle(earth, 7e6), KeyHash.Zero));
            Set(planner, "UnavailableReason", null);
            foreach (string locale in new[] { "en-US", "de-DE" })
            {
                Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
                for (int frame = 0; frame < 5; frame++) Frame(true, true);
                Save("himalia-via-jupiter-" + locale + ".png", 580, 600, 12, 12);
                Set(planner, "Target", jupiter); Set(planner, "UnavailableReason", "transfer.route_hierarchy");
                for (int frame = 0; frame < 5; frame++) Frame(true, true);
                var background = DrawLists("/kitn-card-transfer-plot").SelectMany(list => list.VtxBuffer.Span.ToArray())
                    .Where(vertex => vertex.col == ConsoleStyle.ApplyAlpha(ConsoleStyle.InsetU32)).Select(vertex => vertex.pos).ToArray();
                Check(background.Length >= 4, "Blank plot background is missing");
                var plot = (min: new float2(background.Min(p => p.X), background.Min(p => p.Y)),
                    max: new float2(background.Max(p => p.X), background.Max(p => p.Y)));
                uint red = ImGui.ColorConvertFloat4ToU32(ConsoleStyle.Danger);
                var ink = DrawLists("/kitn-card-transfer-plot").SelectMany(list => list.VtxBuffer.Span.ToArray()).Where(vertex => vertex.col == red).ToArray();
                Save("wrapped-hierarchy-error-" + locale + ".png", 580, 600, 12, 12);
                Console.WriteLine($"  error ink={ink.Length}, plot={plot.min}/{plot.max}, text={(ink.Length == 0 ? "none" : $"{ink.Min(v => v.pos.X)},{ink.Min(v => v.pos.Y)}..{ink.Max(v => v.pos.X)},{ink.Max(v => v.pos.Y)}")}");
                string errorText = (string)Mod.GetType("KITN.L10n").GetMethod("Get").Invoke(null, new object[] { "transfer.route_hierarchy" });
                Check(ink.Length == errorText.Count(c => !char.IsWhiteSpace(c)) * 4 && ink.All(vertex => vertex.pos.X >= plot.min.X && vertex.pos.X <= plot.max.X)
                    && ink.All(vertex => vertex.pos.Y >= plot.min.Y && vertex.pos.Y <= plot.max.Y),
                    "Wrapped error is missing or clipped at the plot edges");
                Check(ink.Max(vertex => vertex.pos.Y) - ink.Min(vertex => vertex.pos.Y) > ImGui.GetFontSize() * 2,
                    "Long hierarchy message remains a single clipped line");
                Save("wrapped-hierarchy-error-" + locale + ".png", 580, 600, 12, 12);
                Set(planner, "Target", himalia); Set(planner, "UnavailableReason", null);
            }
        }
        finally { controllable.SetValue(null, previous); ((IDisposable)planner).Dispose(); }
        Console.WriteLine("PASS native direct Earth -> Himalia: finite burn, moving SOI boundaries, tilted frames, native propagation and final orbit capture; no Jupiter parking/capture; final encounter required; complete localized errors");

        object SearchLeg(object request, double flightDuration, string name)
        {
            Set(request, "Earliest", 6000.0); Set(request, "Latest", flightDuration);
            Set(request, "MinimumDuration", flightDuration * 0.7); Set(request, "MaximumDuration", flightDuration * 1.5);
            Set(request, "Width", 8); Set(request, "Height", 8); Set(request, "IncludeCapture", true);
            var grid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null, new object[] { request, CancellationToken.None, null });
            Check((int)grid.GetType().GetMethod("Best").Invoke(grid, new object[] { true, double.NegativeInfinity, null }) >= 0,
                "No valid transfer for " + name);
            return Call(grid, "Pick", true, false, double.PositiveInfinity, 0.0, null);
        }
        object Vector(double3 value) => New("LandingVector", value.X, value.Y, value.Z);
        double3 Native(object value) => new double3(Number(value, "X"), Number(value, "Y"), Number(value, "Z"));
        double Number(object value, string field) => (double)value.GetType().GetField(field).GetValue(value);
        double Entry(Orbit coast, Orbit body, double start, double end, double soi)
        {
            double Distance(double t) => (coast.GetStateVectorsAt(new UniverseTime(t)).PositionCci - body.GetStateVectorsAt(new UniverseTime(t)).PositionCci).Length();
            Check(Distance(start) > soi && Distance(end) < soi, "Native route misses an SOI entry");
            for (int i = 0; i < 60; i++) { double mid = (start + end) / 2; if (Distance(mid) > soi) start = mid; else end = mid; }
            return end;
        }
    }
}
