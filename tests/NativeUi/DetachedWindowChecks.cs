using System;
using System.IO;
using Brutal.ImGuiApi;
using Brutal.Numerics;

internal static partial class Program
{
    // A headless platform backend exercises native viewport creation and migration
    // without opening OS windows or needing a running game renderer.
    private static unsafe void DetachedWindowChecks()
    {
        var io = ImGui.GetIO();
        var platform = ImGui.GetPlatformIO();
        platform.Platform_CreateWindow = (nint)(delegate*<ImGuiViewportPtr, void>)(&CreateTestViewport);
        platform.Platform_DestroyWindow = (nint)(delegate*<ImGuiViewportPtr, void>)(&DestroyTestViewport);
        platform.Platform_ShowWindow = (nint)(delegate*<ImGuiViewportPtr, void>)(&IgnoreTestViewport);
        platform.Platform_SetWindowPos = (nint)(delegate*<ImGuiViewportPtr, float2, void>)(&SetTestViewportPosition);
        platform.Platform_GetWindowPos = Interop.CreateTrampoline(
            (delegate* unmanaged[Cdecl]<ImGuiViewportPtr, float2>)(delegate*<ImGuiViewportPtr, float2>)(&GetTestViewportPosition), "GetWindowPos");
        platform.Platform_SetWindowSize = (nint)(delegate*<ImGuiViewportPtr, float2, void>)(&SetTestViewportSize);
        platform.Platform_GetWindowSize = Interop.CreateTrampoline(
            (delegate* unmanaged[Cdecl]<ImGuiViewportPtr, float2>)(delegate*<ImGuiViewportPtr, float2>)(&GetTestViewportSize), "GetWindowSize");
        platform.Platform_SetWindowFocus = (nint)(delegate*<ImGuiViewportPtr, void>)(&IgnoreTestViewport);
        platform.Platform_GetWindowFocus = (nint)(delegate*<ImGuiViewportPtr, byte>)(&TestViewportFocused);
        platform.Platform_GetWindowMinimized = (nint)(delegate*<ImGuiViewportPtr, byte>)(&TestViewportMinimized);
        platform.Platform_SetWindowTitle = (nint)(delegate*<ImGuiViewportPtr, nint, void>)(&SetTestViewportTitle);
        platform.Monitors.Add(new ImGuiPlatformMonitor { MainPos = float2.Zero, WorkPos = float2.Zero,
            MainSize = new float2(1600, 1000), WorkSize = new float2(1600, 1000), DpiScale = 1 });
        platform.Monitors.Add(new ImGuiPlatformMonitor { MainPos = new float2(1600, 0), WorkPos = new float2(1600, 0),
            MainSize = new float2(1920, 1440), WorkSize = new float2(1920, 1400), DpiScale = 1 });
        platform.Monitors.Add(new ImGuiPlatformMonitor { MainPos = new float2(-1920, 0), WorkPos = new float2(-1920, 0),
            MainSize = new float2(1920, 1440), WorkSize = new float2(1920, 1400), DpiScale = 1 });
        io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports;
        io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        ImGui.GetMainViewport().PlatformHandle = 1;

        string path = Path.Combine(Path.GetTempPath(), "KITN.NativeUi", Guid.NewGuid().ToString("N"), "ui_layout.json");
        Store = New("UiLayoutStore", path); Call(Store, "Load");
        void SetLayouts(object store)
        {
            MainLayout = New("PersistentWindowLayout", store, "autopilot");
            TimerLayout = New("PersistentWindowLayout", store, "mission_time");
            Set(Window, "Settings", store); Set(Timer, "Settings", store);
            Set(Window, "Layout", MainLayout); Set(Timer, "Layout", TimerLayout);
        }
        SetLayouts(Store);
        void Draw() { Frame(); ImGui.UpdatePlatformWindows(); }
        bool Matches(object first, object second) => (bool)Call(first, "Matches", second);
        Draw(); Draw();
        Call(MainLayout, "PlaceAt", new float2(120, 80));
        Call(TimerLayout, "PlaceAt", new float2(1000, 100)); Draw();
        object mainInside = Call(Store, "Find", "autopilot"), timerInside = Call(Store, "Find", "mission_time");
        // Settings changes happen after the main window's layout was captured,
        // while widgets drawn later in the same frame see the new preference.
        ImGui.NewFrame();
        Call(Window, "Draw", true, Profile, Guidance, Flight, Clock, true, false, Landing);
        Call(Store, "SetDetachedWindows", true, Environment.TickCount64);
        Call(Timer, "Draw", true, Clock, Profile, Guidance.CurrentState, false, null, Flight);
        ImGui.Render(); ImGui.UpdatePlatformWindows(); Draw(); Draw();
        Check((bool)MainLayout.GetType().GetProperty("Detached").GetValue(MainLayout), "Detach preference did not select desktop mode");
        Check(platform.Viewports.Count >= 3, "KITN and instruments were not given independent native viewports");
        Call(MainLayout, "PlaceAt", new float2(1900, 120));
        Call(TimerLayout, "PlaceAt", new float2(-1800, 100)); Draw(); Draw();
        ImGui.SetWindowSize("KITN###KITN", new float2(1000, 720), ImGuiCond.Always); Draw();
        object mainOutside = Call(Store, "Find", "autopilot_detached"), timerOutside = Call(Store, "Find", "mission_time_detached");
        Check(Value(mainOutside, "X") == 1900 && Value(mainOutside, "Width") == 1000 && Value(timerOutside, "X") == -1800,
            "Desktop windows were clamped to the game or ignored negative monitor coordinates");
        Check(Matches(mainInside, Call(Store, "Find", "autopilot")) && Matches(timerInside, Call(Store, "Find", "mission_time")),
            "Moving detached windows overwrites their in-game positions");
        // The custom KITN title must keep working in another viewport.
        float2 start = new float2(Value(mainOutside, "X") + 70, Value(mainOutside, "Y") + 15);
        for (int frame = 0; frame < 22; frame++)
        {
            float progress = Math.Clamp((frame - 5) / 8f, 0, 1);
            io.AddMousePosEvent(start.X + progress * 80, start.Y + progress * 60);
            if (frame == 3) io.AddMouseButtonEvent(0, true);
            if (frame == 17) io.AddMouseButtonEvent(0, false);
            Draw();
        }
        object dragged = Call(Store, "Find", "autopilot_detached");
        Check(Math.Abs(Value(dragged, "X") - Value(mainOutside, "X") - 80) < 1
            && Math.Abs(Value(dragged, "Y") - Value(mainOutside, "Y") - 60) < 1, "The custom header cannot drag a detached window");
        mainOutside = dragged;
        Call(Store, "SetDetachedWindows", false, Environment.TickCount64); Draw(); Draw();
        Check(Matches(mainInside, Call(Store, "Find", "autopilot")) && Matches(timerInside, Call(Store, "Find", "mission_time")),
            "Reattaching did not restore the original in-game positions and sizes");
        Call(Store, "SetDetachedWindows", true, Environment.TickCount64); Draw(); Draw();
        Check(Matches(mainOutside, Call(Store, "Find", "autopilot_detached")) && Matches(timerOutside, Call(Store, "Find", "mission_time_detached")),
            "Detaching again did not restore the desktop layout");
        Call(Store, "Flush", Environment.TickCount64, true);
        Store = New("UiLayoutStore", path); Call(Store, "Load"); SetLayouts(Store); Draw(); Draw();
        Check(Matches(mainOutside, Call(Store, "Find", "autopilot_detached")) && Matches(timerOutside, Call(Store, "Find", "mission_time_detached")),
            "Restarting while detached loses the desktop layout");
        io.ConfigFlags &= ~ImGuiConfigFlags.ViewportsEnable; Draw(); Draw();
        Check(!(bool)MainLayout.GetType().GetProperty("Detached").GetValue(MainLayout)
            && Matches(mainInside, Call(Store, "Find", "autopilot")) && (bool)Store.GetType().GetProperty("DetachedWindows").GetValue(Store),
            "Fixed-viewport fallback lost the saved preference or in-game layout");
        Check(Matches(mainOutside, Call(Store, "Find", "autopilot_detached")), "Fallback overwrote the desktop layout");
        io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable; Draw(); Draw();
        platform.Monitors.RemoveAt(2);
        SetLayouts(Store); Draw(); Draw();
        Check(Value(Call(Store, "Find", "mission_time_detached"), "X") >= 0, "A disconnected monitor leaves a widget unreachable");
        Call(Store, "SetDetachedWindows", false, Environment.TickCount64); Draw(); Draw();

        // Both minimized views also need independent positions in each mode.
        object widget = New("MinimizedWindow", Store);
        object activity = Activator.CreateInstance(Mod.GetType("KITN.ModActivity"));
        void WidgetFrame() { ImGui.NewFrame(); Call(widget, "Draw", activity); ImGui.Render(); ImGui.UpdatePlatformWindows(); }
        object WidgetLayout() => widget.GetType().GetProperty("Layout").GetValue(widget);
        Call(widget, "PlaceAt", mainInside); WidgetFrame(); WidgetFrame();
        Call(WidgetLayout(), "PlaceAt", new float2(300, 200)); WidgetFrame();
        object iconInside = Call(Store, "Find", "minimized_icon");
        activity = New("ModActivity", "Controlling", "Test activity", "Details", true, null, null);
        WidgetFrame(); Call(WidgetLayout(), "PlaceAt", new float2(450, 300)); WidgetFrame();
        object activityInside = Call(Store, "Find", "minimized_activity");
        Call(Store, "SetDetachedWindows", true, Environment.TickCount64); WidgetFrame();
        Call(WidgetLayout(), "PlaceAt", new float2(2000, 300)); WidgetFrame();
        object activityOutside = Call(Store, "Find", "minimized_activity_detached");
        activity = Activator.CreateInstance(Mod.GetType("KITN.ModActivity")); WidgetFrame();
        Call(WidgetLayout(), "PlaceAt", new float2(-1600, 300)); WidgetFrame();
        object iconOutside = Call(Store, "Find", "minimized_icon_detached");
        Call(Store, "SetDetachedWindows", false, Environment.TickCount64); WidgetFrame();
        Check(Matches(iconInside, Call(Store, "Find", "minimized_icon")), "Reattaching the minimized icon loses its in-game position");
        activity = New("ModActivity", "Controlling", "Test activity", "Details", true, null, null); WidgetFrame();
        Check(Matches(activityInside, Call(Store, "Find", "minimized_activity")), "Reattaching the activity view loses its in-game position");
        Call(Store, "SetDetachedWindows", true, Environment.TickCount64); WidgetFrame();
        Check(Matches(activityOutside, Call(Store, "Find", "minimized_activity_detached")), "The activity view loses its saved desktop position");
        activity = Activator.CreateInstance(Mod.GetType("KITN.ModActivity")); WidgetFrame();
        Check(Matches(iconOutside, Call(Store, "Find", "minimized_icon_detached")), "The minimized icon loses its saved desktop position");
        Call(Store, "SetDetachedWindows", false, Environment.TickCount64); WidgetFrame();
        Console.WriteLine("PASS detached windows: independent native viewports, three-monitor layouts, both positions and sizes, reattach, restart, fixed-viewport fallback and monitor removal");
        Console.WriteLine("PASS detached custom title dragging and independent minimized icon/activity layouts in both modes");
    }

    private static void CreateTestViewport(ImGuiViewportPtr viewport) { viewport.PlatformHandle = 1; viewport.PlatformUserData = 1; }
    private static void DestroyTestViewport(ImGuiViewportPtr viewport) { viewport.PlatformHandle = 0; viewport.PlatformUserData = 0; }
    private static void IgnoreTestViewport(ImGuiViewportPtr viewport) { }
    private static float2 GetTestViewportPosition(ImGuiViewportPtr viewport) => viewport.Pos;
    private static float2 GetTestViewportSize(ImGuiViewportPtr viewport) => viewport.Size;
    private static void SetTestViewportPosition(ImGuiViewportPtr viewport, float2 position) { viewport.Pos = position; }
    private static void SetTestViewportSize(ImGuiViewportPtr viewport, float2 size) { viewport.Size = size; }
    private static byte TestViewportFocused(ImGuiViewportPtr viewport) => 1;
    private static byte TestViewportMinimized(ImGuiViewportPtr viewport) => 0;
    private static void SetTestViewportTitle(ImGuiViewportPtr viewport, nint title) { }
}
