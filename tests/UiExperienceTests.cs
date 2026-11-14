using KITN;

internal static class UiExperienceTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static UiWindowBounds Bounds(float x, float y, float w, float h, float scale = 1) =>
        new UiWindowBounds { X = x, Y = y, Width = w, Height = h, InterfaceScale = scale };

    public static void ThemePersistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "KITN.UiExperienceTests", Guid.NewGuid().ToString("N"), "ui_layout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // A real version-one position file with no theme must migrate without losing geometry.
        File.WriteAllText(path, "{\"version\":1,\"windows\":{\"autopilot\":{\"x\":45,\"y\":70,\"width\":780,\"height\":650,\"interfaceScale\":1}}}");
        var store = new UiLayoutStore(path);
        Check(store.Load() && !store.GravityAssists, "Existing preferences enable gravity assists unexpectedly");
        Check(store.Load() && store.Theme == UiTheme.Retro && !store.AutoHideInstrumentFrame && store.AllowRcs && !store.IsDirty, "Existing layouts do not default to visible Retro windows and permitted RCS cleanly");
        Check(store.SetTheme(UiTheme.Modern, 100) && !store.SetTheme(UiTheme.Modern, 200), "Theme changes are lost or unchanged selections dirty the file");
        Check(store.SetAutoHideInstrumentFrame(true, 100) && !store.SetAutoHideInstrumentFrame(true, 200), "Auto-hide preference is lost or unchanged values cause repeated writes");
        Check(store.SetAllowRcs(false, 100) && !store.SetAllowRcs(false, 200), "RCS permission cannot be disabled or causes repeated writes");
        Check(store.Flush(599) == UiLayoutSaveResult.Unchanged && store.Flush(600) == UiLayoutSaveResult.Saved, "Theme autosave ignores the debounce");
        var restarted = new UiLayoutStore(path);
        Check(store.SetGravityAssists(true, 600) && !store.SetGravityAssists(true, 601)
            && store.Flush(600, true) == UiLayoutSaveResult.Saved, "Gravity-assist preference fails to save or repeats unchanged writes");
        Check(restarted.Load() && restarted.GravityAssists, "Gravity-assist preference is lost on restart");
        Check(restarted.Load() && restarted.Theme == UiTheme.Modern && restarted.AutoHideInstrumentFrame && !restarted.AllowRcs && restarted.Find("autopilot").X == 45, "Theme, RCS permission, auto-hide preference or window position is lost on restart");
        Check(restarted.SetAutoHideInstrumentFrame(false, 1000), "Auto-hide cannot be disabled");
        Check(restarted.SetAllowRcs(true, 1000), "RCS permission cannot be enabled again");
        Check(restarted.SetTheme(UiTheme.Retro, 1000) && restarted.Flush(1000, true) == UiLayoutSaveResult.Saved, "Theme does not flush when closed");
        var enabled = new UiLayoutStore(path);
        Check(enabled.Load() && enabled.AllowRcs, "Saved RCS permission cannot be loaded");
        byte[] saved = File.ReadAllBytes(path);
        Check(!restarted.SetTheme((UiTheme)99, 2000) && restarted.Flush(3000) == UiLayoutSaveResult.Unchanged
            && File.ReadAllBytes(path).SequenceEqual(saved), "An invalid theme overwrites user settings");
        foreach (string theme in new[] { "\"FutureTheme\"", "42", "null" })
        {
            string invalid = "{\"version\":1,\"windows\":{},\"settings\":{\"theme\":" + theme + "}}";
            File.WriteAllText(path, invalid);
            var damaged = new UiLayoutStore(path);
            Check(!damaged.Load() && !damaged.SetTheme(UiTheme.Modern, 0) && !damaged.SetAutoHideInstrumentFrame(true, 0) && !damaged.SetAllowRcs(false, 0)
                && damaged.Flush(1000, true) == UiLayoutSaveResult.Unchanged
                && File.ReadAllText(path) == invalid, "An unreadable theme file is overwritten");
        }
    }

    public static void Alignment()
    {
        UiWindowBounds viewport = Bounds(0, 0, 1920, 1080);
        var moving = Bounds(765, 415, 400, 240);
        var result = WindowAlignment.Snap(moving, viewport, null, 10);
        Check(result.X == 760 && result.Y == 420 && result.GuideX == 960 && result.GuideY == 540,
            "Window centers do not snap independently to viewport center");
        Check(moving.X == 765 && moving.Y == 415, "Snapping mutates the unsnapped drag source");
        moving = Bounds(200, 200, 400, 240);
        result = WindowAlignment.Snap(moving, viewport, null, 10);
        Check(result.X == 200 && result.Y == 200 && result.GuideX == null && result.GuideY == null, "Distant windows stick to a guide");
        UiWindowBounds peer = Bounds(100, 100, 780, 650);
        moving = Bounds(294, 306, 400, 240);
        result = WindowAlignment.Snap(moving, viewport, peer, 10);
        Check(result.X == 290 && result.Y == 305 && result.GuideX == 490 && result.GuideY == 425, "Peer window centers do not align");
        moving = Bounds(106, 95, 400, 240);
        result = WindowAlignment.Snap(moving, viewport, peer, 10);
        Check(result.X == 100 && result.Y == 100 && result.GuideX == 100 && result.GuideY == 100, "Matching edges cannot align");
        result = WindowAlignment.Snap(Bounds(579, 450, 780, 650), viewport, Bounds(580, 700, 400, 240), 10);
        Check(result.Y <= 430, "Snapping hides the window below the viewport");
        var offset = WindowAlignment.Snap(Bounds(895, 464, 400, 240), Bounds(100, 50, 2000, 1080), null, 20);
        Check(offset.X == 900 && offset.Y == 470, "UI scale or an offset viewport breaks snapping");
        result = WindowAlignment.Snap(moving, viewport, peer, float.NaN);
        Check(result.X == moving.X && result.Y == moving.Y && !result.GuideX.HasValue, "Invalid snapping inputs produce non-finite positions");
    }

    public static void WidgetPersistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "KITN.WidgetTests", Guid.NewGuid().ToString("N"), "ui_layout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"version\":1,\"windows\":{},\"settings\":{\"theme\":\"Modern\",\"autoHideInstrumentFrame\":true}}");
        var store = new UiLayoutStore(path);
        Check(store.Load() && store.MainWindowOpen && !store.MissionTimeOpen && store.Theme == UiTheme.Modern && store.AutoHideInstrumentFrame,
            "Legacy preferences lose their theme/frame settings or default visibility");
        Check(store.SetMissionTimeOpen(true, 100) && store.SetMainWindowOpen(false, 100), "Window states cannot be changed");
        var future = new UiWidgetPreferences { Enabled = true, Theme = UiTheme.Retro, AutoHideFrame = true };
        Check(store.SetWidget("future-widget", future, 100), "Future widget preferences cannot be stored independently");
        future.Enabled = false;
        Check(store.Widget("future-widget").Enabled, "Widget preferences alias the caller's mutable object");
        var copy = store.Widget("mission_time"); copy.Theme = UiTheme.Retro;
        Check(store.Theme == UiTheme.Modern, "Reading widget preferences allows unsaved mutation");
        Check(store.Flush(600) == UiLayoutSaveResult.Saved, "Widget changes did not debounce and save");
        var restart = new UiLayoutStore(path);
        Check(restart.Load() && !restart.MainWindowOpen && restart.MissionTimeOpen && restart.Theme == UiTheme.Modern
            && restart.AutoHideInstrumentFrame && restart.Widget("future-widget").Enabled, "Independent widget states do not survive restart");
        Check(restart.SetMissionTimeOpen(false, 1000) && restart.SetMainWindowOpen(true, 1000)
            && restart.Flush(1000, true) == UiLayoutSaveResult.Saved, "Closing instruments or reopening the main window cannot save immediately");
        Check(restart.Load() && !restart.MissionTimeOpen && restart.MainWindowOpen, "Closed widgets reopen on restart");
        Check(!restart.SetMissionTimeOpen(false, 1100) && !restart.SetMainWindowOpen(true, 1100) && !restart.IsDirty,
            "Unchanged window state dirties preferences on every frame");
        string invalid = "{\"version\":1,\"windows\":{},\"settings\":{\"widgets\":{\"mission_time\":{\"theme\":\"Invalid\"}}}}";
        File.WriteAllText(path, invalid);
        Check(!restart.Load() && !restart.SetMissionTimeOpen(true, 2000) && restart.Flush(3000, true) == UiLayoutSaveResult.Unchanged
            && File.ReadAllText(path) == invalid, "Invalid widget preferences are overwritten");
    }

    public static void FlightSceneVisibility()
    {
        for (int flags = 0; flags < 8; flags++)
        {
            bool loaded = (flags & 1) != 0, editor = (flags & 2) != 0, rocket = (flags & 4) != 0;
            Check(FlightUiVisibility.ShouldShow(loaded, editor, rocket) == (flags == 5),
                "Main menu, editor or a scene without a rocket displays flight UI");
        }
    }
}
