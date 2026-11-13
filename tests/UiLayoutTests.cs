using System.Globalization;
using KITN;

internal static class UiLayoutTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string NewPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KITN.UiLayoutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "ui_layout.json");
    }
    private static UiWindowBounds MainBounds() => new UiWindowBounds { X = 125.5f, Y = 80, Width = 900, Height = 700, InterfaceScale = 1 };

    public static void AutomaticPersistence()
    {
        string path = NewPath();
        var store = new UiLayoutStore(path);
        Check(store.Load() && !File.Exists(path), "Loading a missing layout creates a file or fails");
        UiWindowBounds main = MainBounds();
        var timer = new UiWindowBounds { X = 1050, Y = 100, Width = 400, Height = 200, InterfaceScale = 1.25f };
        Check(store.Record("autopilot", main, 100) && store.Record("mission_time", timer, 150), "Window movement was not recorded");
        main.X = 500;
        Check(store.Find("autopilot").X == 125.5f, "Changing caller geometry changes the saved window state");
        Check(store.Flush(649) == UiLayoutSaveResult.Unchanged && !File.Exists(path), "Autosave writes continuously while windows are moving");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check(store.Flush(650) == UiLayoutSaveResult.Saved && !store.IsDirty, "A settled layout was not saved automatically");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var restarted = new UiLayoutStore(path);
        Check(restarted.Load() && restarted.WindowCount == 2 && restarted.Find("autopilot").Matches(MainBounds())
            && restarted.Find("mission_time").Matches(timer), "Independent window positions and sizes did not survive restart");
        byte[] saved = File.ReadAllBytes(path);
        Check(!restarted.Record("autopilot", MainBounds(), 2000) && restarted.Flush(5000) == UiLayoutSaveResult.Unchanged
            && File.ReadAllBytes(path).SequenceEqual(saved) && !File.Exists(path + ".bak"), "An unchanged layout is repeatedly written");
        main = MainBounds(); main.Width = 950;
        Check(restarted.Record("autopilot", main, 6000) && restarted.Flush(6000, true) == UiLayoutSaveResult.Saved,
            "Closing a window or unloading cannot immediately save a final resize");
        Check(File.ReadAllBytes(path + ".bak").SequenceEqual(saved), "Updating window geometry loses the previous layout file");
    }

    public static void ReachableLayout()
    {
        UiWindowBounds main = MainBounds();
        UiWindowBounds scaled = main.Fit(1.5f, 0, 0, 3840, 2160);
        Check(scaled.X == 188.25f && scaled.Y == 120 && scaled.Width == 1350 && scaled.Height == 1050
            && scaled.InterfaceScale == 1.5f && main.Matches(MainBounds()), "Changing KSA UI scale loses or mutates saved geometry");
        main.X = 1800; main.Y = 1000;
        UiWindowBounds small = main.Fit(1, 0, 0, 800, 600);
        Check(small.X == 0 && small.Y == 0 && small.Width == 800 && small.Height == 600,
            "A layout from a larger screen cannot be reached on a smaller resolution");
        main.X = -400; main.Y = -300;
        UiWindowBounds offset = main.Fit(1, 200, 80, 1000, 800);
        Check(offset.X == 200 && offset.Y == 80, "Negative coordinates hide the window outside the game viewport");
        main.InterfaceScale = float.Epsilon;
        Check(main.Fit(2, 0, 0, 1920, 1080).IsValid, "Extreme saved scales produce non-finite UI geometry");
        Check(main.Fit(float.NaN, 0, 0, 1920, 1080) == null && main.Fit(1, 0, 0, 0, 0) == null,
            "An unavailable display or scale creates unusable window bounds");
    }

    public static void FileRecovery()
    {
        string path = NewPath();
        var store = new UiLayoutStore(path);
        Check(store.Load() && store.Record("autopilot", MainBounds(), 0) && store.Flush(500) == UiLayoutSaveResult.Saved,
            "Could not create the original layout");
        byte[] original = File.ReadAllBytes(path);
        UiWindowBounds changed = MainBounds(); changed.X = 250;
        store.Record("autopilot", changed, 1000);
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(store.Flush(1500) == UiLayoutSaveResult.Failed && store.IsDirty && store.LastError != null,
                "A failed layout write discards pending movement");
        Check(File.ReadAllBytes(path).SequenceEqual(original) && Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0,
            "A failed atomic layout write alters existing data or leaves temporary files");
        Check(store.Flush(6499) == UiLayoutSaveResult.Unchanged && store.Flush(6500) == UiLayoutSaveResult.Saved,
            "Autosave cannot recover from a file error or retries on every frame");
        var restarted = new UiLayoutStore(path);
        Check(restarted.Load() && restarted.Find("autopilot").X == 250, "Recovered geometry is lost on restart");
        foreach (string invalid in new[] { "{ corrupt JSON", "{\"version\":99,\"windows\":{}}",
            "{\"version\":1,\"windows\":{\"autopilot\":{\"x\":0,\"y\":0,\"width\":900,\"height\":700}}}" })
        {
            File.WriteAllText(path, invalid);
            var damaged = new UiLayoutStore(path);
            Check(!damaged.Load() && damaged.LastError != null, "An invalid layout is accepted");
            damaged.Record("autopilot", MainBounds(), 0);
            Check(damaged.Flush(500, true) == UiLayoutSaveResult.Unchanged && File.ReadAllText(path) == invalid,
                "Autosave destroys an unreadable or newer layout file");
        }
    }
}
