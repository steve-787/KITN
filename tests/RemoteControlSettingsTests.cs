using KITN;

internal static class RemoteControlSettingsTests
{
    public static void Persistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KITN.RemoteControlTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "ui_layout.json");
        var store = new UiLayoutStore(path);
        Check(store.Load() && store.AllowRemoteControl && !store.AllowPhysicsWarp, "New settings have unexpected control defaults");
        File.WriteAllText(path, "{\"version\":1,\"windows\":{},\"settings\":{\"allowRcs\":false}}");
        Check(store.Load() && store.AllowRemoteControl && !store.AllowPhysicsWarp && !store.AllowRcs, "Existing preferences did not migrate");
        store.SetWidget("remote_control", new UiWidgetPreferences { Enabled = true }, 0);
        store.SetAllowRemoteControl(false, 0); store.SetAllowPhysicsWarp(true, 0);
        Check(store.Flush(500) == UiLayoutSaveResult.Saved, "Control settings were not saved");
        var reload = new UiLayoutStore(path);
        Check(reload.Load() && !reload.AllowRemoteControl && reload.AllowPhysicsWarp && reload.Widget("remote_control").Enabled,
            "Control permissions and widget visibility are not independent or did not survive restart");
        reload.SetAllowRemoteControl(true, 1000); reload.SetWidget("remote_control", new UiWidgetPreferences(), 1000);
        Check(reload.Flush(1500) == UiLayoutSaveResult.Saved && store.Load() && store.AllowRemoteControl
            && !store.Widget("remote_control").Enabled, "Hiding the widget disables remote control");
    }

    public static void PausedCountdown()
    {
        var clock = new MissionClock();
        clock.BeginCountdown(10, 100); clock.Advance(104);
        clock.ResumeCountdown(200);
        Check(!clock.Advance(304) && clock.Seconds == 6, "Returning to physics range consumed a suspended countdown");
        Check(!clock.Advance(309.99) && clock.Advance(310) && !clock.Advance(311), "Resumed countdown did not launch exactly once");
        clock.CancelCountdown(); clock.ResumeCountdown(200);
        Check(!clock.Advance(600), "A suspended, cancelled countdown launched");
    }

    public static void PhysicsWarp()
    {
        Check(PhysicsWarpPolicy.Limit(false, 1.0 / 120) == 1, "Disabled physics warp accepts accelerated control");
        Check(PhysicsWarpPolicy.Limit(true, 1.0 / 60) == 4, "Healthy physics cannot run at modest warp");
        Check(PhysicsWarpPolicy.Limit(true, 1.0 / 60, true) == 2, "Final approach allows excessive warp");
        Check(PhysicsWarpPolicy.Limit(true, 0.04) == 2 && PhysicsWarpPolicy.Limit(true, 0.06) == 1,
            "Slow frames do not lower the warp cap");
        foreach (double dt in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
            Check(PhysicsWarpPolicy.Limit(true, dt) == 1, "Invalid timing permits warp");
        foreach (double dt in new[] { 0.005, 0.016, 0.025, 0.035, 0.049, 0.08 })
            foreach (bool precise in new[] { false, true })
            {
                double cap = PhysicsWarpPolicy.Limit(true, dt, precise);
                Check(cap <= 4 && (cap == 1 || cap * dt <= (precise ? 0.05 : 0.1) + 1e-9), "Warp exceeds the control step budget");
            }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
