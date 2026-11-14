using KITN;

internal static class LandingSettingsTests
{
    public static void Persistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "KITN.LandingSettings", Guid.NewGuid().ToString("N"), "layout.json");
        var store = new UiLayoutStore(path);
        if (!store.Load() || !store.Landing.Matches(new LandingSettings())) throw new Exception("Landing defaults do not migrate old settings");
        var settings = new LandingSettings { AutoWarp = false, AdaptiveDrag = false, TouchdownSpeedMps = 0.7f, BrakingMargin = 1.9f,
            HandoverHeightM = 7500, TerminalHorizonSeconds = 18, MaximumTiltDeg = 35, MaximumTurnRateDegS = 5,
            ReservePercent = 55, ReserveExtraMps = 400, DragAreaScale = 1.6f, SearchWindowHours = 48 };
        if (!store.SetLanding(settings, 0) || store.Flush(0, true) != UiLayoutSaveResult.Saved) throw new Exception("Landing settings do not save");
        settings.HandoverHeightM = 15000;
        var restarted = new UiLayoutStore(path);
        if (!restarted.Load() || !restarted.Landing.Matches(store.Landing) || restarted.Landing.HandoverHeightM != 7500)
            throw new Exception("Landing settings are aliased to UI edits or lost after restart");
        byte[] saved = File.ReadAllBytes(path);
        settings.TouchdownSpeedMps = float.NaN;
        if (store.SetLanding(settings, 1000) || store.Flush(1000, true) != UiLayoutSaveResult.Unchanged || !saved.SequenceEqual(File.ReadAllBytes(path)))
            throw new Exception("Invalid landing settings overwrite saved values");
        File.WriteAllText(path, "{\"version\":1,\"windows\":{},\"settings\":{\"landing\":{\"maximumTiltDeg\":95}}}");
        var damaged = new UiLayoutStore(path);
        if (damaged.Load() || damaged.SetLanding(new LandingSettings(), 1000)) throw new Exception("Corrupt landing settings can be overwritten");
    }
}
