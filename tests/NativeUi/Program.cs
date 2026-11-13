using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Brutal.ImGuiApi;
using Brutal.ImPlotApi;
using Brutal.Numerics;
using KITN;
using KSA;

internal static class Program
{
    private const string Game = @"K:\Games\Kitten Space Agency";
    private static Assembly Mod;
    private static object Window, Timer, Flight, Clock, Store, MainLayout, TimerLayout;
    private static LaunchProfile Profile;
    private static AscentGuidance Guidance;
    private static string Output;
    private static string Focus;
    private static string MainAction, TimerAction;

    private static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = name.Name == "StarMap.API"
                ? @"C:\Users\karst\.nuget\packages\starmap.api\0.5.4\lib\net10.0\StarMap.API.dll"
                : Path.Combine(Game, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/ui/redesign");
        Directory.CreateDirectory(Output);
        Run();
    }

    private static object New(string type, params object[] args) => Activator.CreateInstance(Mod.GetType("KITN." + type, true), args);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name).Invoke(target, args);
    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property != null) property.SetValue(target, value);
        else target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    }
    private static float Value(object target, string name) => (float)target.GetType().GetProperty(name).GetValue(target);
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }

    private static void Run()
    {
        foreach (Assembly assembly in new[] { typeof(ImGui).Assembly, typeof(ImPlot).Assembly, typeof(ImFontAtlasPtr).Assembly }.Distinct())
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
            {
                string path = Path.Combine(Game, name.EndsWith(".dll") ? name : name + ".dll");
                return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
            });
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.DisplaySize = new float2(1600, 1000); io.DeltaTime = 1f / 60; io.IniFilename = default;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.Fonts.AddFontDefault();
        FontManager.Fonts[GameSettings.GetFontName()] = io.Fonts.AddFontFromFileTTF(Path.Combine(Game, "Content/Fonts/3270-Regular.ttf"), 16);
        FontManager.Fonts["MilSpec33558"] = io.Fonts.AddFontFromFileTTF(Path.Combine(Game, "Content/Fonts/MilSpec33558.ttf"), 48);
        Mod = typeof(LaunchProfile).Assembly;
        Profile = new LaunchProfile { CurrentAltitudeKm = 173.422, CurrentApoapsisKm = 199.8, CurrentPeriapsisKm = -4600, CurrentOrbitalSpeed = 5400 };
        Guidance = new AscentGuidance(Profile, new StagingController(Profile));
        Window = New("AutopilotWindow"); Timer = New("MissionTimeWindow"); Flight = New("FlightDisplay"); Clock = New("MissionClock");
        Set(Flight, "HasVehicle", true); Set(Flight, "VehicleName", "LEO Test Vehicle"); Set(Flight, "AltitudeMeters", 173422.0);
        Call(Clock, "MarkLaunched", 0.0); Call(Clock, "Advance", 237.0);
        Store = New("UiLayoutStore", Path.Combine(Path.GetTempPath(), "KITN.NativeUi", Guid.NewGuid().ToString("N"), "ui_layout.json"));
        Call(Store, "Load");
        MainLayout = New("PersistentWindowLayout", Store, "autopilot"); TimerLayout = New("PersistentWindowLayout", Store, "mission_time");
        Set(MainLayout, "Peer", TimerLayout); Set(TimerLayout, "Peer", MainLayout);
        Set(MainLayout, "Enabled", true); Set(TimerLayout, "Enabled", true);
        Set(Window, "Layout", MainLayout); Set(Timer, "Layout", TimerLayout); Set(Window, "Settings", Store); Set(Timer, "Settings", Store);
        Set(Window, "Presets", New("FlightPresetStore", Path.Combine(Path.GetTempPath(), "KITN.NativeUi", Guid.NewGuid().ToString("N"), "flight_presets.json")));
        Call(Window.GetType().GetProperty("Presets").GetValue(Window), "Load");
        var predicted = Flight.GetType().GetProperty("Prediction").GetValue(Flight);
        Set(predicted, "Distances", new double[] { 0, 10, 50, 120, 300, 600, 900, 1200 });
        Set(predicted, "Altitudes", new double[] { 0, 25, 60, 100, 150, 180, 197, 200 });
        Set(predicted, "MessageKey", "preview.complete");

        foreach (string theme in new[] { "Retro", "Modern" })
        {
            Call(Store, "SetTheme", Enum.Parse(Mod.GetType("KITN.UiTheme"), theme), Environment.TickCount64);
            for (int frame = 0; frame < 3; frame++) Frame(true);
            Save("instruments-" + theme.ToLowerInvariant() + ".png", 416, 288, 820, 12);
            for (int tab = 0; tab < 5; tab++)
            {
                Set(Window, "_tab", tab);
                for (int frame = 0; frame < 3; frame++) Frame(true);
                Check(ImGui.GetDrawData().TotalVtxCount > 1000, "A main panel page did not render");
                if (theme == "Retro") Save("page-" + tab + ".png", 800, 670, 12, 12);
            }
            Set(Window, "_settingsOpen", true);
            for (int frame = 0; frame < 3; frame++) Frame(true);
            Save("settings-" + theme.ToLowerInvariant() + ".png", 800, 670, 12, 12);
            Set(Window, "_settingsOpen", false);
            Console.WriteLine("PASS native all five tabs and general Settings with " + theme + " theme");
        }
        Set(Window, "_settingsOpen", true);
        Call(Store, "SetTheme", Enum.Parse(Mod.GetType("KITN.UiTheme"), "Retro"), Environment.TickCount64);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Click(700, 177, "autopilot");
        for (int frame = 0; frame < 2; frame++) Frame();
        Save("theme-dropdown.png", 800, 670, 12, 12);
        io.AddKeyEvent(ImGuiKey.DownArrow, true); Frame();
        io.AddKeyEvent(ImGuiKey.DownArrow, false); Frame();
        io.AddKeyEvent(ImGuiKey.Enter, true); Frame();
        io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
        Check(Store.GetType().GetProperty("Theme").GetValue(Store).ToString() == "Modern", "Theme dropdown cannot select Modern with keyboard navigation");
        Check(Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "A dropdown theme selection was not persisted");
        Set(Window, "_settingsOpen", false);
        Console.WriteLine("PASS native theme dropdown keyboard selection applies to the visible instruments and persists");
        Call(Clock, "BeginCountdown", 10, 0.0);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("countdown-minimum.png", 336, 264, 820, 12);
        Save("main-minimum.png", 576, 416, 12, 12);
        Check(Value(Call(Store, "Find", "mission_time"), "Width") == 320 && Value(Call(Store, "Find", "mission_time"), "Height") == 248,
            "Minimum instrument geometry was not exercised");
        Check(Click(893, 253, "mission_time") == "cancel_countdown", "Compact instrument countdown cancel cannot be clicked");
        Console.WriteLine("PASS native minimum window sizes with reachable countdown controls");
        Check(Click(514, 111, "autopilot") == null && (bool)Window.GetType().GetField("_settingsOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window),
            "Settings toolbar button cannot open the general page");
        Check(Click(95, 66, "autopilot") == "cancel_countdown", "Countdown cannot be cancelled from general Settings");
        Set(Window, "_settingsOpen", false);
        Console.WriteLine("PASS native toolbar opens Settings and preserves countdown cancellation there");
        Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { "de-DE" });
        Call(Clock, "CancelCountdown");
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("main-german-minimum.png", 576, 416, 12, 12);
        Set(Window, "_settingsOpen", true);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("settings-german-minimum.png", 576, 416, 12, 12);
        Set(Window, "_settingsOpen", false);
        Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { "en-US" });
        Console.WriteLine("PASS native German general Settings and minimum main toolbar layout");
        Call(Clock, "CancelCountdown");
        Set(Flight, "HasVehicle", false);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Set(Flight, "HasVehicle", true);
        Console.WriteLine("PASS native no-vehicle instrument fallback");
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Drag("mission_time", true, -400, 100);
        Drag("autopilot", false, 50, 40);
        Drag("mission_time", false, 40, 40);
        Drag("autopilot", true, 0, 0);
        Console.WriteLine("PASS native both windows drag freely and Alt-drag snaps to screen center");
        io.AddKeyEvent(ImGuiKey.Mod_Alt, true); Frame();
        Check(ImGui.GetDrawData().CmdListsCount >= 3, "Alt did not create a foreground guide layer");
        Save("alignment.png", 1600, 1000);
        io.AddKeyEvent(ImGuiKey.Mod_Alt, false); Frame();
        Console.WriteLine("PASS Alt guide layer appears and disappears on modifier release");
        CompactAndHover();
        Check(ImPlot.GetCurrentContext().IsNull(), "Plot context leaks into the game");
        ((IDisposable)Window).Dispose();
        ImGui.DestroyContext();
    }

    private static void Frame(bool place = false, bool small = false)
    {
        ImGui.NewFrame();
        if (place) ImGui.GetIO().AddMousePosEvent(-1000, -1000);
        if (Focus == "autopilot") ImGui.SetNextWindowFocus();
        if (place)
        {
            ImGui.SetNextWindowPos(new float2(20, 20), ImGuiCond.Always);
            ImGui.SetWindowSize("KITN · LEO Test Vehicle###KITN", small ? new float2(560, 400) : new float2(780, 650), ImGuiCond.Always);
        }
        MainAction = (string)Call(Window, "Draw", true, Profile, Guidance, Flight, Clock, true, false, null);
        if (Focus == "mission_time") ImGui.SetNextWindowFocus();
        if (place)
        {
            ImGui.SetNextWindowPos(new float2(828, 20), ImGuiCond.Always);
            ImGui.SetWindowSize("KITN · FLIGHT INSTRUMENTS###KITNMissionTime", small ? new float2(320, 248) : new float2(400, 272), ImGuiCond.Always);
        }
        TimerAction = (string)Call(Timer, "Draw", true, Clock, Profile, Guidance.CurrentState, false, null, Flight);
        Mod.GetType("KITN.PersistentWindowLayout").GetMethod("DrawGuides").Invoke(null, new[] { MainLayout, TimerLayout });
        ImGui.Render();
    }

    private static void Drag(string id, bool alt, float dx, float dy)
    {
        var io = ImGui.GetIO();
        Focus = id; Frame(); Focus = null;
        object before = Call(Store, "Find", id);
        float x = Value(before, "X"), y = Value(before, "Y"), width = Value(before, "Width"), height = Value(before, "Height");
        if (alt) { dx = 800 - width / 2 - x + 5; dy = 500 - height / 2 - y + 4; }
        float startX = x + 100, startY = y + 10;
        for (int frame = 0; frame < 23; frame++)
        {
            float progress = Math.Clamp((frame - 6) / 8f, 0, 1);
            io.AddMousePosEvent(startX + dx * progress, startY + dy * progress);
            if (frame == 4) { io.AddKeyEvent(ImGuiKey.Mod_Alt, alt); io.AddMouseButtonEvent(0, true); }
            if (frame == 16) io.AddMouseButtonEvent(0, false);
            Frame();
            if (alt && frame >= 8 && frame <= 16) CheckOuterFrame(id);
        }
        object after = Call(Store, "Find", id);
        if (alt)
            Check(Math.Abs(Value(after, "X") + width / 2 - 800) < 1 && Math.Abs(Value(after, "Y") + height / 2 - 500) < 1,
                "Native Alt drag did not snap both axes to viewport center");
        else Check(Math.Abs(Value(after, "X") - x - dx) < 2 && Math.Abs(Value(after, "Y") - y - dy) < 2,
            $"Native free dragging of {id} lost position offsets: before {x}/{y}, after {Value(after, "X")}/{Value(after, "Y")}, delta {dx}/{dy}");
        io.AddKeyEvent(ImGuiKey.Mod_Alt, false); Frame();
    }

    private static void Save(string name, int width, int height, int x = 0, int y = 0) =>
        Raster.Save(ImGui.GetDrawData(), Path.Combine(Output, name), width, height, x, y);

    private static string Click(float x, float y, string id)
    {
        Focus = id; Frame(); Focus = null;
        var io = ImGui.GetIO();
        io.AddMousePosEvent(x, y); Frame();
        io.AddMouseButtonEvent(0, true); Frame();
        io.AddMouseButtonEvent(0, false); Frame();
        return id == "autopilot" ? MainAction : TimerAction;
    }

    private static unsafe ImDrawListPtr WindowDrawList(string id)
    {
        string token = id == "autopilot" ? "###KITN" : "###KITNMissionTime";
        foreach (var list in ImGui.GetDrawData().CmdLists.Span)
        {
            string owner = Marshal.PtrToStringUTF8((IntPtr)list._OwnerName);
            if (owner?.EndsWith(token, StringComparison.Ordinal) == true) return list;
        }
        throw new Exception("No native draw list for " + id);
    }

    private static void CheckOuterFrame(string id)
    {
        object bounds = Call(Store, "Find", id);
        var panel = WindowDrawList(id).VtxBuffer.Span.ToArray().Where(v => v.col == ConsoleStyle.PanelU32).ToArray();
        Check(panel.Length > 0, "Native outer frame has no background geometry");
        float x = Value(bounds, "X"), y = Value(bounds, "Y"), w = Value(bounds, "Width"), h = Value(bounds, "Height");
        Check(Math.Abs(panel.Min(v => v.pos.X) - x) < 1 && Math.Abs(panel.Max(v => v.pos.X) - x - w) < 1
            && Math.Abs(panel.Min(v => v.pos.Y) - y) < 1 && Math.Abs(panel.Max(v => v.pos.Y) - y - h) < 1,
            "Snapping moved the content without moving the rendered border/background on the same frame");
    }

    private static void CompactAndHover()
    {
        var io = ImGui.GetIO();
        for (int frame = 0; frame < 3; frame++) Frame(true);
        ResizeInstruments(-200, -140);
        object resized = Call(Store, "Find", "mission_time");
        Check(Math.Abs(Value(resized, "Width") - 200) < 2 && Math.Abs(Value(resized, "Height") - 132) < 2,
            "Native corner resizing cannot shrink below the old minimum");
        foreach (string theme in new[] { "Retro", "Modern" })
        {
            Call(Store, "SetTheme", Enum.Parse(Mod.GetType("KITN.UiTheme"), theme), Environment.TickCount64);
            ImGui.SetWindowSize("KITN · FLIGHT INSTRUMENTS###KITNMissionTime", new float2(144, 108), ImGuiCond.Always);
            for (int frame = 0; frame < 3; frame++) Frame();
            object bounds = Call(Store, "Find", "mission_time");
            Check(Value(bounds, "Width") == 144 && Value(bounds, "Height") == 108, "Instruments cannot shrink to the new minimum");
            Save("compact-" + theme.ToLowerInvariant() + ".png", 160, 124, (int)Value(bounds, "X") - 8, (int)Value(bounds, "Y") - 8);
        }
        Call(Store, "SetAutoHideInstrumentFrame", true, Environment.TickCount64);
        io.AddMousePosEvent(-1000, -1000); Frame(); Frame();
        object before = Call(Store, "Find", "mission_time");
        Check(!(bool)Timer.GetType().GetProperty("IsChromeVisible").GetValue(Timer), "Instrument frame remains visible without hover");
        var hidden = WindowDrawList("mission_time").VtxBuffer.Span.ToArray();
        Check(hidden.All(v => v.col != ConsoleStyle.PanelU32), "Hidden instrument window still draws its panel background");
        Check(hidden.Count(v => v.col == ConsoleStyle.TextPrimaryU32) > 40, "Auto-hide removed the actual instruments");
        Save("compact-hidden.png", 160, 124, (int)Value(before, "X") - 8, (int)Value(before, "Y") - 8);
        io.AddMousePosEvent(Value(before, "X") + 30, Value(before, "Y") + 40); Frame(); Frame();
        Check((bool)Timer.GetType().GetProperty("IsChromeVisible").GetValue(Timer), "Hover does not restore the instrument frame");
        CheckOuterFrame("mission_time");
        object after = Call(Store, "Find", "mission_time");
        Check(Value(before, "X") == Value(after, "X") && Value(before, "Y") == Value(after, "Y")
            && Value(before, "Width") == Value(after, "Width") && Value(before, "Height") == Value(after, "Height"), "Hover changes the instrument window's geometry");
        Save("compact-hover.png", 160, 124, (int)Value(after, "X") - 8, (int)Value(after, "Y") - 8);
        io.AddMousePosEvent(-1000, -1000); Frame(); Frame();
        Check(!(bool)Timer.GetType().GetProperty("IsChromeVisible").GetValue(Timer), "Instrument frame does not hide again after mouse departure");
        Call(Store, "SetAutoHideInstrumentFrame", false, Environment.TickCount64); Frame();
        CheckOuterFrame("mission_time");
        Call(Clock, "BeginCountdown", 10, 0.0);
        after = Call(Store, "Find", "mission_time");
        Check(Click(Value(after, "X") + 50, Value(after, "Y") + Value(after, "Height") - 13, "mission_time") == "cancel_countdown",
            "Countdown cancellation is unreachable at 144 by 108 pixels");
        Call(Clock, "CancelCountdown");
        Console.WriteLine("PASS compact sizing in both themes and hover-only frame reveal without geometry changes");
    }

    private static void ResizeInstruments(float dx, float dy)
    {
        Focus = "mission_time"; Frame(); Focus = null;
        var io = ImGui.GetIO();
        object before = Call(Store, "Find", "mission_time");
        float x = Value(before, "X") + Value(before, "Width") - 3;
        float y = Value(before, "Y") + Value(before, "Height") - 3;
        for (int frame = 0; frame < 23; frame++)
        {
            float progress = Math.Clamp((frame - 6) / 8f, 0, 1);
            io.AddMousePosEvent(x + dx * progress, y + dy * progress);
            if (frame == 4) io.AddMouseButtonEvent(0, true);
            if (frame == 16) io.AddMouseButtonEvent(0, false);
            Frame();
        }
    }
}

