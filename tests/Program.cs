using System.Globalization;
using System.Text;
using KITN;

var tests = new (string Name, Action Run)[]
{
    ("CSV round-trip under German and English cultures", CsvRoundTrip),
    ("Previous sessions stay readable and UTF-8 has no BOM", SessionPreservation),
    ("Rotation keeps three archives and a CSV header in every file", BoundedRotation),
    ("Open tail readers can observe entries while files rotate", LiveReader),
    ("Oversized diagnostic entries stay intact", LargeEntry),
    ("File errors are reported to the logger for recovery", FileError),
    ("Duplicate telemetry columns are rejected", DuplicateColumns),
    ("Gravity turn waits for altitude, speed and upright ascent", TurnStartConditions),
    ("Default LEO pitch profile builds horizontal flight continuously", TurnProfile),
    ("Pitch target uses simulation time and waits for real alignment", TurnRateAndAlignment),
    ("Atmospheric turn limits the angle to the flight path", TurnAerodynamicLimit),
    ("Orbit calculations match known circular and escape states", OrbitElements),
    ("Finite burn timing does not use altitude as an ignition trigger", InsertionTiming),
    ("Orbit completion requires both target apsides", InsertionCompletion),
    ("Apoapsis prediction cuts radial thrust before overshoot", ApoapsisGuard),
    ("Recorded failed flight is diagnosed by orbit calculations", RecordedFlight),
    ("Finite insertion burn converges to a 200 km LEO", SimulatedInsertion),
    ("Recorded rocket estimates support the complete LEO ascent without RCS", FlightSimulation.Run),
    ("Logged cutoff reaches the target periapsis without RCS or powered coast", FlightSimulation.LoggedCoast),
    ("Minimum-throttle pulses preserve the requested average and cutoff", MinimumThrottle),
    ("Logged insertion cancels descent instead of lowering an unsafe orbit", LoggedInsertion),
    ("Insertion continues after AP has moved to the next orbit", PassedApoapsis),
    ("TVC thrust stays available until angular motion settles without RCS", InsertionSettling),
    ("Minimum ignition time preserves average thrust", MinimumPulseTime),
    ("Retrograde trims lower excess apoapsis to both target apsides", SimulatedTrim),
    ("A constant excess AP does not block the preliminary retrograde trim", ConstantApoapsisTrim),
    ("Graph measures surface distance independently of height and origin", FlightDisplayTests.SurfaceDistance),
    ("Graph uses simulation time, preserves stopped traces and resets reverted flights", FlightDisplayTests.TraceLifecycle),
    ("Bounded flight history retains the launch and full distance", FlightDisplayTests.BoundedTrace),
    ("Invalid measurements cannot poison the live graph", FlightDisplayTests.InvalidSamples),
    ("Route preview responds to orbit targets and turn settings", FlightDisplayTests.PreviewProfile),
    ("Route preview respects finite fuel and manual staging", FlightDisplayTests.PreviewFuel),
    ("Adaptive flight plans reach LEO for rebuilt, heavier, lighter and lower-thrust rockets", AdaptivePlanningTests.RocketVariants),
    ("Unflyable rockets report missing thrust, fuel, control or automatic staging", AdaptivePlanningTests.FailureReasons),
    ("The logged safe low orbit coasts to AP while suborbital descent still captures immediately", AdaptivePlanningTests.LoggedSafeCoast),
    ("Adaptive guidance handles phased thrust, delayed TVC, minimum throttle and drag changes", AdaptiveFlightSimulation.RocketControl),
    ("The recorded safe 163 km orbit reaches the target after the next AP without RCS", AdaptiveFlightSimulation.SafeLowOrbit),
    ("Flight presets persist all launch settings and preserve live state", FlightPresetTests.Persistence),
    ("Preset updates replace their name and preserve the previous file", FlightPresetTests.Updates),
    ("Invalid and corrupt preset data cannot overwrite saved flight curves", FlightPresetTests.InvalidData),
    ("Failed preset file writes preserve data and recover on retry", FlightPresetTests.FailedWrite),
    ("Window layouts save automatically, survive restart and preserve independent geometry", UiLayoutTests.AutomaticPersistence),
    ("Restored layouts remain reachable after resolution and UI scale changes", UiLayoutTests.ReachableLayout),
    ("Window autosave preserves corrupt files and recovers from failed writes", UiLayoutTests.FileRecovery),
    ("Instrument themes migrate existing layouts and persist safely", UiExperienceTests.ThemePersistence),
    ("Window snapping aligns centers and edges without sticking or hiding controls", UiExperienceTests.Alignment),
    ("Countdown reaches zero once and mission time starts at actual launch", MissionClockTests.Launch),
    ("Mission clock formats T minus and plus with full hours, minutes and seconds", MissionClockTests.Display),
    ("Cancelled and reset countdowns cannot launch a rocket", MissionClockTests.Cancel),
    ("Mission clock follows simulation pauses, warp and time reverts", MissionClockTests.SimulationTime),
    ("Mission elapsed time survives completed guidance and switching to a booster", MissionClockTests.VehicleFocus),
    ("Return transfers reach their rotating target in prograde and retrograde planes", LandingTests.Transfers),
    ("Atmospheric return targeting corrects the predicted landing footprint", LandingTests.AtmosphericTarget),
    ("Powered descent brakes vertical motion and converges on a rotating landing pad", LandingTests.Descent),
    ("Atmospheric return and finite-fuel landing tolerate engine minimums and attitude delay", LandingTests.FullReturn),
    ("Landing confirmation requires real sustained contact inside the selected pad", LandingTests.Contact),
    ("Landing logs retain a stable schema and distinguish control from mission time", LandingTests.Telemetry),
    ("Empty side boosters confirm depletion independently of the main rocket", BoosterSeparationTests.EmptyBoosters),
    ("Unequal side boosters separate independently and preserve full neighbours", BoosterSeparationTests.IndependentBoosters),
    ("Fuel transfer, transient exhaustion and reset cancel booster confirmation", BoosterSeparationTests.FuelRecovery),
    ("Payloads, future engines and unavailable fuel data cannot trigger booster separation", BoosterSeparationTests.SafeAttachments),
    ("Booster confirmation follows simulation pause and reverts", BoosterSeparationTests.SimulationTime),
    ("UI text and numbers follow the game locale independently of Windows", LocalizationTests.GameCulture),
    ("English and German catalogs preserve every formatted parameter", LocalizationTests.CatalogIntegrity),
    ("Cached status, preview and apsis names follow live language changes", LocalizationTests.CachedMessages),
    ("Game translations override fallbacks while widget IDs remain stable", LocalizationTests.NativeOverridesAndIds)
};
int failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex); }
}
return failures == 0 ? 0 : 1;

static void CsvRoundTrip()
{
    CultureInfo previous = CultureInfo.CurrentCulture;
    try
    {
        foreach (string culture in new[] { "de-DE", "en-US" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var record = new TelemetryRecord();
            record.Section("POSITION");
            record.Add("height_m", 1234.567890123);
            record.Add("missing", double.NaN);
            record.Add("unbound_apoapsis", double.PositiveInfinity);
            record.Section("CONTROL");
            record.Add("flags", "PitchUp, YawRight");
            record.Add("status", "Rakete \"Münze\"\r\nPfad C:\\flight");
            record.Add("vehicle", (string)null!);
            record.Add("active", true);
            record.Add("sample", long.MaxValue);
            string[] names = ParseCsv(record.CsvHeader);
            string[] values = ParseCsv(record.CsvRow);
            Check(names.Length == values.Length && values.Length == 8, "Header and row column counts differ");
            Check(double.Parse(values[0], CultureInfo.InvariantCulture) == 1234.567890123, "Position precision was lost");
            Check(values[0].Contains('.') && !values[0].Contains(','), "Decimal separator depends on the PC language");
            Check(values[1] == "NaN" && values[2] == "Infinity", "Unavailable/unbound numeric values changed");
            Check(values[3] == "PitchUp, YawRight", "Comma-separated thruster flags corrupt CSV columns");
            Check(values[4] == "Rakete \"Münze\"\\r\\nPfad C:\\\\flight", "Quotes/newlines/backslashes did not survive CSV");
            Check(values[5] == "n/a" && values[6] == "true" && values[7] == long.MaxValue.ToString(CultureInfo.InvariantCulture), "Typed fields changed");
            Check(!record.CsvRow.Contains('\n') && !record.CsvRow.Contains('\r'), "One sample spans multiple CSV lines");
            Check(record.Sections.Count == 2 && record.Sections.Count == 2, "Reading sections duplicated the final section");
            Check(record.Sections[0].Value.Contains("height_m=1234.568"), "Text measurements use the wrong locale");
        }
    }
    finally { CultureInfo.CurrentCulture = previous; }
}

static void SessionPreservation()
{
    string directory = TempDirectory();
    string path = Path.Combine(directory, "telemetry.csv");
    File.WriteAllText(path, "previous session\n", new UTF8Encoding(false));
    using (var file = new DiagnosticFile(path, 1024, 3, "sample,height_m"))
    {
        file.Start();
        file.Write("1,42.5");
        Check(File.ReadAllText(path + ".1") == "previous session\n", "Starting the mod overwrote the previous flight");
        Check(File.ReadAllLines(path).SequenceEqual(new[] { "sample,height_m", "1,42.5" }), "Missing CSV header or row");
        byte[] bytes = File.ReadAllBytes(path);
        Check(bytes.Length > 3 && !(bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf), "Unexpected UTF-8 BOM");
    }
    using var next = new DiagnosticFile(path, 1024, 3, "sample,height_m");
    next.Write("2,99");
    Check(File.ReadAllLines(path + ".1")[1] == "1,42.5", "Newest archive is not the previous session");
    Check(File.ReadAllText(path + ".2") == "previous session\n", "Earlier session was lost");
}

static void BoundedRotation()
{
    string path = Path.Combine(TempDirectory(), "telemetry.csv");
    using var file = new DiagnosticFile(path, 38, 3, "sample,status");
    for (int i = 1; i <= 8; i++) file.Write(i + ",Münze");
    string[] paths = Directory.GetFiles(Path.GetDirectoryName(path)!);
    Check(paths.Length == 4 && !File.Exists(path + ".4"), "Archive retention grew beyond three files");
    foreach (string item in paths)
    {
        string[] lines = File.ReadAllLines(item);
        Check(lines[0] == "sample,status", "Rotated CSV lost its schema");
        Check(new FileInfo(item).Length <= 38, "UTF-8 byte size was not respected");
        Check(lines.Skip(1).All(row => ParseCsv(row).Length == 2), "Rotation split or damaged a CSV row");
    }
    Check(File.ReadAllText(path).Contains("8,Münze"), "Latest sample is missing after rotation");
}

static void LiveReader()
{
    string path = Path.Combine(TempDirectory(), "autopilot.log");
    using var file = new DiagnosticFile(path, 12, 3);
    file.Write("first");
    using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete));
    Check(reader.ReadLine() == "first", "Latest log entry was not flushed");
    file.Write("second entry");
    Check(File.ReadAllText(path).Contains("second entry") && File.ReadAllText(path + ".1").Contains("first"),
        "A live reader prevented file rotation");
}

static void LargeEntry()
{
    string path = Path.Combine(TempDirectory(), "autopilot.log");
    string entry = "Exception: " + new string('x', 1024) + "\nStack trace line";
    using var file = new DiagnosticFile(path, 64, 3);
    file.Write(entry);
    file.Write("next event");
    Check(File.ReadAllText(path + ".1") == entry + Environment.NewLine, "An oversized stack trace was truncated");
    Check(File.ReadAllText(path) == "next event" + Environment.NewLine, "Writing after a large exception failed");
}

static void FileError()
{
    string directory = TempDirectory();
    string blockedParent = Path.Combine(directory, "blocked");
    File.WriteAllText(blockedParent, "This is a file, not a directory");
    using var file = new DiagnosticFile(Path.Combine(blockedParent, "autopilot.log"), 1024, 3);
    bool failed = false;
    try { file.Write("message"); } catch (IOException) { failed = true; }
    Check(failed, "The sink silently discarded a file error");
    File.Move(blockedParent, blockedParent + ".saved");
    Directory.CreateDirectory(blockedParent);
    file.Write("recovered");
    Check(File.ReadAllText(Path.Combine(blockedParent, "autopilot.log")).Contains("recovered"), "The sink could not recover");
}

static void DuplicateColumns()
{
    var record = new TelemetryRecord();
    record.Add("sample", 1L);
    bool failed = false;
    try { record.Add("sample", 2L); } catch (InvalidOperationException) { failed = true; }
    Check(failed, "Duplicate columns silently broke the CSV schema");
}

static void TurnStartConditions()
{
    var profile = new LaunchProfile();
    bool Ready(double altitude, double speed, double verticalSpeed, double pitch)
        => GravityTurnController.ReadyToTurn(altitude, profile.GravityTurnStartM, speed,
            profile.GravityTurnMinSpeedMps, verticalSpeed, pitch);
    Check(!Ready(profile.GravityTurnStartM - 1, 250, 240, 90), "Turn begins below the configured height");
    Check(!Ready(profile.GravityTurnStartM, profile.GravityTurnMinSpeedMps - 1, 95, 90), "Turn begins without enough speed");
    Check(!Ready(5000, 200, -1, 90), "Turn begins while descending");
    Check(!Ready(5000, 200, 100, 70), "Turn begins with an unsettled rocket");
    Check(Ready(profile.GravityTurnStartM, 200, 190, 89), "Healthy upright ascent never starts the turn");
    Check(!Ready(double.NaN, 200, 190, 89), "Invalid telemetry starts the turn");
}

static void TurnProfile()
{
    var profile = new LaunchProfile();
    double Pitch(double height) => GravityTurnController.ProfilePitch(height, profile.GravityTurnStartM,
        profile.GravityTurnEndM, profile.GravityTurnShape);
    Check(profile.TargetApoapsisKm == 200 && profile.TargetPeriapsisKm == 200, "LEO is not the default orbit");
    Check(Pitch(0) == 90 && Pitch(profile.GravityTurnStartM) == 90 && Pitch(profile.GravityTurnEndM) == 0, "Turn endpoints are incorrect");
    Check(Pitch(3000) > 89 && Pitch(10000) > 75, "Pitch profile has an abrupt low-altitude turn");
    Check(Pitch(30000) < 60 && Pitch(55000) < 25, "LEO profile never reaches a useful horizontal pitch");
    double previous = 90;
    for (int height = (int)profile.GravityTurnStartM; height <= profile.GravityTurnEndM; height += 500)
    {
        double pitch = Pitch(height);
        Check(pitch <= previous && pitch >= 0, "Pitch profile changes direction or falls below the horizon");
        Check(previous - pitch < 1.5, "Pitch profile has a jump");
        previous = pitch;
    }
    double lower = GravityTurnController.RateLimit(19999.9, profile.MaxPitchRateDegS);
    double upper = GravityTurnController.RateLimit(20000.1, profile.MaxPitchRateDegS);
    Check(Math.Abs(upper - lower) < 0.0001, "Turn rate still doubles abruptly at 20 km");
    Check(GravityTurnController.LimitPitchForApoapsis(60, 40000, 190000, 200000) < 10,
        "Approaching the apoapsis target does not reduce radial climb");
}

static void OrbitElements()
{
    const double mu = 3.986004418e14, radius = 6571000;
    double speed = Math.Sqrt(mu / radius);
    var circular = OrbitInsertion.CalculateOrbit(mu, radius, 0, speed);
    Check(Math.Abs(circular.ApoapsisRadius - radius) < 1 && Math.Abs(circular.PeriapsisRadius - radius) < 1,
        "A circular state has incorrect apsides");
    var escape = OrbitInsertion.CalculateOrbit(mu, radius, 0, Math.Sqrt(2 * mu / radius) + 1);
    Check(!escape.Bound && escape.PeriapsisRadius > 0, "Escape state is mistaken for LEO");
    var radial = OrbitInsertion.CalculateOrbit(mu, radius, 1000, 0);
    Check(radial.ApoapsisRadius > radius && radial.PeriapsisRadius == 0, "A vertical trajectory is mistaken for orbit");
    Check(Math.Abs(OrbitInsertion.TargetTangentialSpeed(mu, radius, radius, radius) - speed) < 1e-6,
        "Target circular speed is incorrect");
}

static void MinimumThrottle()
{
    var actuator = new ThrottleActuator();
    foreach (double requested in new[] { 0.003, 0.01, 0.05, 0.15 })
    {
        actuator.Reset();
        double sum = 0;
        for (int frame = 0; frame < 10000; frame++)
        {
            double command = actuator.Command(requested, 0.2, 0.02, true);
            Check(command == 0 || command >= 0.2, "An engine command falls below its physical throttle range");
            sum += command;
        }
        Check(Math.Abs(sum / 10000 - requested) < 0.0001, "Duty cycle changes the requested average thrust");
    }
    Check(actuator.Command(0.01, 0.2, 0.02, false) == 0, "Unsafe minimum-throttle pulse crosses the apoapsis limit");
    Check(actuator.Command(0, 0.2, 0.02, true) == 0, "Cutoff leaves the engine on");
    Check(actuator.Command(0.01, 0.2, 0, true) == 0, "A paused simulation accumulates a thrust pulse");
}

static void InsertionTiming()
{
    double burn = OrbitInsertion.BurnDuration(1500, 465000, 19293340, 4553.8984);
    Check(burn > 28 && burn < 40, "Finite burn duration does not account for decreasing mass");
    Check(!OrbitInsertion.ReadyToBurn(120000, 79.78, burn, 760), "The logged premature ignition still triggers");
    Check(OrbitInsertion.ReadyToBurn(198000, OrbitInsertion.BurnLead(burn), burn, 30), "Calculated ignition window is missed");
    Check(!OrbitInsertion.ReadyToBurn(80000, 1, burn, -1), "Insertion begins inside the dense atmosphere");
    Check(!OrbitInsertion.ReadyToBurn(200000, 1, double.NaN, 0), "Unavailable propulsion ignites a burn");
    Check(OrbitInsertion.BurnDuration(100, 1000, 10000, 0) == 10, "Constant-mass burn duration is wrong");
}

static void InsertionCompletion()
{
    Check(OrbitInsertion.OrbitAchieved(199500, 200500, 200000, 200000), "Valid LEO does not finish");
    Check(!OrbitInsertion.OrbitAchieved(20000000, 199000, 200000, 200000), "A high apoapsis is accepted as LEO");
    Check(!OrbitInsertion.OrbitAchieved(200000, -6000000, 200000, 200000), "Suborbital periapsis is accepted");
    Check(!OrbitInsertion.OrbitAchieved(double.PositiveInfinity, 200000, 200000, 200000), "Escape is accepted");
}

static void ApoapsisGuard()
{
    const double mu = 3.986004418e14, bodyRadius = 6371000, radius = bodyRadius + 150000;
    double radialSpeed = Math.Sqrt(2 * mu * (1 / radius - 1 / (bodyRadius + 199800)));
    double throttle = OrbitInsertion.AscentThrottle(mu, bodyRadius, radius, radialSpeed, 0, 200000, 40, 90, 0.02, out double predicted);
    Check(throttle < 0.05 && predicted <= 200001, "High radial thrust is not shut down before the target");
    Check(OrbitInsertion.AscentThrottle(mu, bodyRadius, radius, radialSpeed + 50, 0, 200000, 40, 90, 0.02, out _) == 0,
        "An already excessive apoapsis keeps receiving thrust");
    double insertionRadial = Math.Sqrt(2 * mu * (1 / radius - 1 / (bodyRadius + 209800)));
    double guard = OrbitInsertion.GuardInsertionThrottle(mu, bodyRadius, radius, insertionRadial, 300, 200000, 40, 90, 1, 0.02, out _);
    Check(guard < 1 && OrbitInsertion.CalculateOrbit(mu, radius, insertionRadial + guard * 40 * 1.5, 300)
        .ApoapsisRadius - bodyRadius <= OrbitInsertion.ApoapsisCeiling(200000) + 1,
        "The insertion guard permits a radial burn beyond the ceiling");
}

static void RecordedFlight()
{
    string path = @"C:\Users\karst\Documents\My Games\Kitten Space Agency\mods\KITN\kitn_telemetry.csv";
    if (!File.Exists(path)) { Console.WriteLine("SKIP recorded flight: no local telemetry"); return; }
    using var file = new StreamReader(path);
    string[] header = ParseCsv(file.ReadLine()!);
    int Column(string name) => Array.IndexOf(header, name);
    int tested = 0;
    string row;
    while ((row = file.ReadLine()) != null)
    {
        string[] values = ParseCsv(row);
        if (values[Column("state")] != "GravityTurn" && values[Column("state")] != "CoastToApoapsis") continue;
        double Value(string name) => double.Parse(values[Column(name)], CultureInfo.InvariantCulture);
        double r = Value("radius_from_parent_m");
        double px = Value("pos_cci_x_m"), py = Value("pos_cci_y_m"), pz = Value("pos_cci_z_m");
        double vx = Value("velocity_cci_x_m_s"), vy = Value("velocity_cci_y_m_s"), vz = Value("velocity_cci_z_m_s");
        double vr = (px * vx + py * vy + pz * vz) / r;
        double vt = Math.Sqrt(Math.Max(0, vx * vx + vy * vy + vz * vz - vr * vr));
        var elements = OrbitInsertion.CalculateOrbit(Value("gravity_two_body_m_s2") * r * r, r, vr, vt);
        Check(Math.Abs(elements.ApoapsisRadius - 6371000 - Value("apoapsis_altitude_m")) < 2,
            "Orbit calculation does not reproduce the logged apoapsis");
        Check(Math.Abs(elements.PeriapsisRadius - 6371000 - Value("periapsis_altitude_m")) < 2,
            "Orbit calculation does not reproduce the logged periapsis");
        tested++;
    }
    Check(tested > 20, "No useful failed-flight samples were checked");
    Console.WriteLine($"  {tested} recorded ascent/coast orbit states reproduced");
}

static void SimulatedInsertion()
{
    const double mu = 3.986004418e14, bodyRadius = 6371000, targetAltitude = 200000, dt = 0.02;
    foreach (double initialTangentialSpeed in new[] { 5500.0, 7000.0, 7600.0 })
    {
        double radius = bodyRadius + targetAltitude, radial = 0, tangent = initialTangentialSpeed;
        double mass = 465000, thrust = 19293340, massFlow = 4553.8984;
        bool finished = false;
        double highestAp = 0;
        for (int step = 0; step < 30000; step++)
        {
            var orbit = OrbitInsertion.CalculateOrbit(mu, radius, radial, tangent);
            double ap = orbit.ApoapsisRadius - bodyRadius, pe = orbit.PeriapsisRadius - bodyRadius;
            highestAp = Math.Max(highestAp, ap);
            if (OrbitInsertion.OrbitAchieved(ap, pe, targetAltitude, targetAltitude)) { finished = true; break; }
            if (mass < 58000) { thrust = 955522; massFlow = 222.897; }
            double acceleration = thrust / mass;
            var command = OrbitInsertion.Capture(mu, radius, radial, tangent,
                bodyRadius + targetAltitude, bodyRadius + targetAltitude, acceleration);
            double pitch = command.PitchDeg, throttle = command.Throttle;
            throttle = OrbitInsertion.GuardInsertionThrottle(mu, bodyRadius, radius, radial, tangent, targetAltitude,
                acceleration, pitch, throttle, dt, out _);
            double oldRadius = radius;
            radius += radial * dt;
            radial += (tangent * tangent / oldRadius - mu / (oldRadius * oldRadius)
                + acceleration * throttle * Math.Sin(pitch * Math.PI / 180)) * dt;
            tangent += (-radial * tangent / oldRadius + acceleration * throttle * Math.Cos(pitch * Math.PI / 180)) * dt;
            mass -= massFlow * throttle * dt;
            if (radius < bodyRadius + 100000 || !orbit.Bound) break;
        }
        var final = OrbitInsertion.CalculateOrbit(mu, radius, radial, tangent);
        Console.WriteLine($"  insertion start={initialTangentialSpeed:F0} m/s: Ap={(final.ApoapsisRadius-bodyRadius)/1000:F2}, Pe={(final.PeriapsisRadius-bodyRadius)/1000:F2} km, max Ap={highestAp/1000:F2} km");
        Check(finished, "Finite insertion did not converge to the target orbit");
        Check(highestAp <= OrbitInsertion.ApoapsisCeiling(targetAltitude) + 500, "Finite insertion overshot its apoapsis ceiling");
    }
}

static void LoggedInsertion()
{
    const double mu = 3.9860187717e14, planet = 6371000, dt = 0.02;
    string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "insertion-20261112.csv");
    string[] lines = File.ReadAllLines(path);
    foreach (string row in lines.Skip(1))
    {
        double[] data = ParseCsv(row).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        double radius = data[1], vr = data[2], vt = data[3], mass = data[6];
        var elements = OrbitInsertion.CalculateOrbit(mu, radius, vr, vt);
        Check(Math.Abs(elements.ApoapsisRadius - planet - data[9]) < 2
            && Math.Abs(elements.PeriapsisRadius - planet - data[10]) < 2, "Saved flight state is not reproduced");
        if (data[0] < 420) continue;
        var initial = OrbitInsertion.Capture(mu, radius, vr, vt, planet + 200000, planet + 200000, data[7] / mass);
        Check(initial.RadialAcceleration > 0 && initial.TangentialAcceleration > 0,
            "The logged unsafe orbit is still pushed down instead of closing periapsis");
        var actuator = new ThrottleActuator();
        bool captured = false;
        double highestAp = data[9];
        for (int step = 0; step < 20000; step++)
        {
            var orbit = OrbitInsertion.CalculateOrbit(mu, radius, vr, vt);
            double ap = orbit.ApoapsisRadius - planet, pe = orbit.PeriapsisRadius - planet;
            highestAp = Math.Max(highestAp, ap);
            if (pe >= 198000 && ap <= 210000) { captured = true; break; }
            double acceleration = data[7] / mass;
            var command = OrbitInsertion.Capture(mu, radius, vr, vt, planet + 200000, planet + 200000, acceleration);
            double throttle = OrbitInsertion.GuardInsertionThrottle(mu, planet, radius, vr, vt, 200000,
                acceleration, command.PitchDeg, command.Throttle, dt, out _);
            bool pulseSafe = OrbitInsertion.GuardImpulse(mu, planet, radius, vr, vt, 210000, acceleration,
                command.PitchDeg, 0.2, dt, out _) >= 0.2 - 1e-6;
            throttle = actuator.Command(throttle, 0.2, dt, pulseSafe);
            double angle = command.PitchDeg * Math.PI / 180;
            double ar = vt * vt / radius - mu / (radius * radius) + acceleration * throttle * Math.Sin(angle);
            double at = -vr * vt / radius + acceleration * throttle * Math.Cos(angle);
            radius += vr * dt; vr += ar * dt; vt += at * dt; mass -= data[8] * throttle * dt;
            if (radius < planet + 100000 || mass < 12000) break;
        }
        Check(captured, $"Logged T+{data[0]}s state never reaches safe target periapsis");
        Check(highestAp <= 210100, "Corrected logged burn exceeds the unchanged apoapsis ceiling");
    }
}

static void PassedApoapsis()
{
    Check(OrbitInsertion.ReadyToBurn(206825, 5074, 0.8, -6.3), "Passed AP waits for a full revolution while PE is unsafe");
    Check(OrbitInsertion.ReadyToTrim(4900, 1, 1, 0.1, true), "Passed periapsis prevents a trim follow-up burn");
    const double mu = 3.986004418e14, planet = 6371000, radius = 6577578.2;
    double before = OrbitInsertion.CalculateOrbit(mu, radius, -60, 7726).ApoapsisRadius - planet;
    double throttle = OrbitInsertion.GuardInsertionThrottle(mu, planet, radius, -60, 7726, 200000,
        145, 90, 1, 0.02, out double predicted);
    Check(before > 210000 && throttle > 0 && predicted < before,
        "An above-ceiling orbit cannot receive outward thrust that reduces its apoapsis");
    Check(OrbitInsertion.GuardInsertionThrottle(mu, planet, radius, -60, 7726, 200000,
        145, -90, 0.1, 0.02, out _) == 0, "An above-ceiling orbit receives thrust that worsens its apoapsis");
}

static void InsertionSettling()
{
    var gate = new InsertionAttitudeGate();
    Check(!gate.Update(10, 0.1, false, false), "Early coast wastes fuel on nose alignment");
    Check(gate.Update(12.9, 2.07, false, true), "Logged coast misalignment does not get TVC thrust");
    Check(gate.Update(0.5, 2.9, false, true), "Thrust switches off while the aligned rocket is still rotating");
    Check(gate.Update(1.5, 0.2, false, true), "Damping loses its hysteresis near the target");
    Check(!gate.Update(0.5, 0.1, false, true), "Settled TVC burns indefinitely");
    Check(!gate.Update(12.9, 2.07, true, true), "Main engines are used despite sufficient RCS");
    Check(gate.Update(0.5, 0.07, false, true, 0.01), "An inertial coast is started without canceling orbital tracking rotation");
}

static void MinimumPulseTime()
{
    var actuator = new ThrottleActuator();
    double sum = 0;
    int runLength = 0;
    for (int frame = 0; frame < 20000; frame++)
    {
        double command = actuator.Command(0.02, 0.2, 0.02, true, 0.2);
        sum += command;
        if (command > 0) runLength++;
        else if (runLength > 0) { Check(runLength == 10, "Pulse is shorter or longer than the predicted minimum ignition time"); runLength = 0; }
    }
    Check(Math.Abs(sum / 20000 - 0.02) < 0.0001, "Minimum ignition time multiplies the average thrust");
    Check(actuator.Command(0, 0.2, 0.02, true, 0.2) == 0, "Cutoff fails during a pulse");
}

static void SimulatedTrim()
{
    const double mu = 3.9860187717e14, planet = 6371000, dt = 0.02;
    foreach (double initialAp in new[] { 206800.0, 210000.0, 300000.0 })
    {
        double radius = planet + 199500, vr = 0;
        double vt = OrbitInsertion.TargetTangentialSpeed(mu, radius, radius, planet + initialAp);
        double mass = 90000;
        var actuator = new ThrottleActuator();
        bool finished = false;
        for (int step = 0; step < 20000; step++)
        {
            var orbit = OrbitInsertion.CalculateOrbit(mu, radius, vr, vt);
            if (OrbitInsertion.OrbitAchieved(orbit.ApoapsisRadius - planet, orbit.PeriapsisRadius - planet, 200000, 200000))
            { finished = true; break; }
            double acceleration = 12862226 / mass;
            var command = OrbitInsertion.Trim(mu, radius, vr, vt, planet + 200000, planet + 200000, acceleration);
            Check(command.TangentialAcceleration < 0, "Excess apoapsis is raised with another prograde burn");
            double throttle = OrbitInsertion.GuardInsertionThrottle(mu, planet, radius, vr, vt, 200000,
                acceleration, command.PitchDeg, command.Throttle, dt, out _);
            bool safe = OrbitInsertion.GuardImpulse(mu, planet, radius, vr, vt, 210000, acceleration,
                command.PitchDeg, 0.2, dt, out _) >= 0.2 - 1e-6;
            throttle = actuator.Command(throttle, 0.2, dt, safe);
            double pitch = command.PitchDeg * Math.PI / 180;
            double ar = vt * vt / radius - mu / (radius * radius) + acceleration * throttle * Math.Sin(pitch);
            double at = -vr * vt / radius + acceleration * throttle * Math.Cos(pitch);
            radius += vr * dt; vr += ar * dt; vt += at * dt; mass -= 3035.93212890625 * throttle * dt;
        }
        Check(finished, "Periapsis trim fails to bring excess apoapsis into the target orbit");
    }
}

static void ConstantApoapsisTrim()
{
    const double mu = 3.9860187717e14, planet = 6371000, radius = planet + 230000;
    double vt = OrbitInsertion.TargetTangentialSpeed(mu, radius, planet + 220000, radius);
    double throttle = OrbitInsertion.GuardInsertionThrottle(mu, planet, radius, 0, vt,
        200000, 145, 180, 0.01, 0.02, out double predicted);
    Check(throttle == 0.01 && predicted <= 230000.01,
        "Retrograde thrust at a high AP is blocked although it does not increase the AP");
}

static void TurnRateAndAlignment()
{
    var turn = new GravityTurnController();
    turn.Reset(90);
    Check(turn.Update(40, 90, 0, 0, 0.35, 0) == 90, "Target moves while the simulation is paused");
    Check(turn.Update(40, 90, 0, 0, 0.35, -1) == 90, "Target moves on a time reversal");
    double commanded = turn.Update(40, 90, 0, 0, 0.35, 0.1);
    Check(Math.Abs(commanded - 89.965) < 1e-9, "Pitch target does not obey the simulation-time rate limit");
    Check(turn.Update(40, 90, 5, 0, 0.35, 0.1) == commanded, "Target keeps turning while heading is misaligned");
    Check(turn.Update(40, 80, 10, 1, 0.35, 0.1) == commanded, "Target advances after the rocket overshoots");
    Check(turn.Update(40, 90, 0, 52, 0.35, 0.1) == commanded, "Target advances at the observed looping angular speed");
    Check(turn.HoldReason == "waiting_for_rotation_to_settle", "Looping rotation is not visible in diagnostics");
    double afterGap = turn.Update(40, commanded, 0, 0, 0.35, 60);
    Check(commanded - afterGap <= 0.35 * 0.25 + 1e-9, "Long frames introduce an instantaneous pitch jump");
    Check(turn.Update(double.NaN, afterGap, 0, 0, 0.35, 0.1) == afterGap, "Invalid target propagates into flight controls");
}

static void TurnAerodynamicLimit()
{
    Check(GravityTurnController.LimitPitchInAtmosphere(65, 88, 200, 20000) == 85,
        "Commanded pitch deviates too far from the flight path under aerodynamic load");
    Check(GravityTurnController.LimitPitchInAtmosphere(65, 88, 200, 100) == 65,
        "Atmospheric limit prevents turning in thin air");
    Check(GravityTurnController.LimitPitchInAtmosphere(89, 88, 200, 20000) == 89,
        "Atmospheric limit lowers an already safe target");
}

static string TempDirectory()
{
    string directory = Path.Combine(Path.GetTempPath(), "KITN.Diagnostics.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string[] ParseCsv(string line)
{
    var cells = new List<string>();
    var cell = new StringBuilder();
    bool quoted = false;
    for (int i = 0; i < line.Length; i++)
    {
        char current = line[i];
        if (current == '"')
        {
            if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
            else quoted = !quoted;
        }
        else if (current == ',' && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
        else cell.Append(current);
    }
    Check(!quoted, "CSV contains an unclosed quote");
    cells.Add(cell.ToString());
    return cells.ToArray();
}
