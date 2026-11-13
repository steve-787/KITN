using System.Globalization;
using KITN;

internal static class FlightPresetTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string PathForTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KITN.PresetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "flight_presets.json");
    }
    private static LaunchProfile Custom() => new LaunchProfile
    {
        TargetApoapsisKm = 400.25f, TargetPeriapsisKm = 250.5f, GravityTurnStartM = 3500,
        GravityTurnEndM = 75000, GravityTurnMinSpeedMps = 150, LaunchHeadingDeg = 45,
        GravityTurnShape = 1.25f, MaxPitchRateDegS = 0.65f, LaunchCountdownSeconds = 30,
        AutoStaging = false, HotStaging = true, HotStagingFuelPercent = 12.5f, AutoTimeskipToApoapsis = false,
        CurrentAltitudeKm = 123, DebugLoggingEnabled = false, DebugLogIntervalSeconds = 2, WriteTelemetryCsv = false
    };

    public static void Persistence()
    {
        string path = PathForTest();
        var store = new FlightPresetStore(path);
        Check(store.Load() && store.Presets.Count == 0 && !File.Exists(path), "Loading the empty preset list creates or fails on a missing file");
        var flight = Custom();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check(store.Save("  Träger · Münze 🚀  ", flight), "Saving a named custom flight failed");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Check(store.Find("Träger · Münze 🚀")?.Settings.Matches(flight) == true, "Saved curve did not capture every launch setting");
        Check(store.Find("  Tra\u0308ger · Mu\u0308nze 🚀  ")?.Settings.Matches(flight) == true,
            "Equivalent Unicode names and surrounding spaces cannot find the saved curve");
        flight.TargetApoapsisKm = 900;
        var restarted = new FlightPresetStore(path);
        Check(restarted.Load() && restarted.Presets.Count == 1, "Presets did not survive a new store instance");
        var original = Custom();
        FlightPreset preset = restarted.Presets[0];
        Check(preset.Settings.Matches(original), "Changing the live plan mutates a stored preset");
        var applied = new LaunchProfile { CurrentAltitudeKm = 666, StatusMessage = "live state" };
        preset.Settings.Apply(applied);
        Check(preset.Settings.Matches(applied) && applied.CurrentAltitudeKm == 666 && applied.StatusMessage == "live state"
            && applied.DebugLoggingEnabled && applied.WriteTelemetryCsv && applied.DebugLogIntervalSeconds == 0.5f,
            "Loading a preset overwrites telemetry, status or diagnostic preferences");
        Check(File.ReadAllText(path).Contains("400.25") && !File.ReadAllText(path).Contains("currentAltitudeKm"),
            "Preset serialization depends on Windows decimal format or stores live telemetry");
    }

    public static void Updates()
    {
        string path = PathForTest();
        var store = new FlightPresetStore(path);
        Check(store.Load() && store.Save("Booster A", Custom()), "Could not create the initial preset");
        byte[] previous = File.ReadAllBytes(path);
        var modified = Custom(); modified.GravityTurnShape = 1.5f;
        Check(store.Save("booster a", modified) && store.Presets.Count == 1, "Updating the same name makes duplicate presets");
        Check(File.ReadAllBytes(path + ".bak").SequenceEqual(previous), "Preset update does not preserve the preceding file");
        Check(store.Find("Booster A").Settings.GravityTurnShape == 1.5f, "Updated preset did not replace its settings");
        Check(store.Save("Alpha", new LaunchProfile()) && store.Presets[0].Name == "Alpha", "Preset list has no stable alphabetical ordering");
        modified.MaxPitchRateDegS = 0.8f;
        Check(store.Save("booster a", modified), "A later update cannot replace an existing backup");
        Check(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "Preset saves leave temporary files behind");
        var resumed = new FlightPresetStore(path);
        Check(resumed.Load() && resumed.Presets.Count == 2 && resumed.Find("Booster A").Settings.MaxPitchRateDegS == 0.8f,
            "Multiple presets or repeated updates are lost on restart");
    }

    public static void InvalidData()
    {
        string path = PathForTest();
        var store = new FlightPresetStore(path);
        Check(store.Load() && store.Save("Good", Custom()), "Could not create valid preset data");
        byte[] previous = File.ReadAllBytes(path);
        var invalid = Custom(); invalid.GravityTurnEndM = invalid.GravityTurnStartM;
        Check(!store.Save("Bad", invalid) && File.ReadAllBytes(path).SequenceEqual(previous), "Invalid curve overwrites existing presets");
        invalid = Custom(); invalid.TargetApoapsisKm = float.NaN;
        Check(!store.Save("Bad", invalid), "Non-finite orbit values are accepted");
        foreach (string name in new[] { "", " ", "test\nname", "display##hidden", new string('x', 81) })
            Check(!store.Save(name, Custom()) && File.ReadAllBytes(path).SequenceEqual(previous), "Unsafe preset name overwrites the file");
        File.WriteAllText(path, "{ corrupt JSON");
        byte[] corrupt = File.ReadAllBytes(path);
        var restart = new FlightPresetStore(path);
        Check(!restart.Load() && !restart.IsLoaded && restart.LastError != null && !restart.Save("New", Custom())
            && File.ReadAllBytes(path).SequenceEqual(corrupt), "Corrupt saved presets are silently overwritten");
        File.WriteAllBytes(path, previous);
        Check(restart.Load() && restart.Presets.Count == 1, "Reloading a repaired preset file cannot recover");
        File.WriteAllText(path, "{\"version\":99,\"presets\":[]}");
        Check(!restart.Load(), "An unsupported preset format is accepted");
        var unsupported = File.ReadAllBytes(path);
        Check(!restart.Save("New", Custom()) && File.ReadAllBytes(path).SequenceEqual(unsupported), "Newer preset format gets overwritten");
    }

    public static void FailedWrite()
    {
        string path = PathForTest();
        var store = new FlightPresetStore(path);
        Check(store.Load() && store.Save("Existing", Custom()), "Could not create original data");
        byte[] previous = File.ReadAllBytes(path);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(!store.Save("Other", Custom()) && store.Presets.Count == 1, "Failed atomic commit changes the in-memory preset list");
        Check(File.ReadAllBytes(path).SequenceEqual(previous), "Failed preset commit destroys the existing file");
        Check(store.Save("Other", Custom()), "Saving does not recover after a file-sharing failure");
    }
}
