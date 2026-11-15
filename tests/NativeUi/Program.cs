using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Brutal.ImGuiApi;
using Brutal.ImPlotApi;
using Brutal.Numerics;
using KITN;
using KSA;

internal static partial class Program
{
    private const string Game = @"K:\Games\Kitten Space Agency";
    private static Assembly Mod;
    private static object Window, Timer, Flight, Clock, Store, MainLayout, TimerLayout, Landing;
    private static LaunchProfile Profile;
    private static AscentGuidance Guidance;
    private static string Output;
    private static string Focus;
    private static string MainAction, TimerAction;
    private static bool CaptureOnly;

    private static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = name.Name == "StarMap.API"
                ? @"C:\Users\karst\.nuget\packages\starmap.api\0.5.4\lib\net10.0\StarMap.API.dll"
                : Path.Combine(Game, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        if (args.Length > 0 && args[0] == "--inspect-ui-api")
        {
            InspectUiApi();
            return;
        }
        CaptureOnly = args.Length > 0 && args[0] == "--capture-ui";
        Output = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "artifacts/ui/redesign");
        Directory.CreateDirectory(Output);
        Run();
    }

    private static void InspectUiApi()
    {
        foreach (var type in new[] { typeof(UniverseTime), typeof(ConsoleWidgets) })
        {
            Console.WriteLine(type.FullName);
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) Console.WriteLine("  " + member);
        }
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
        Landing = New("LandingController");
        Set(Flight, "HasVehicle", true); Set(Flight, "VehicleName", "LEO Test Vehicle"); Set(Flight, "AltitudeMeters", 173422.0);
        Set(Flight, "AltitudeBodyName", "Earth");
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
        for (int frame = 0; frame < 3; frame++) Frame(true);
        if (Environment.GetCommandLineArgs().Contains("--hohmann-checks"))
        {
            HohmannChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--remote-control-checks"))
        {
            RemoteControlChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--rendezvous-checks"))
        {
            RendezvousManeuverChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--docking-checks"))
        {
            AutomaticDockingChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--docking-overlay-checks"))
        {
            DockingOverlayChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--transfer-route-checks"))
        {
            TransferRouteChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--native-transfer-checks"))
        {
            RecordedTransferChecks(DispatchProxy.Create<IParentBody, PlannerBodyProxy>());
            NativeGravityAssistChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--rcs-checks"))
        {
            RcsChecks(); RendezvousRateHoldChecks(); EarthAtmosphereRcsChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--soi-shortfall-checks"))
        {
            SoiShortfallChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--porkchop-axis-checks"))
        {
            PorkchopAxisChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--header-widget-checks"))
        {
            HeaderWidgetChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--detached-window-checks"))
        {
            DetachedWindowChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--staging-checks"))
        {
            StagingChecks(); ((IDisposable)Window).Dispose(); ImGui.DestroyContext(); return;
        }
        Save("launcher.png", 800, 670, 12, 12);
        if (!CaptureOnly) LauncherNavigationChecks();
        Set(Window, "_homeOpen", false);

        foreach (string theme in new[] { "Retro", "Modern" })
        {
            Call(Store, "SetTheme", Enum.Parse(Mod.GetType("KITN.UiTheme"), theme), Environment.TickCount64);
            for (int frame = 0; frame < 45; frame++) Frame(true);
            Save("instruments-" + theme.ToLowerInvariant() + ".png", 416, 288, 820, 12);
            foreach (int tab in new[] { 0, 1, 2, 3, 6, 4, 5 })
            {
                Set(Window, "_tab", tab);
                for (int frame = 0; frame < 45; frame++) Frame(true);
                Check(ImGui.GetDrawData().TotalVtxCount > 1000, "A main panel page did not render");
                if (theme == "Retro") Save("page-" + tab + ".png", 800, 670, 12, 12);
            }
            Set(Window, "_settingsOpen", true);
            for (int frame = 0; frame < 45; frame++) Frame(true);
            Save("settings-" + theme.ToLowerInvariant() + ".png", 800, 670, 12, 12);
            Set(Window, "_settingsOpen", false);
            Console.WriteLine("PASS native all seven modes and general Settings with " + theme + " theme");
        }
        if (CaptureOnly)
        {
            foreach (string locale in new[] { "en-US", "de-DE" })
            {
                Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
                foreach (int tab in new[] { 0, 1, 2, 3, 6, 4, 5, 7 })
                {
                    Set(Window, "_tab", tab); Set(Window, "_settingsOpen", tab == 7);
                    for (int frame = 0; frame < 60; frame++) Frame(true, true);
                    Save($"page-{tab}-{locale}-minimum.png", 580, 420, 12, 12);
                }
            }
            Set(Window, "_settingsOpen", false);
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { "en-US" });
            PlannerChecks();
            ((IDisposable)Window).Dispose(); ImGui.DestroyContext();
            return;
        }
        Set(Window, "_settingsOpen", true);
        Call(Store, "SetTheme", Enum.Parse(Mod.GetType("KITN.UiTheme"), "Retro"), Environment.TickCount64);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Click(700, 309, "autopilot");
        for (int frame = 0; frame < 2; frame++) Frame();
        Save("theme-dropdown.png", 800, 670, 12, 12);
        io.AddKeyEvent(ImGuiKey.DownArrow, true); Frame();
        io.AddKeyEvent(ImGuiKey.DownArrow, false); Frame();
        io.AddKeyEvent(ImGuiKey.Enter, true); Frame();
        io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
        Check(Store.GetType().GetProperty("Theme").GetValue(Store).ToString() == "Modern", "Theme dropdown cannot select Modern with keyboard navigation");
        Check(Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "A dropdown theme selection was not persisted");
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Click(730, 421, "autopilot");
        Check(!(bool)Store.GetType().GetProperty("AllowRcs").GetValue(Store), "The general settings RCS toggle cannot be switched off");
        Check(Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "The RCS toggle was not saved");
        string settingsPath = (string)Store.GetType().GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Store);
        var reloadedSettings = New("UiLayoutStore", settingsPath); Call(reloadedSettings, "Load");
        Check(!(bool)reloadedSettings.GetType().GetProperty("AllowRcs").GetValue(reloadedSettings), "The RCS toggle does not survive restart");
        Click(730, 421, "autopilot");
        Check((bool)Store.GetType().GetProperty("AllowRcs").GetValue(Store), "The RCS toggle cannot be enabled again");
        Console.WriteLine("PASS general settings RCS toggle is clickable and persists across restart");
        Set(Window, "_settingsOpen", false);
        Console.WriteLine("PASS native theme dropdown keyboard selection applies to the visible instruments and persists");
        WidgetSettingsChecks();
        Call(Clock, "BeginCountdown", 10, 0.0);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("countdown-minimum.png", 336, 264, 820, 12);
        Save("main-minimum.png", 576, 416, 12, 12);
        Check(Value(Call(Store, "Find", "mission_time"), "Width") == 320 && Value(Call(Store, "Find", "mission_time"), "Height") == 248,
            "Minimum instrument geometry was not exercised");
        Check(Click(893, 253, "mission_time") == "cancel_countdown", "Compact instrument countdown cancel cannot be clicked");
        Console.WriteLine("PASS native minimum window sizes with reachable countdown controls");
        Check(Click(547, 70, "autopilot") == null && (bool)Window.GetType().GetField("_settingsOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window),
            "Settings toolbar button cannot open the general page");
        Check(Click(95, 157, "autopilot") == "cancel_countdown", "Countdown cannot be cancelled from general Settings");
        Set(Window, "_settingsOpen", false);
        Console.WriteLine("PASS native toolbar opens Settings and preserves countdown cancellation there");
        Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { "de-DE" });
        Call(Clock, "CancelCountdown");
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("main-german-minimum.png", 576, 416, 12, 12);
        Set(Window, "_settingsOpen", true);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("settings-german-minimum.png", 576, 416, 12, 12);
        ScrollBody(-4);
        Save("settings-german-minimum-scrolled.png", 576, 416, 12, 12);
        Click(490, 197, "autopilot");
        Check(!(bool)Store.GetType().GetProperty("AllowRcs").GetValue(Store), "RCS toggle is unreachable at minimum German window size");
        Click(490, 197, "autopilot");
        ScrollBody(20);
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
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Drag("mission_time", true, 0, 0, true);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Drag("autopilot", true, 0, 0, true);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Call(Store, "SetAutoHideInstrumentFrame", true, Environment.TickCount64);
        Drag("mission_time", true, 0, 0, true, ImGuiKey.LeftAlt);
        Call(Store, "SetAutoHideInstrumentFrame", false, Environment.TickCount64);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Drag("autopilot", true, 0, 0, true, ImGuiKey.RightAlt);
        Console.WriteLine("PASS native both windows snap from title and empty body with left/right Alt and auto-hide");
        io.AddKeyEvent(ImGuiKey.Mod_Alt, true); Frame();
        Check(ImGui.GetDrawData().CmdListsCount >= 3, "Alt did not create a foreground guide layer");
        Save("alignment.png", 1600, 1000);
        io.AddKeyEvent(ImGuiKey.Mod_Alt, false); Frame();
        Console.WriteLine("PASS Alt guide layer appears and disappears on modifier release");
        CompactAndHover();
        RcsChecks();
        RendezvousChecks();
        PlannerChecks();
        TransferControlChecks();
        StagingChecks();
        Check(ImPlot.GetCurrentContext().IsNull(), "Plot context leaks into the game");
        ((IDisposable)Window).Dispose();
        ImGui.DestroyContext();
    }

    private static void LauncherNavigationChecks()
    {
        bool Home() => (bool)Window.GetType().GetField("_homeOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
        bool SettingsOpen() => (bool)Window.GetType().GetField("_settingsOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
        Check(Home(), "The mod opens a page instead of its launcher");
        Click(140, 200, "autopilot");
        Check(!Home() && !SettingsOpen() && (int)Window.GetType().GetField("_tab", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window) == 0,
            "Launcher flight tile cannot open its app page");
        for (int frame = 0; frame < 45; frame++) Frame();
        Save("navigation-flight.png", 800, 670, 12, 12);
        Click(53, 70, "autopilot");
        Check(Home(), "Home icon cannot return to the launcher");
        Click(650, 315, "autopilot");
        Check(!Home() && (int)Window.GetType().GetField("_tab", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window) == 5,
            "Launcher maneuver tile cannot open its app page");
        Save("navigation-transition.png", 800, 670, 12, 12);
        for (int page = 0; page < 7; page++)
        {
            Click(767, 70, "autopilot");
            Check(SettingsOpen(), "Right-aligned settings icon is unreachable");
            for (int frame = 0; frame < 60; frame++) Frame();
            Click(53 + 37 * (page + 1), 70, "autopilot");
            bool diagnostics = (bool)Window.GetType().GetField("_diagnosticsOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
            Check(page == 6 ? diagnostics : !SettingsOpen() && (int)Window.GetType().GetField("_tab", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window) == page,
                "Compact navigation cannot select mode " + page);
        }
        ImGui.GetIO().AddMousePosEvent(53, 70);
        for (int frame = 0; frame < 45; frame++) Frame();
        Check(HasDrawList("Tooltip"), "Icon navigation does not reveal a hover tooltip");
        Save("navigation-tooltip.png", 800, 670, 12, 12);
        ImGui.GetIO().AddMousePosEvent(-1000, -1000);
        Call(Window, "OnVisibilityChanged", false); Frame();
        Check(Home() && !SettingsOpen(), "Reopening the mod does not return to its launcher");
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("launcher-minimum.png", 580, 420, 12, 12);
        Click(400, 320, "autopilot");
        Check(!Home(), "Launcher tiles cannot be selected at the minimum width");
        Set(Window, "_settingsOpen", false); Set(Window, "_diagnosticsOpen", false); Set(Window, "_homeOpen", false); Set(Window, "_tab", 0);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Console.WriteLine("PASS launcher tiles, all compact mode icons, Home, settings, hover tooltips and reopen behavior");
    }

    private static void WidgetSettingsChecks()
    {
        Set(Window, "_settingsOpen", true); Set(Window, "_diagnosticsOpen", false);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Check(!(bool)Store.GetType().GetProperty("MissionTimeOpen").GetValue(Store), "Legacy instrument state defaults unexpectedly to open");
        Click(730, 250, "autopilot");
        Check((bool)Store.GetType().GetProperty("MissionTimeOpen").GetValue(Store), "Widget enable toggle is unreachable");
        Click(730, 340, "autopilot");
        Check((bool)Store.GetType().GetProperty("AutoHideInstrumentFrame").GetValue(Store), "Widget transparency toggle is unreachable");
        Check(Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "Widget settings do not save");
        string path = (string)Store.GetType().GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Store);
        var restart = New("UiLayoutStore", path); Call(restart, "Load");
        Check((bool)restart.GetType().GetProperty("MissionTimeOpen").GetValue(restart) && (bool)restart.GetType().GetProperty("AutoHideInstrumentFrame").GetValue(restart),
            "Clicked widget settings do not survive restart");
        Click(730, 250, "autopilot"); Click(730, 340, "autopilot");
        Check(!(bool)Store.GetType().GetProperty("MissionTimeOpen").GetValue(Store), "Widget cannot be disabled again");
        Set(Window, "_settingsOpen", false);
        Console.WriteLine("PASS widget visibility and transparency are independently clickable and saved across restart");
    }

    private static void RendezvousChecks()
    {
        var controller = New("RendezvousController");
        var station = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        var modStation = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        Set(station, "Id", "Station Alpha"); Set(modStation, "Id", "Mod Station");
        var targets = (System.Collections.IList)controller.GetType().GetField("Targets").GetValue(controller);
        targets.Add(station); targets.Add(modStation); Set(controller, "Target", station);
        DockingPort Port(string name)
        {
            var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part)); Set(part, "DisplayName", name);
            var port = (DockingPort)RuntimeHelpers.GetUninitializedObject(typeof(DockingPort)); Set(port, "Parent", part);
            var connector = (Part.Connector)RuntimeHelpers.GetUninitializedObject(typeof(Part.Connector));
            connector.Parent = part; connector.Asmb2ParentAsmb = doubleQuat.Identity;
            port.Connector = connector; return port;
        }
        var own = Port("Nose port"); var target = Port("Station port");
        ((System.Collections.IList)controller.GetType().GetField("OwnPorts").GetValue(controller)).Add(own);
        ((System.Collections.IList)controller.GetType().GetField("TargetPorts").GetValue(controller)).Add(target);
        Set(controller, "OwnPort", own); Set(controller, "TargetPort", target);
        Set(controller, "CanMatch", true); Set(controller, "CanDock", true); Set(controller, "UnavailableReason", null);
        Set(controller, "Distance", 120.0); Set(controller, "RelativeSpeed", 0.25); Set(controller, "PointingError", 1.5); Set(controller, "LateralError", 0.2);
        Set(Window, "Rendezvous", controller); Set(Window, "_tab", 3);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("rendezvous.png", 800, 670, 12, 12);
        Check(Click(85, 180, "autopilot") == "rendezvous_dock", "Autodock does not reach the physics action queue");
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("rendezvous-minimum.png", 580, 420, 12, 12);
        Check(Click(85, 180, "autopilot") == "rendezvous_dock", "Autodock is unreachable at minimum window size");
        Check(Click(100, 370, "autopilot") == "rendezvous_match", "Match velocity is unreachable at minimum window size");
        Set(controller, "CanDock", false);
        Check(Click(85, 180, "autopilot") == null, "Unavailable docking still starts from the toolbar");
        Set(controller, "CanDock", true);
        Set(controller, "_activeVehicle", station);
        Check(Click(85, 180, "autopilot") == "rendezvous_stop", "Active docking cannot be stopped");
        Set(Window, "_settingsOpen", true);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Check(Click(85, 157, "autopilot") == "rendezvous_stop", "Settings hide the active docking stop control");
        Set(controller, "_activeVehicle", null); Set(Window, "_tab", 0);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("settings-diagnostics-entry.png", 800, 670, 12, 12);
        Click(100, 485, "autopilot");
        Check((bool)Window.GetType().GetField("_diagnosticsOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window), "Settings cannot open the diagnostics page");
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("diagnostics.png", 800, 670, 12, 12);
        Check(Click(767, 70, "autopilot") == null && !(bool)Window.GetType().GetField("_diagnosticsOpen", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window),
            "Diagnostics cannot return to settings");
        Set(Window, "_settingsOpen", false); Set(Window, "_tab", 3);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Click(710, 274, "autopilot");
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.DownArrow, true); Frame(); io.AddKeyEvent(ImGuiKey.DownArrow, false); Frame();
        io.AddKeyEvent(ImGuiKey.Enter, true); Frame(); io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
        Check(ReferenceEquals(controller.GetType().GetProperty("Target").GetValue(controller), modStation), "Rendezvous cannot select a mod-created vehicle");
        Console.WriteLine("PASS Rendezvous page renders real port labels and preserves docking start/stop controls");

        var flagsMethod = controller.GetType().GetMethod("TranslationFlags", BindingFlags.NonPublic | BindingFlags.Static);
        var flags = (ThrusterMapFlags)flagsMethod.Invoke(null, new object[] { new double3(1, -1, 1), new float3(0.01f, 0.01f, 0.01f) });
        Check(flags == (ThrusterMapFlags.TranslateForward | ThrusterMapFlags.TranslateLeft | ThrusterMapFlags.TranslateDown), "Docking translation commands use incorrect control-frame axes");
        var nativeMap = typeof(ThrusterController).GetMethod("ComputeControlMap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var expected = (ThrusterMapFlags)nativeMap.Invoke(null, new object[] { new float3(1, -1, 1), float3.Zero });
        Check(flags == expected, "Docking translations disagree with native thruster mapping");
        Check((ThrusterMapFlags)flagsMethod.Invoke(null, new object[] { new double3(0.001, 0.001, 0.001), new float3(0.01f, 0.01f, 0.01f) }) == ThrusterMapFlags.None,
            "Docking fires translation jets inside the resting deadband");
        Console.WriteLine("PASS docking translation axes agree with the native RCS control map");
        var orientationMethod = controller.GetType().GetMethod("DockingControlTarget", BindingFlags.NonPublic | BindingFlags.Static);
        var ownPortRotation = doubleQuat.CreateFromAxisAngle(double3.UnitY, Math.PI / 2);
        var targetRotation = doubleQuat.CreateFromAxisAngle(double3.UnitZ, 0.8);
        var controlRotation = doubleQuat.CreateFromAxisAngle(double3.UnitX, 0.3);
        var desiredControl = (doubleQuat)orientationMethod.Invoke(null, new object[] { ownPortRotation, targetRotation, controlRotation });
        var desiredBody = doubleQuat.Concatenate(controlRotation.Inverse(), desiredControl);
        var actualPort = doubleQuat.Concatenate(ownPortRotation, desiredBody);
        Check((double3.UnitX.Transform(actualPort) + double3.UnitX.Transform(targetRotation)).Length() < 1e-9
            && (double3.UnitZ.Transform(actualPort) - double3.UnitZ.Transform(targetRotation)).Length() < 1e-9,
            "Side-mounted docking ports do not face the selected target correctly");
        Console.WriteLine("PASS native docking transforms align side-mounted ports and preserve roll orientation");
        var saved = new FlightComputer { RateLimit = 0.01f, AngleDeadband = 0.001f, RollMode = FlightComputerRollMode.Up };
        Set(controller, "_activeVehicle", station); Set(controller, "_savedComputer", saved);
        Set(controller, "_savedRate", 0.2f); Set(controller, "_savedDeadband", 0.02f); Set(controller, "_savedRoll", FlightComputerRollMode.Decoupled);
        Set(controller, "_savedThrust", FlightComputerManualThrustMode.Direct);
        Call(controller, "Update", station, 1.0, false);
        Check(!(bool)controller.GetType().GetProperty("IsActive").GetValue(controller)
            && saved.RateLimit == 0.2f && saved.AngleDeadband == 0.02f && saved.RollMode == FlightComputerRollMode.Decoupled
            && saved.ManualThrustMode == FlightComputerManualThrustMode.Direct, "Disabling RCS leaves docking active or loses saved controls");
        var inputs = (ManualControlInputs)typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(station);
        Check(inputs.ThrusterCommandFlags == ThrusterMapFlags.None && !inputs.EngineOn && inputs.EngineThrottle == 0,
            "Stopping docking leaves thrusters or main engines commanded on");
        var combined = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        var combinedComputer = new FlightComputer { RCSMode = FlightComputerRCSMode.Enabled, RateLimit = 0.01f };
        Set(combined, "FlightComputer", combinedComputer);
        var tree = (PartTree)RuntimeHelpers.GetUninitializedObject(typeof(PartTree)); tree.OwningVehicle = combined;
        Set(own.Parent.FullPart, "Tree", tree);
        Set(controller, "_activeVehicle", station); Set(controller, "_savedComputer", saved); Set(controller, "_savedRcs", FlightComputerRCSMode.Disabled);
        Call(controller, "Stop", true);
        Check(combinedComputer.RCSMode == FlightComputerRCSMode.Disabled && combinedComputer.RateLimit == 0.2f
            && combinedComputer.ManualThrustMode == FlightComputerManualThrustMode.Direct,
            "Docking leaves automatic RCS settings on the newly combined vehicle");
        Console.WriteLine("PASS disabling RCS stops docking, clears thrust commands and restores manual controls");
        Set(Window, "_settingsOpen", false); Set(Window, "_tab", 0); Set(Window, "Rendezvous", null);
    }

    private static void EarthAtmosphereRcsChecks()
    {
        var body = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var proxy = (PlannerBodyProxy)body;
        proxy.AtmosphereRadius = proxy.Radius + 100800;
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        var fc = new FlightComputer { RCSMode = FlightComputerRCSMode.Enabled, AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.None };
        Set(vehicle, "FlightComputer", fc);
        var controller = New("RcsController");
        var allowed = Mod.GetType("KITN.RcsController").GetMethod("Allowed");
        void AtAltitude(double altitude)
        {
            var orbit = Orbit.CreateFromStateCci(body, Universe.GetElapsedTime(), new double3(proxy.Radius + altitude, 0, 0),
                new double3(0, 7500, 0), byte4.Zero);
            Set(vehicle, "FlightPlan", new FlightPlan(orbit, KeyHash.Zero));
        }
        foreach (double altitude in new[] { 60.0, 30000.0, 100799.0, 100800.0 })
        {
            AtAltitude(altitude);
            Check(!(bool)allowed.Invoke(null, new object[] { vehicle, true }), "Earth atmosphere permits RCS at " + altitude);
            Call(controller, "Update", vehicle, true, true, true, true);
            Check(fc.RCSMode == FlightComputerRCSMode.Disabled, "Transfer or docking overrides the Earth atmosphere RCS block");
        }
        AtAltitude(100801);
        Call(controller, "Update", vehicle, true, true, false, false);
        Check(fc.RCSMode == FlightComputerRCSMode.Enabled, "RCS does not return after leaving Earth's atmosphere");
        Call(controller, "Update", vehicle, true, false, true, false);
        Check(fc.RCSMode == FlightComputerRCSMode.Disabled, "Leaving Earth's atmosphere overrides the saved RCS preference");
        AtAltitude(50000);
        Call(controller, "Update", vehicle, true, true, false, false);
        Check(fc.RCSMode == FlightComputerRCSMode.Disabled, "Reentry does not reapply the RCS block");
        proxy.Id = "Mars";
        Call(controller, "Update", vehicle, true, true, false, false);
        Check(fc.RCSMode == FlightComputerRCSMode.Enabled, "Earth-only RCS restriction also blocks another planet");
        Call(controller, "Restore");
        Check(fc.RCSMode == FlightComputerRCSMode.Enabled, "Atmosphere restriction changes the player's saved manual RCS mode");
        var environment = Mod.GetType("KITN.RcsSteering").GetMethod("EnvironmentPermitted");
        Check(!(bool)environment.Invoke(null, new object[] { true, "Custom Earth", "Earth", proxy.Radius + 50000, proxy.AtmosphereRadius }),
            "Earth template aliases bypass the atmospheric RCS restriction");
        Check(!(bool)environment.Invoke(null, new object[] { true, "Earth", "Earth", double.NaN, proxy.AtmosphereRadius }),
            "Unknown Earth position enables RCS");
        Console.WriteLine("PASS native Earth atmosphere RCS block covers ascent, reentry, transfers and docking, restores permission in space and preserves manual settings");
    }

    private static void RcsChecks()
    {
        const double radians = Math.PI / 180;
        var controller = New("RcsController");
        var fc = new FlightComputer
        {
            RCSMode = FlightComputerRCSMode.Enabled, RateLimit = (float)(3 * radians), AngleDeadband = (float)(0.2 * radians),
            RollMode = FlightComputerRollMode.Decoupled, RcsTorqueAuthority = new float3(0, 0.001f, 0.001f)
        };
        float originalRate = fc.RateLimit, originalDeadband = fc.AngleDeadband;
        Call(controller, "Apply", fc, true, false, true, 120.0);
        Check(fc.RCSMode == FlightComputerRCSMode.Disabled, "Disabling RCS still permits a transfer flip");
        Call(controller, "Apply", fc, true, true, false, 30.0);
        Check(fc.RCSMode == FlightComputerRCSMode.Disabled, "Ordinary steering uses RCS for a large turn");
        Call(controller, "Apply", fc, true, true, false, 2.0);
        Check(fc.RCSMode == FlightComputerRCSMode.Enabled && fc.RateLimit <= 0.501 * radians && fc.AngleDeadband >= 0.599 * radians,
            "Fine corrections do not use the reduced rate and resting deadband");
        Call(controller, "Apply", fc, true, true, true, 120.0);
        Check(fc.RCSMode == FlightComputerRCSMode.Enabled && fc.RateLimit > 0.5 * radians && fc.RateLimit <= 1.501 * radians,
            "A badly misaligned transfer cannot turn or retains the fine-correction cap");
        fc.RateLimit = (float)(0.25 * radians);
        Call(controller, "Apply", fc, true, true, true, 120.0);
        Check(fc.RateLimit <= 0.251 * radians, "RCS limits override a lower guidance rotation limit");
        var other = new FlightComputer { RCSMode = FlightComputerRCSMode.Disabled, RateLimit = 0.1f, AngleDeadband = 0.02f };
        Call(controller, "Apply", other, true, false, true, 120.0);
        Check(fc.RCSMode == FlightComputerRCSMode.Enabled && fc.RateLimit == originalRate && fc.AngleDeadband == originalDeadband,
            "Switching vehicles loses the original RCS or attitude settings");
        Call(controller, "Restore");
        Check(other.RCSMode == FlightComputerRCSMode.Disabled && other.RateLimit == 0.1f && other.AngleDeadband == 0.02f,
            "Ending automation does not restore manual flight settings");
        Console.WriteLine("PASS RCS permission covers transfers and corrections and restores vehicle settings");

        var limit = Mod.GetType("KITN.RcsSteering").GetMethod("RateLimit");
        double Rate(double error, double authority) => (double)limit.Invoke(null, new object[] { 3 * radians, error, authority });
        Check(Rate(30, 0.0001) < Rate(30, 0.01), "Heavy vehicles do not receive a lower inertia-dependent rotation limit");
        Check(Rate(double.NaN, 0.001) == 0 && Rate(30, 0) <= 0.501 * radians, "Unavailable attitude/authority produces an unsafe rate");
        var axis = typeof(FlightComputer).GetMethod("ComputeRcsTrackAxis", BindingFlags.NonPublic | BindingFlags.Instance);
        (double impulse, double peak, int coast) Simulate(double inertia, bool economy)
        {
            const double dt = 0.1, torque = 5, minimumImpulse = 0.01;
            float authority = (float)(torque / inertia), bit = (float)(minimumImpulse / inertia);
            float turnaround = Math.Max(bit * bit / authority, 10 * bit);
            var native = new FlightComputer { AngleDeadband = (float)((economy ? 0.6 : 0.2) * radians) };
            double angle = 120 * radians, rate = 0, impulse = 0, peak = 0;
            object state = Enum.ToObject(axis.GetParameters()[7].ParameterType.GetElementType(), 0);
            int coast = 0, run = 0;
            for (int step = 0; step < 30000; step++)
            {
                native.RateLimit = (float)(economy ? Rate(Math.Abs(angle) / radians, authority) : 3 * radians);
                object[] args = { (float)angle, (float)rate, bit, bit, turnaround, (float)inertia, authority, state, 0f, float.PositiveInfinity };
                axis.Invoke(native, args); state = args[7];
                double command = Math.Clamp((float)args[8], -torque * dt, torque * dt);
                impulse += Math.Abs(command); rate += command / inertia; angle += rate * dt; peak = Math.Max(peak, Math.Abs(rate));
                if (Math.Abs(command) < 1e-8 && Math.Abs(rate) > 0.1 * radians) { run++; coast = Math.Max(coast, run); } else run = 0;
                if (Math.Abs(angle) < 0.6 * radians && Math.Abs(rate) < 0.02 * radians) return (impulse, peak, coast);
            }
            throw new Exception($"Native RCS turn did not settle: inertia={inertia}, economy={economy}, angle={angle / radians}, rate={rate / radians}");
        }
        foreach (double inertia in new[] { 1000.0, 10000.0 })
        {
            var previous = Simulate(inertia, false); var economy = Simulate(inertia, true);
            Check(economy.impulse < previous.impulse * 0.9, "The fuel-saving native turn does not reduce total RCS impulse");
            Check(economy.coast >= 20, "The native RCS controller continuously fires instead of coasting");
            Check(economy.peak <= 1.51 * radians, "RCS turn exceeds the planned rotation speed");
            Console.WriteLine($"  RCS inertia={inertia:F0}: impulse {previous.impulse:F1} → {economy.impulse:F1} Nms, coast={economy.coast * 0.1:F1}s");
        }
        Console.WriteLine("PASS native inertia-aware RCS turns coast, brake, settle and use less impulse");
    }

    private static void StagingChecks()
    {
        Part Node(Part parent = null)
        {
            var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part));
            part.Modules = new ModuleList(); part.TreeChildren = new System.Collections.Generic.List<Part>();
            part.TreeParent = parent;
            parent?.TreeChildren.Add(part);
            return part;
        }
        Decoupler Separator(Part owner, Part other, int sequence)
        {
            var connector = (Part.Connector)RuntimeHelpers.GetUninitializedObject(typeof(Part.Connector));
            connector.Parent = owner;
            if (other != null)
            {
                connector.Connection = (Part.Connection)RuntimeHelpers.GetUninitializedObject(typeof(Part.Connection));
                connector.Connection.Connectors[0] = owner; connector.Connection.Connectors[1] = other;
            }
            var decoupler = new Decoupler("staging-test", new Decoupler.TemplateData()) { Parent = owner, Connector = connector, Sequence = sequence };
            owner.Modules.Add(decoupler);
            return decoupler;
        }
        var root = Node(); var lower = Node(root); var tank = Node(lower);
        var axial = Separator(root, lower, 3);
        var monitor = Mod.GetType("KITN.BoosterMonitor");
        var branch = monitor.GetMethod("TryGetDiscardedBranch", BindingFlags.NonPublic | BindingFlags.Static);
        object[] args = { axial, null };
        Check((bool)branch.Invoke(null, args) && ReferenceEquals(args[1], lower), "Axial staging does not isolate the empty lower stage from the controlled upper stage");
        var ownerOnLower = Separator(lower, root, 3);
        args = new object[] { ownerOnLower, null };
        Check((bool)branch.Invoke(null, args) && ReferenceEquals(args[1], lower), "Decoupler orientation changes the discarded branch");
        axial.Connector.Connection = null;
        args = new object[] { axial, null };
        Check(!(bool)branch.Invoke(null, args), "Disconnected staging branch is selected again");
        Console.WriteLine("PASS native axial staging chooses only the detached lower subtree in both connector orientations");

        var spentPart = Node(); var spent = Separator(spentPart, null, 2);
        var nextPart = Node(); var connected = Separator(nextPart, Node(nextPart), 2);
        var sequence = (Sequence)RuntimeHelpers.GetUninitializedObject(typeof(Sequence));
        sequence.Number = 2;
        Set(sequence, "_partsCache", new System.Collections.Generic.List<Part> { spentPart }); Set(sequence, "_partsCached", true);
        var exhausted = monitor.GetMethod("IsSpentSequence", BindingFlags.NonPublic | BindingFlags.Static);
        Check((bool)exhausted.Invoke(null, new object[] { sequence, 2 }), "Spent booster sequence blocks later staging");
        Set(sequence, "_partsCache", new System.Collections.Generic.List<Part> { spentPart, nextPart });
        Check(!(bool)exhausted.Invoke(null, new object[] { sequence, 2 }), "Partly exhausted sequence skips a connected booster");
        Set(connected, "IsEnabled", false);
        Check(!(bool)exhausted.Invoke(null, new object[] { sequence, 2 }), "Disabled connected decoupler is mistaken for a spent separation");
        Set(connected, "IsActive", true);
        Check(!(bool)exhausted.Invoke(null, new object[] { sequence, 2 }), "A queued but still connected separator is mistaken for a completed separation");
        Set(sequence, "_partsCache", new System.Collections.Generic.List<Part> { spentPart });
        var engine = (EngineController)RuntimeHelpers.GetUninitializedObject(typeof(EngineController));
        Set(engine, "Sequence", 2); spentPart.Modules.Add(engine);
        Check(!(bool)exhausted.Invoke(null, new object[] { sequence, 2 }), "Spent decouplers skip an upper-stage ignition in the same sequence");
        Set(engine, "Sequence", 3);
        Check((bool)exhausted.Invoke(null, new object[] { sequence, 2 }), "An action in a later sequence blocks advancement of a spent group");
        Console.WriteLine("PASS native spent groups advance only after all separations complete and preserve engine actions and disabled modules");

        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        GC.SuppressFinalize(vehicle);
        var tree = (PartTree)RuntimeHelpers.GetUninitializedObject(typeof(PartTree));
        GC.SuppressFinalize(tree);
        tree.Modules = new ModuleList();
        Set(tree, "_parts", new System.Collections.Generic.List<Part> { root, lower, tank });
        vehicle.Parts = tree;
        tree.SequenceList = new SequenceList(tree);
        var stateType = typeof(PartTree).GetField("Moles").FieldType;
        var moles = RuntimeHelpers.GetUninitializedObject(stateType);
        var states = new System.Collections.Generic.List<MoleState> { new MoleState { Mass = 1 } };
        Set(moles, "_states", states); Set(tree, "Moles", moles);
        root.Template = (PartTemplate)RuntimeHelpers.GetUninitializedObject(typeof(PartTemplate));
        root.Template.EditorTags = new System.Collections.Generic.List<EditorTag>();
        var updateState = (VehicleUpdateState)RuntimeHelpers.GetUninitializedObject(typeof(VehicleUpdateState));
        GC.SuppressFinalize(updateState);
        var bubble = (PhysicsBubble)RuntimeHelpers.GetUninitializedObject(typeof(PhysicsBubble));
        GC.SuppressFinalize(bubble);
        Set(updateState, "Bubble", bubble); Set(vehicle, "_threadWorkerUpdateState", updateState);

        var fuel = new Liquid { Id = "staging-test-fuel", Hash = KeyHash.Zero, Name = "Staging test fuel", Substance = null, StorageDensity = 1, StorageTemperature = 300 };
        var mole = new Mole(fuel, 1, 1) { Parent = tank };
        Set(mole, "StatesIdx", 0);
        var fuelTank = (Tank)RuntimeHelpers.GetUninitializedObject(typeof(Tank));
        GC.SuppressFinalize(fuelTank);
        fuelTank.Parent = tank; fuelTank.Moles = new System.Collections.Generic.List<Mole> { mole };
        tank.Modules.Add(fuelTank);
        var core = (ThermalCore)RuntimeHelpers.GetUninitializedObject(typeof(ThermalCore));
        GC.SuppressFinalize(core);
        core.Mix = new ReactantMix(new[] { new Reactant { SubstancePhase = fuel, MassFraction = 1 } }, "Test mix");
        var lowerEngine = (EngineController)RuntimeHelpers.GetUninitializedObject(typeof(EngineController));
        GC.SuppressFinalize(lowerEngine);
        lowerEngine.Parent = lower; lowerEngine.Cores = new RocketCore[] { core }; lowerEngine.Sequence = 1;
        Set(lowerEngine, "IsActive", true);
        lower.Modules.Add(lowerEngine); tree.Modules.Add(lowerEngine);

        // Observe ignition before the inline separator is next, then let that engine flame out.
        var staging = new StagingController(new LaunchProfile { AutoStaging = true });
        var localMonitor = typeof(StagingController).GetField("_boosters", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(staging);
        Call(localMonitor, "Update", vehicle, 0.0);
        Set(lowerEngine, "IsActive", false);
        ownerOnLower.Connector.Connection = null;
        axial.Connector.Connection = (Part.Connection)RuntimeHelpers.GetUninitializedObject(typeof(Part.Connection));
        axial.Connector.Connection.Connectors[0] = root; axial.Connector.Connection.Connectors[1] = lower;
        var upperEngine = (EngineController)RuntimeHelpers.GetUninitializedObject(typeof(EngineController));
        GC.SuppressFinalize(upperEngine);
        upperEngine.Parent = root; upperEngine.Sequence = 3;
        root.Modules.Add(upperEngine); tree.Modules.Add(upperEngine);
        var inline = (Sequence)RuntimeHelpers.GetUninitializedObject(typeof(Sequence));
        inline.Number = 3;
        Set(inline, "_partsCache", new System.Collections.Generic.List<Part> { root }); Set(inline, "_partsCached", true);
        Set(tree.SequenceList, "_sequences", new System.Collections.Generic.List<Sequence> { sequence, inline });
        var simStep = typeof(Universe).GetField("_lastSimStep", BindingFlags.NonPublic | BindingFlags.Static);
        object previousStep = simStep.GetValue(null);
        void Coast(double time)
        {
            object step = Activator.CreateInstance(simStep.FieldType);
            Set(step, "NextTime", new UniverseTime(time)); simStep.SetValue(null, step);
            staging.Update(vehicle, 10 + time, 0);
        }
        try
        {
            Coast(0);
            Check(sequence.Activated && tree.SequenceList.GetNextSequenceNumber() == 3,
                "A completely detached booster group blocks the upcoming inline stage");
            Check(staging.BoosterPendingCount == 0 && staging.BoosterStatus == "booster_has_usable_fuel",
                "A fueled inline stage is dropped or its earlier ignition was forgotten");
            states[0] = new MoleState { Mass = 0 };
            Coast(0);
            Check(staging.BoosterPendingCount == 0, "Empty inline stage bypasses depletion confirmation");
            Coast(0.5);
            Check(staging.BoosterPendingCount == 1 && staging.StageEventNumber == 1,
                "Zero commanded thrust blocks previously ignited empty inline-stage separation");
            Check(!inline.Activated && !upperEngine.IsActive && staging.BoosterDropCount == 0,
                "Individual inline separation activates the upper engine or reports success before native application");
            axial.Connector.Connection = null;
            Coast(0.6);
            Check(staging.BoosterDropCount == 1 && !inline.Activated, "Inline separation confirmation loses the upper-stage ignition action");
            // Replay the logged post-separation state: only the inactive upper engine remains,
            // while a ~1.6% average insertion demand produces brief 10% pulses with gaps.
            tree.Modules = new ModuleList(); tree.Modules.Add(upperEngine);
            Set(tree, "_parts", new System.Collections.Generic.List<Part> { root });
            root.TreeChildren.Clear(); lower.TreeParent = null;
            upperEngine.Cores = Array.Empty<RocketCore>();
            Set(vehicle, "FlightComputer", new FlightComputer());
            tree.States = new ModuleStateList();
            tree.RocketNozzles = new RocketNozzle.StateList();
            Coast(2);
            Check(!inline.Activated && !upperEngine.IsActive, "Coasting after MECO prematurely ignites the upper stage");
            void Insertion(double time, float average, float pulse)
            {
                object step = Activator.CreateInstance(simStep.FieldType);
                Set(step, "NextTime", new UniverseTime(time)); simStep.SetValue(null, step);
                staging.Update(vehicle, 10 + time, average, pulse);
            }
            Insertion(2.1, 0.016f, 0);
            Check(!inline.Activated && staging.DebugStatus == "confirming_no_active_engine", "A minimum-throttle pulse gap suppresses upper-stage confirmation");
            Insertion(2.3, 0, 0);
            Check(!inline.Activated && staging.ConfirmationElapsedSeconds == 0, "Real MECO does not cancel pending ignition confirmation");
            Insertion(2.4, 0.016f, 0.1f);
            Insertion(2.6, 0.016f, 0);
            Insertion(2.9, 0.016f, 0);
            int Ignitions() => InputEvents.IActivateInputBuffer.Get().ToArray()
                .Count(input => ReferenceEquals(input.EngineController, upperEngine) && input.ActivationState);
            Check(inline.Activated && Ignitions() == 1 && tree.SequenceList.GetNextSequenceNumber() < 0,
                "Upper-stage ignition remains blocked by low insertion demand or pulse gaps after lower-stage separation");
            Insertion(4, 0.016f, 0.1f);
            Check(Ignitions() == 1, "Pending native upper-stage ignition is queued more than once");

            // A healthy active engine also has zero instantaneous thrust between pulses.
            // Its global state must prevent those gaps from advancing the next sequence.
            var engineStates = tree.States.InitializeHotPathList<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>();
            ref var global = ref engineStates.GetMutableGlobalStateForInitialization();
            global.IsAnyActive = global.IsAnyPropellantAvailable = true;
            inline.Activated = false; Set(tree.SequenceList, "ActiveSequence", 2); staging.Reset();
            for (int i = 0; i < 120; i++) Insertion(5 + i * 0.02, 0.016f, i % 6 == 0 ? 0.1f : 0);
            Check(!inline.Activated && Ignitions() == 1,
                "A fueled active engine is staged away because minimum-throttle pulsing reports zero instantaneous thrust");
        }
        finally { simStep.SetValue(null, previousStep); }
        Console.WriteLine("PASS native empty inline stage remembers earlier ignition and separates during coast without igniting the upper stage");
        Console.WriteLine("PASS logged post-MECO upper-stage ignition survives minimum-throttle pulse gaps while real cutoff cancels confirmation");
        Console.WriteLine("PASS healthy fueled engines do not advance staging during minimum-throttle pulse gaps");
    }

    private static void TransferControlChecks()
    {
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        var fc = new FlightComputer { BurnMode = FlightComputerBurnMode.Auto, RollMode = FlightComputerRollMode.Decoupled };
        Set(vehicle, "FlightComputer", fc);
        var manual = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.NonPublic | BindingFlags.Instance);
        manual.SetValue(vehicle, new ManualControlInputs { EngineOn = true, EngineThrottle = 1, ThrusterCommandFlags = ThrusterMapFlags.TranslateForward });
        var plannerType = Mod.GetType("KITN.ManeuverPlanner");
        plannerType.GetMethod("ClearThrustInputs", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { vehicle });
        var cleared = (ManualControlInputs)manual.GetValue(vehicle);
        Check(!cleared.EngineOn && cleared.EngineThrottle == 0 && cleared.ThrusterCommandFlags == ThrusterMapFlags.None,
            "Transfer does not clear old throttle and RCS translation commands");

        var body = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var orbit = Orbit.CreateFromStateCci(body, new UniverseTime(0), new double3(7e6, 0, 0), new double3(0, 7600, 0), byte4.Zero);
        Set(vehicle, "FlightPlan", new FlightPlan(orbit, KeyHash.Zero));
        Set(vehicle, "Body2Cce", doubleQuat.Identity); Set(vehicle, "BodyRates", double3.Zero);
        var burn = (Burn)RuntimeHelpers.GetUninitializedObject(typeof(Burn));
        GC.SuppressFinalize(burn);
        typeof(Burn).GetField("Vehicle").SetValue(burn, vehicle);
        burn.Time = new UniverseTime(1100); burn.DeltaVVlf = new double3(100, 0, 0); burn.PlannedThrottle = 1;
        typeof(BurnPlan).GetField("_burns", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(fc.BurnPlan, new System.Collections.Generic.List<Burn> { burn });
        fc.Burn = new BurnTarget { ImpulsiveInstant = burn.Time, IgnitionTime = new UniverseTime(1000), BurnDuration = 200,
            BurnBody2Cci = doubleQuat.Identity, DeltaVTargetCci = new float3(100, 0, 0) };
        var planner = New("ManeuverPlanner");
        Set(planner, "_vehicle", vehicle); Set(planner, "_executingVehicle", vehicle);
        Set(planner, "_node", burn); Set(planner, "_departureBurn", fc.Burn);
        Set(planner, "AutoWarp", false);
        var phase = plannerType.GetNestedType("ExecutionPhase", BindingFlags.NonPublic);
        var update = plannerType.GetMethod("UpdateDeparture", BindingFlags.NonPublic | BindingFlags.Instance);
        var controllable = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.NonPublic | BindingFlags.Static);
        bool original = (bool)controllable.GetValue(null);
        controllable.SetValue(null, true);
        try
        {
            Universe.SetSimulationSpeed(1, false);
            manual.SetValue(vehicle, new ManualControlInputs { EngineOn = true, EngineThrottle = 1 });
            plannerType.GetMethod("StartExecution", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new object[] { vehicle });
            Check(fc.BurnMode == FlightComputerBurnMode.Manual, "Starting a transfer enables automatic thrust before alignment");
            update.Invoke(planner, new object[] { vehicle, 900.0, true });
            update.Invoke(planner, new object[] { vehicle, 902.0, true });
            Check(fc.BurnMode == FlightComputerBurnMode.Manual, "Aligned transfer ignites before the native ignition time");
            cleared = (ManualControlInputs)manual.GetValue(vehicle);
            Check(!cleared.EngineOn && cleared.EngineThrottle == 0, "Transfer alignment leaves manual engines on");

            Set(planner, "_executionPhase", Enum.Parse(phase, "Aligning")); Set(planner, "AutoWarp", true);
            Set(planner, "_settledSince", double.NaN);
            fc.RateBit = new float3(0, (float)(0.58 * Math.PI / 180), (float)(0.58 * Math.PI / 180));
            Set(vehicle, "BodyRates", new double3(0.04, 0.5 * Math.PI / 180, 0));
            fc.ConservativeFlipTime = 5;
            update.Invoke(planner, new object[] { vehicle, 903.0, true });
            Check(Universe.IsAutoWarpActive && Math.Abs(Universe.AutoWarpTime.Value.Seconds() - 940) < 0.001
                && fc.BurnMode == FlightComputerBurnMode.Manual, "First alignment does not immediately warp to one minute before ignition with unavoidable RCS pulse rates");

            Set(planner, "_executionPhase", Enum.Parse(phase, "Warping"));
            Universe.SetSimulationSpeed(100, false);
            fc.BurnMode = FlightComputerBurnMode.Auto;
            manual.SetValue(vehicle, new ManualControlInputs { EngineOn = true, EngineThrottle = 1 });
            update.Invoke(planner, new object[] { vehicle, 1000.0, true });
            cleared = (ManualControlInputs)manual.GetValue(vehicle);
            Check(fc.BurnMode == FlightComputerBurnMode.Manual && !cleared.EngineOn && cleared.EngineThrottle == 0,
                "Warping transfer leaves automatic or manual thrust enabled");
            Check(fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None,
                "Transfer keeps adjusting attitude while warping");

            Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            Set(vehicle, "BodyRates", double3.Zero);
            Set(vehicle, "Body2Cce", doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2));
            update.Invoke(planner, new object[] { vehicle, 940.0, true });
            Check(fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.PositiveDv
                && fc.BurnMode == FlightComputerBurnMode.Manual && !Universe.IsAutoWarpActive,
                "Transfer does not resume RCS alignment with engines off a minute before ignition");
            Set(vehicle, "Body2Cce", doubleQuat.Identity);
            fc.RateBit = float3.Zero;
            Set(planner, "_settledSince", 999.0);
            Set(vehicle, "BodyRates", new double3(0, 0.01, 0));
            update.Invoke(planner, new object[] { vehicle, 1000.0, true });
            Check(fc.BurnMode == FlightComputerBurnMode.Manual, "Transfer fires with residual angular momentum");
            Set(vehicle, "BodyRates", double3.Zero);
            Set(vehicle, "Body2Cce", doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2));
            Set(planner, "_settledSince", 999.0);
            update.Invoke(planner, new object[] { vehicle, 1000.0, true });
            Check(fc.BurnMode == FlightComputerBurnMode.Manual, "Transfer fires while facing away from the departure burn");
            Set(vehicle, "Body2Cce", doubleQuat.Identity);
            Set(planner, "_settledSince", 999.0);
            update.Invoke(planner, new object[] { vehicle, 1000.0, true });
            Check(fc.BurnMode == FlightComputerBurnMode.Auto, "Settled transfer cannot ignite at the scheduled instant");

            fc.Burn.StartedAt = new UniverseTime(1000); fc.Burn.DeltaVAccumCci = fc.Burn.DeltaVTargetCci;
            update.Invoke(planner, new object[] { vehicle, 1200.0, true });
            Check(!(bool)plannerType.GetProperty("IsExecuting").GetValue(planner) && fc.BurnMode == FlightComputerBurnMode.Manual
                && (bool)plannerType.GetField("_departureFinished", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(planner),
                "Completed departure remains in automatic thrust mode or loses capture tracking");
        }
        finally { controllable.SetValue(null, original); Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false); }
        Console.WriteLine("PASS native transfer lifecycle clears thrust during alignment/warp, gates ignition and stops after departure");

        const double mu = 3.986004418e14, periapsis = 7e6;
        var atPe = Orbit.CreateFromStateCci(body, new UniverseTime(3600), new double3(periapsis, 0, 0),
            new double3(0, Math.Sqrt(2 * mu / periapsis + 900 * 900), 0), byte4.Zero);
        var inboundState = atPe.GetStateVectorsAt(new UniverseTime(0));
        var inbound = Orbit.CreateFromStateCci(body, new UniverseTime(0), inboundState.PositionCci, inboundState.VelocityCci, byte4.Zero);
        var next = inbound.GetNextPeriapsisTime(new UniverseTime(0));
        Check(next.HasValue && Math.Abs(next.Value.Seconds() - 3600) < 0.01, "Native hyperbolic arrival periapsis time is wrong");
        var state = inbound.GetStateVectorsAt(next.Value);
        object V(double3 v) => New("LandingVector", v.X, v.Y, v.Z);
        object[] captureArgs = { body.Mu, V(state.PositionCci), V(state.VelocityCci), null };
        Check((bool)Mod.GetType("KITN.TransferSafety").GetMethod("Capture").Invoke(null, captureArgs), "Native arrival cannot calculate capture");
        object dv = captureArgs[3];
        var delta = new double3((double)dv.GetType().GetField("X").GetValue(dv), (double)dv.GetType().GetField("Y").GetValue(dv), (double)dv.GetType().GetField("Z").GetValue(dv));
        var vlf = OrbitalTransfers.CciToVlf(inbound, next.Value, delta);
        var captured = Orbit.CreateFromStateCci(body, next.Value, state.PositionCci,
            state.VelocityCci + vlf.Transform(state.GetVlf2ParentCci().Value), byte4.Zero);
        Check(captured.IsBound() && captured.Eccentricity < 1e-7 && Math.Abs(captured.Periapsis - periapsis) < 0.1,
            "Capture node vectors fail to circularize using KSA's orbit and maneuver frames");
        Console.WriteLine("PASS native hyperbolic periapsis and capture vectors produce a bound circular orbit");
        NativeCaptureChecks(vehicle, body, orbit);
    }

    private static void NativeCaptureChecks(Vehicle vehicle, IParentBody earth, Orbit parking)
    {
        var meshes = typeof(ModLibrary).GetField("AllMeshes", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        foreach (string id in new[] { "Sphere", "ConeWithTailMesh", "ConeMesh" })
        {
            var mesh = new MeshReference { Id = id }; mesh.SetHash();
            meshes.GetType().GetMethod("Register").Invoke(meshes, new object[] { mesh });
        }
        var moon = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
        typeof(Celestial).GetField("_meanRadius", BindingFlags.Public | BindingFlags.Instance).SetValue(moon, 1737100.0);
        typeof(Celestial).GetField("_cci2Cce", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(moon, doubleQuat.Identity);
        Set(moon, "Mass", 4.9048695e12 / 6.6743e-11); Set(moon, "SphereOfInfluence", 66e6); Set(moon, "Id", "Capture Moon");
        var targetOrbit = Orbit.CreateFromStateCci(earth, new UniverseTime(0), new double3(384e6, 0, 0), new double3(0, 1019, 0), byte4.Zero);
        moon.Orbit = targetOrbit;
        IParentBody target = moon;
        var atPe = Orbit.CreateFromStateCci(target, new UniverseTime(3600), new double3(1737100 + 11000, 0, 0),
            new double3(0, Math.Sqrt(2 * target.Mu / (1737100 + 11000) + 900 * 900), 0), byte4.Zero);
        var state = atPe.GetStateVectorsAt(new UniverseTime(0));
        var incoming = Orbit.CreateFromStateCci(target, new UniverseTime(0), state.PositionCci, state.VelocityCci, byte4.Zero);
        var plan = new FlightPlan(parking, KeyHash.Zero);
        plan.Patches[0].EndTime = new UniverseTime(1000);
        plan.Patches.Add(new PatchedConic(new UniverseTime(1000), new UniverseTime(10000), PatchTransition.Final, PatchTransition.Final, incoming, KeyHash.Zero));
        Set(vehicle, "FlightPlan", plan);
        var fc = new FlightComputer(); Set(vehicle, "FlightComputer", fc);
        var planner = New("ManeuverPlanner");
        Set(planner, "_captureVehicle", vehicle); Set(planner, "_captureTarget", moon); Set(planner, "_departureFinished", true);
        var update = planner.GetType().GetMethod("UpdateCapture", BindingFlags.NonPublic | BindingFlags.Instance);
        update.Invoke(planner, new object[] { vehicle, 0.0 });
        Check(fc.BurnPlan.BurnCount == 1 && fc.BurnMode == FlightComputerBurnMode.Manual, "Predicted lunar encounter does not create exactly one non-executing capture node");
        Burn capture = fc.BurnPlan.FirstBurn;
        Check(ReferenceEquals(vehicle.Parent, earth) && ReferenceEquals(capture.Patch.PrimaryBody, moon)
            && Math.Abs(capture.Time.Seconds() - 3600) < 0.01, "Capture uses the departure body's frame or waits until SOI entry");
        var nativeDv = fc.Burn.DeltaVTargetCci;
        Check((new double3(nativeDv.X, nativeDv.Y, nativeDv.Z) + incoming.GetStateVectorsAt(capture.Time).VelocityCci).Length()
            < incoming.GetStateVectorsAt(capture.Time).VelocityCci.Length(), "Capture node accelerates instead of braking");
        update.Invoke(planner, new object[] { vehicle, 2.0 });
        Check(fc.BurnPlan.BurnCount == 1, "Capture node is duplicated");
        fc.RemoveBurn(capture);
        update.Invoke(planner, new object[] { vehicle, 4.0 });
        Check(fc.BurnPlan.BurnCount == 0, "Deleted capture node is automatically recreated");
        Console.WriteLine("PASS native capture creates one retrograde node before lunar SOI entry and respects node removal");

        Set(planner, "_captureNode", null);
        var correction = (Burn)RuntimeHelpers.GetUninitializedObject(typeof(Burn)); GC.SuppressFinalize(correction);
        typeof(Burn).GetField("Vehicle").SetValue(correction, vehicle); correction.Time = new UniverseTime(500);
        typeof(BurnPlan).GetField("_burns", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(fc.BurnPlan,
            new System.Collections.Generic.List<Burn> { correction });
        update.Invoke(planner, new object[] { vehicle, 6.0 });
        Check(fc.BurnPlan.BurnCount == 1 && ReferenceEquals(fc.BurnPlan.FirstBurn, correction)
            && (string)planner.GetType().GetProperty("Message").GetValue(planner) == "transfer.capture_existing", "Capture overwrites an existing correction node");
        Console.WriteLine("PASS native capture preserves existing correction nodes");

        Set(vehicle, "FlightComputer", new FlightComputer()); Set(vehicle, "Target", moon);
        var restored = New("ManeuverPlanner");
        update.Invoke(restored, new object[] { vehicle, 10.0 });
        Check(vehicle.BurnPlan.BurnCount == 1 && ReferenceEquals(vehicle.BurnPlan.FirstBurn.Patch.PrimaryBody, moon),
            "Restored/manual flight fails to create capture before target SOI entry");
        Console.WriteLine("PASS native capture recovers a saved or manual encounter before SOI entry");
        ContinuousCaptureChecks(vehicle, earth, moon, incoming);
        NativeRouteValidationChecks(vehicle, earth, moon, incoming, parking);
        RecordedTransferChecks(earth);
        NativeGravityAssistChecks();
    }

    private static void RecordedTransferChecks(IParentBody earth)
    {
        const double now = 5346.943808178;
        var parking = Orbit.CreateFromStateCci(earth, new UniverseTime(now),
            new double3(-1784936.5264293605, -5682592.366657389, 2775400.6390009737),
            new double3(7494.60375282298, -1913.933900767815, 902.4384571692448), byte4.Zero);
        const double radians = Math.PI / 180;
        doubleQuat eclipticToCci = Celestial.CreateOrb2Cci(-0.363633 * radians, 23.441522 * radians);
        var orientation = doubleQuat.Concatenate(Orbit.CreateOrb2ParentCci(343.9586928342719 * radians, 5.027765739305488 * radians, 90.95632163367938 * radians), eclipticToCci);
        Orbit.GetPlaneAnglesFromOrb2Reference(orientation, out double node, out double inclination, out double argument);
        const double axis = 380863334.9807188, eccentricity = 0.06734146418596218;
        var moon = Orbit.Create(earth, new OrbitData(new UniverseTime(442332.8562855744), earth.Mass,
            axis * (1 - eccentricity), eccentricity, inclination, node, argument), byte4.Zero);
        object Vector(double3 value) => New("LandingVector", value.X, value.Y, value.Z);
        object Snapshot(Orbit orbit)
        {
            var state = orbit.GetStateVectorsAt(new UniverseTime(now));
            return New("TransferOrbit", orbit.Mu, now, Vector(state.PositionCci), Vector(state.VelocityCci));
        }
        var request = New("TransferRequest");
        Set(request, "Parking", Snapshot(parking)); Set(request, "Target", Snapshot(moon));
        Set(request, "Now", now); Set(request, "CentralMu", earth.Mu); Set(request, "TargetMu", 4.9048695e12);
        Set(request, "TargetRadius", 1737100.0); Set(request, "PeriapsisAltitude", 100000.0);
        Set(request, "TargetSoi", 66e6);
        Set(request, "DepartureSafetyRadius", earth.GetNearSurfaceRadius()); Set(request, "CentralSoi", earth.SphereOfInfluence);
        Set(request, "IncludeCapture", true);
        double transferAxis = (parking.SemiMajorAxis + moon.SemiMajorAxis) / 2;
        double hohmann = Math.PI * Math.Sqrt(transferAxis * transferAxis * transferAxis / earth.Mu);
        Set(request, "Earliest", now + 300); Set(request, "Latest", now + 300 + parking.Period * 2);
        Set(request, "MinimumDuration", hohmann * 0.25); Set(request, "MaximumDuration", hohmann * 2);
        var grid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null, new[] { request, (object)default(System.Threading.CancellationToken), null });
        var originalIndex = (int)grid.GetType().GetMethod("Best").Invoke(grid, new object[] { true, double.NegativeInfinity, null });
        var original = ((Array)grid.GetType().GetField("Cells").GetValue(grid)).GetValue(originalIndex);
        var phases = new[] { new FiniteBurnPhase(955522.3125, 222.89703369140625, 52813.765625, 49474.73046875 / 222.89703369140625) };
        Mod.GetType("KITN.TransferBurnRefiner").GetMethod("Attach").Invoke(null, new object[] { request, phases });
        grid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null, new[] { request, (object)default(System.Threading.CancellationToken), null });
        var correctedIndex = (int)grid.GetType().GetMethod("Best").Invoke(grid, new object[] { true, double.NegativeInfinity, null });
        Check(correctedIndex >= 0, "Finite burn refinement eliminates all recorded parking orbit routes");
        var corrected = ((Array)grid.GetType().GetField("Cells").GetValue(grid)).GetValue(correctedIndex);
        Set(request, "Oberth", true);
        var finiteOverviewRequest = Call(request, "CopyForPorkchop");
        Mod.GetType("KITN.TransferBurnRefiner").GetMethod("Attach").Invoke(null, new object[] { finiteOverviewRequest, phases });
        var periapsisGrid = Mod.GetType("KITN.TransferMath").GetMethod("SearchWithPorkchop").Invoke(null,
            new[] { request, finiteOverviewRequest, (object)default(System.Threading.CancellationToken), null });
        var finiteOverview = periapsisGrid.GetType().GetField("PorkchopGrid").GetValue(periapsisGrid);
        var finiteCells = (Array)finiteOverview.GetType().GetField("Cells").GetValue(finiteOverview);
        var normalFiniteCells = (Array)grid.GetType().GetField("Cells").GetValue(grid);
        for (int index = 0; index < finiteCells.Length; index++)
        {
            var a = finiteCells.GetValue(index); var b = normalFiniteCells.GetValue(index);
            bool valid = (bool)a.GetType().GetField("Valid").GetValue(a);
            Check(valid == (bool)b.GetType().GetField("Valid").GetValue(b)
                && (!valid || Math.Abs((double)Call(a, "Cost", true) - (double)Call(b, "Cost", true)) < 1e-6),
                "Cached finite-burn overview differs from recalculating Porkchop with the rocket's actual engine phases");
        }
        var periapsisRoute = Call(periapsisGrid, "Pick", true, false, double.PositiveInfinity, now + 30, null);
        Check((bool)periapsisRoute.GetType().GetField("Valid").GetValue(periapsisRoute), "Recorded finite moon transfer has no Earth-periapsis route");
        double periapsisDeparture = (double)periapsisRoute.GetType().GetField("Departure").GetValue(periapsisRoute);
        Check(Math.Abs(parking.GetStateVectorsAt(new UniverseTime(periapsisDeparture)).PositionCci.Length() - parking.Periapsis) < 1,
            "Finite Oberth node is placed away from Earth's actual periapsis");
        Set(request, "Oberth", false);
        Console.WriteLine("PASS recorded finite moon-transfer Oberth search uses native Earth periapsis epochs");
        var optimized = Call(grid, "Pick", true, false, double.PositiveInfinity, now + 30, null);
        Check((double)Call(optimized, "Cost", true) <= (double)Call(corrected, "Cost", true), "Native minimum selection ignores refined candidates");
        var displayPlanner = New("ManeuverPlanner");
        Set(displayPlanner, "Grid", grid); Set(displayPlanner, "Now", now); Set(displayPlanner, "OriginName", "Earth");
        Set(displayPlanner, "UnavailableReason", null); Set(Window, "Planner", displayPlanner); Set(Window, "_tab", 5);
        Call(displayPlanner, "SelectBest", false);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("maneuver-continuous-recorded-orbit.png", 800, 670, 12, 12);
        Set(Window, "Planner", null); Set(Window, "_tab", 0);
        var cells = new[] { original, corrected };
        var destination = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
        typeof(Celestial).GetField("_meanRadius", BindingFlags.Public | BindingFlags.Instance).SetValue(destination, 1737100.0);
        Set(destination, "Mass", 4.9048695e12 / 6.6743e-11); Set(destination, "SphereOfInfluence", 66e6);
        foreach (var cell in cells)
        {
            double departure = (double)cell.GetType().GetField("Departure").GetValue(cell);
            var dv = cell.GetType().GetField("DeltaV").GetValue(cell);
            double3 delta = new double3((double)dv.GetType().GetField("X").GetValue(dv), (double)dv.GetType().GetField("Y").GetValue(dv), (double)dv.GetType().GetField("Z").GetValue(dv));
            var time = new UniverseTime(departure);
            var state = parking.GetStateVectorsAt(time);
            var impulsive = Orbit.CreateFromStateCci(earth, time, state.PositionCci, state.VelocityCci + delta, byte4.Zero);
            var arc = FiniteBurnPredictor.Predict(parking, time, OrbitalTransfers.CciToVlf(parking, time, delta),
                phases);
            var finite = Orbit.CreateFromStateCci(earth, arc.Cutoff, arc.PositionCci, arc.VelocityCci, byte4.Zero);
            double arrival = departure + (double)cell.GetType().GetField("Duration").GetValue(cell);
            var end = finite.GetStateVectorsAt(new UniverseTime(arrival));
            var moonEnd = moon.GetStateVectorsAt(new UniverseTime(arrival));
            Console.WriteLine($"  recorded transfer: departure={departure:F1}s dv={delta.Length():F1}m/s impulse_pe={impulsive.Periapsis - earth.MeanRadius:F0}m finite_pe={finite.Periapsis - earth.MeanRadius:F0}m burn={(arc.Cutoff - arc.Ignition).Seconds():F1}s miss={(end.PositionCci - moonEnd.PositionCci).Length():F0}m bound={finite.IsBound()}");
            if (ReferenceEquals(cell, corrected))
            {
                Check((end.PositionCci - moonEnd.PositionCci).Length() < destination.SphereOfInfluence, "Corrected finite burn still misses lunar SOI");
                double lo = arc.Cutoff.Seconds(), hi = arrival;
                for (int i = 0; i < 60; i++)
                {
                    double mid = (lo + hi) / 2;
                    double distance = (finite.GetStateVectorsAt(new UniverseTime(mid)).PositionCci - moon.GetStateVectorsAt(new UniverseTime(mid)).PositionCci).Length();
                    if (distance > destination.SphereOfInfluence) lo = mid; else hi = mid;
                }
                var shipEntry = finite.GetStateVectorsAt(new UniverseTime(hi)); var moonEntry = moon.GetStateVectorsAt(new UniverseTime(hi));
                var encounter = Orbit.CreateFromStateCci(destination, new UniverseTime(hi), shipEntry.PositionCci - moonEntry.PositionCci,
                    shipEntry.VelocityCci - moonEntry.VelocityCci, byte4.Zero);
                Console.WriteLine($"  recorded native lunar arrival: periapsis={encounter.Periapsis - destination.MeanRadius:F0}m entry={hi:F1}s");
                Check(Math.Abs(encounter.Periapsis - destination.MeanRadius - 100000) < 300,
                    "Corrected finite burn misses the requested lunar periapsis");
                var returnTime = finite.GetNextPeriapsisTime(arc.Cutoff);
                Check(!returnTime.HasValue || returnTime.Value <= arc.Cutoff || returnTime.Value > new UniverseTime(hi),
                    "Selected corrected transfer falls back before entering lunar SOI");
            }
        }
        var refine = Mod.GetType("KITN.TransferBurnRefiner").GetMethod("Refine");
        foreach (double altitude in new[] { 10000.0, 1000000.0 })
        {
            Set(request, "PeriapsisAltitude", altitude);
            var route = refine.Invoke(null, new object[] { request, original, phases, default(System.Threading.CancellationToken) });
            Check((bool)route.GetType().GetField("Valid").GetValue(route), "Finite burn cannot target a low or high lunar periapsis");
            double departure = (double)route.GetType().GetField("Departure").GetValue(route);
            double arrival = departure + (double)route.GetType().GetField("Duration").GetValue(route);
            var dv = route.GetType().GetField("DeltaV").GetValue(route);
            var delta = new double3((double)dv.GetType().GetField("X").GetValue(dv), (double)dv.GetType().GetField("Y").GetValue(dv), (double)dv.GetType().GetField("Z").GetValue(dv));
            var time = new UniverseTime(departure);
            var arc = FiniteBurnPredictor.Predict(parking, time, OrbitalTransfers.CciToVlf(parking, time, delta), phases);
            var outgoing = Orbit.CreateFromStateCci(earth, arc.Cutoff, arc.PositionCci, arc.VelocityCci, byte4.Zero);
            double lo = arc.Cutoff.Seconds(), hi = arrival;
            for (int pass = 0; pass < 60; pass++)
            {
                double mid = (lo + hi) / 2;
                if ((outgoing.GetStateVectorsAt(new UniverseTime(mid)).PositionCci - moon.GetStateVectorsAt(new UniverseTime(mid)).PositionCci).Length() > destination.SphereOfInfluence) lo = mid;
                else hi = mid;
            }
            var ship = outgoing.GetStateVectorsAt(new UniverseTime(hi)); var body = moon.GetStateVectorsAt(new UniverseTime(hi));
            var approach = Orbit.CreateFromStateCci(destination, new UniverseTime(hi), ship.PositionCci - body.PositionCci, ship.VelocityCci - body.VelocityCci, byte4.Zero);
            Check(Math.Abs(approach.Periapsis - destination.MeanRadius - altitude) < 300, "Native lunar arrival does not match low/high requested altitude");
        }
        Set(request, "PeriapsisAltitude", 100000.0);
        var weak = new[] { new FiniteBurnPhase(955522.3125, 222.89703369140625, 52813.765625, 5) };
        var impossible = refine.Invoke(null, new object[] { request, original, weak, default(System.Threading.CancellationToken) });
        Check(!(bool)impossible.GetType().GetField("Valid").GetValue(impossible), "Finite burn invents thrust after propellant is exhausted");
        using var cancelled = new System.Threading.CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { refine.Invoke(null, new object[] { request, original, phases, cancelled.Token }); }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { stopped = true; }
        Check(stopped, "Cancelling finite burn correction does not stop the worker");
        Set(request, "UseBestEffortFallback", true);
        var windows = Array.CreateInstance(Mod.GetType("KITN.TransferWindowExclusion"), 1);
        windows.SetValue(New("TransferWindowExclusion", now + 300, hohmann,
            parking.Period * 3, hohmann * 3, true), 0);
        Set(request, "ExcludedWindows", windows);
        var fallbackGrid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null,
            new[] { request, (object)default(System.Threading.CancellationToken), null });
        int fallbackIndex = (int)fallbackGrid.GetType().GetMethod("Best").Invoke(fallbackGrid, new object[] { true, double.NegativeInfinity, null });
        Check(fallbackIndex >= 0, "Finite burn refinement prevents the requested best-effort fallback");
        var fallbackRoute = ((Array)fallbackGrid.GetType().GetField("Cells").GetValue(fallbackGrid)).GetValue(fallbackIndex);
        Check((bool)fallbackRoute.GetType().GetField("BestEffort").GetValue(fallbackRoute), "Fallback route loses its best-effort flag");
        Set(request, "DepartureSafetyRadius", parking.Apoapsis + 1000);
        var unsafeFallback = refine.Invoke(null, new object[] { request, fallbackRoute, phases, default(System.Threading.CancellationToken) });
        Check(!(bool)unsafeFallback.GetType().GetField("Valid").GetValue(unsafeFallback),
            "Best-effort refinement ignores an impact during the engine burn");

        var planner = New("ManeuverPlanner"); Set(planner, "Now", now);
        var overridden = Orbit.Create(earth, new OrbitData(new UniverseTime(442332.8562855744), earth.Mass * 1.3,
            axis * (1 - eccentricity), eccentricity, inclination, node, argument), byte4.Zero, centralMass: earth.Mass * 1.3);
        var snapshot = planner.GetType().GetMethod("SnapshotOrbit", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new object[] { overridden });
        Check(Math.Abs((double)snapshot.GetType().GetField("Mu").GetValue(snapshot) / overridden.Mu - 1) < 1e-12,
            "Mod-body ephemeris discards the orbit's central-mass override");
        object[] propagation = { now + 600000, null, null };
        Check((bool)snapshot.GetType().GetMethod("State").Invoke(snapshot, propagation), "Overridden orbit snapshot cannot propagate");
        var expected = overridden.GetStateVectorsAt(new UniverseTime(now + 600000));
        var actual = propagation[1];
        var position = new double3((double)actual.GetType().GetField("X").GetValue(actual), (double)actual.GetType().GetField("Y").GetValue(actual), (double)actual.GetType().GetField("Z").GetValue(actual));
        Check((position - expected.PositionCci).Length() < 1, "Overridden mod-body ephemeris drifts from native orbit");
        foreach (var velocity in new[] { new double3(0, 7600, 300), new double3(-500, 12000, 900) })
        {
            var native = Orbit.CreateFromStateCci(earth, new UniverseTime(0), new double3(7e6, 0, 0), velocity, byte4.Zero);
            var coast = New("TransferCoast", earth.Mu, Vector(new double3(7e6, 0, 0)), Vector(velocity));
            foreach (double elapsed in new[] { -1000.0, 1000.0, 500000.0 })
            {
                object[] stateArgs = { elapsed, null, null };
                Check((bool)coast.GetType().GetMethod("State").Invoke(coast, stateArgs), "Conic coast does not propagate");
                var result = stateArgs[1];
                var computed = new double3((double)result.GetType().GetField("X").GetValue(result), (double)result.GetType().GetField("Y").GetValue(result), (double)result.GetType().GetField("Z").GetValue(result));
                Check((computed - native.GetStateVectorsAt(new UniverseTime(elapsed)).PositionCci).Length() < 1,
                    "Elliptic or hyperbolic coast disagrees with native propagation");
            }
        }
        Console.WriteLine("PASS recorded inclined parking orbit with finite thrust reaches lunar SOI before return at 10/100/1000 km periapsis; fuel, cancellation, fallback, impact, conic and mass-override checks");
    }

    private static void NativeGravityAssistChecks()
    {
        const double mu = 1.3271244e20, au = 149597870700, day = 86400;
        IParentBody Body(string id, double gravity, double radius, double soi)
        {
            var body = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
            var proxy = (PlannerBodyProxy)body; proxy.Id = id; proxy.Mu = gravity; proxy.Radius = radius; proxy.Soi = soi;
            return body;
        }
        var sun = Body("Sun", mu, 7e8, double.PositiveInfinity);
        var venus = Body("Venus", 3.24859e14, 6051800, 6.16e8);
        var mars = Body("Mars", 4.2828e13, 3390000, 5.77e8);
        Orbit Circle(double radius, double phase) => Orbit.CreateFromStateCci(sun, new UniverseTime(0),
            new double3(radius * Math.Cos(phase), radius * Math.Sin(phase) * Math.Cos(0.12), radius * Math.Sin(phase) * Math.Sin(0.12)),
            new double3(-Math.Sqrt(mu / radius) * Math.Sin(phase), Math.Sqrt(mu / radius) * Math.Cos(phase) * Math.Cos(0.12),
                Math.Sqrt(mu / radius) * Math.Cos(phase) * Math.Sin(0.12)), byte4.Zero);
        object Vector(double3 vector) => New("LandingVector", vector.X, vector.Y, vector.Z);
        double3 Native(object vector) => new((double)vector.GetType().GetField("X").GetValue(vector),
            (double)vector.GetType().GetField("Y").GetValue(vector), (double)vector.GetType().GetField("Z").GetValue(vector));
        double Number(object obj, string field) => (double)obj.GetType().GetField(field).GetValue(obj);
        object Snapshot(Orbit orbit)
        {
            var state = orbit.GetStateVectorsAt(new UniverseTime(0));
            return New("TransferOrbit", orbit.Mu, 0.0, Vector(state.PositionCci), Vector(state.VelocityCci));
        }
        var parking = Circle(au, 0); var assistOrbit = Circle(0.723 * au, Math.PI / 2); var targetOrbit = Circle(1.52 * au, Math.PI / 2);
        var request = New("TransferRequest");
        Set(request, "Parking", Snapshot(parking)); Set(request, "Target", Snapshot(targetOrbit));
        Set(request, "CentralMu", mu); Set(request, "CentralSafetyRadius", 7e8); Set(request, "DepartureSafetyRadius", 7e8);
        Set(request, "CentralSoi", double.PositiveInfinity); Set(request, "TargetMu", mars.Mu);
        Set(request, "TargetRadius", mars.MeanRadius); Set(request, "TargetSoi", mars.SphereOfInfluence); Set(request, "PeriapsisAltitude", 200000.0);
        Set(request, "Earliest", 300.0); Set(request, "Latest", 400 * day); Set(request, "MinimumDuration", 100 * day); Set(request, "MaximumDuration", 800 * day);
        Set(request, "Width", 32); Set(request, "Height", 24); Set(request, "IncludeCapture", true); Set(request, "GravityAssists", true);
        var assistBodies = Array.CreateInstance(Mod.GetType("KITN.TransferBodySnapshot"), 1);
        assistBodies.SetValue(New("TransferBodySnapshot", "Venus", Snapshot(assistOrbit), venus.Mu, venus.MeanRadius, venus.SphereOfInfluence, 6251800.0), 0);
        Set(request, "AssistBodies", assistBodies);
        var grid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null, new[] { request, (object)default(System.Threading.CancellationToken), null });
        var candidates = (System.Collections.IEnumerable)grid.GetType().GetField("Candidates").GetValue(grid);
        var solution = candidates.Cast<object>().First(candidate => candidate.GetType().GetField("Flyby").GetValue(candidate) != null);
        double departure = Number(solution, "Departure"), arrival = departure + Number(solution, "Duration");
        var start = parking.GetStateVectorsAt(new UniverseTime(departure));
        var coast = Orbit.CreateFromStateCci(sun, new UniverseTime(departure), start.PositionCci,
            start.VelocityCci + Native(solution.GetType().GetField("DeltaV").GetValue(solution)), byte4.Zero);
        double Entry(Orbit ship, Orbit body, double epoch, double hint, double soi)
        {
            double lo = epoch, hi = hint;
            Check((ship.GetStateVectorsAt(new UniverseTime(hi)).PositionCci - body.GetStateVectorsAt(new UniverseTime(hi)).PositionCci).Length() < soi,
                "Native trajectory misses the computed assist or destination SOI");
            for (int pass = 0; pass < 60; pass++)
            {
                double mid = (lo + hi) / 2;
                if ((ship.GetStateVectorsAt(new UniverseTime(mid)).PositionCci - body.GetStateVectorsAt(new UniverseTime(mid)).PositionCci).Length() > soi) lo = mid; else hi = mid;
            }
            return hi;
        }
        var metadata = solution.GetType().GetField("Flyby").GetValue(solution);
        double entry = Entry(coast, assistOrbit, departure, Number(metadata, "Time"), venus.SphereOfInfluence);
        var shipEntry = coast.GetStateVectorsAt(new UniverseTime(entry)); var bodyEntry = assistOrbit.GetStateVectorsAt(new UniverseTime(entry));
        var local = Orbit.CreateFromStateCci(venus, new UniverseTime(entry), shipEntry.PositionCci - bodyEntry.PositionCci,
            shipEntry.VelocityCci - bodyEntry.VelocityCci, byte4.Zero);
        Check(local.IsUnbound() && local.Periapsis > 6252800 && Math.Abs(local.Periapsis - Number(metadata, "Periapsis")) < 300,
            "Native flyby is unsafe, bound or disagrees with the computed periapsis");
        double peTime = local.GetNextPeriapsisTime(new UniverseTime(entry)).Value.Seconds();
        double low = peTime, high = peTime + 10 * day;
        for (int pass = 0; pass < 60; pass++)
        {
            double mid = (low + high) / 2;
            if (local.GetStateVectorsAt(new UniverseTime(mid)).PositionCci.Length() < venus.SphereOfInfluence) low = mid; else high = mid;
        }
        var localExit = local.GetStateVectorsAt(new UniverseTime(high)); var bodyExit = assistOrbit.GetStateVectorsAt(new UniverseTime(high));
        Check(Math.Abs(localExit.PositionCci.Length() - venus.SphereOfInfluence) < 0.1
            && Math.Abs(localExit.VelocityCci.Length() - (shipEntry.VelocityCci - bodyEntry.VelocityCci).Length()) < 0.001,
            "Unpowered assist changes excess energy across equal SOI radii");
        var final = Orbit.CreateFromStateCci(sun, new UniverseTime(high), bodyExit.PositionCci + localExit.PositionCci,
            bodyExit.VelocityCci + localExit.VelocityCci, byte4.Zero);
        double targetEntry = Entry(final, targetOrbit, high, arrival, mars.SphereOfInfluence);
        var shipArrival = final.GetStateVectorsAt(new UniverseTime(targetEntry)); var bodyArrival = targetOrbit.GetStateVectorsAt(new UniverseTime(targetEntry));
        var encounter = Orbit.CreateFromStateCci(mars, new UniverseTime(targetEntry), shipArrival.PositionCci - bodyArrival.PositionCci,
            shipArrival.VelocityCci - bodyArrival.VelocityCci, byte4.Zero);
        Check(Math.Abs(encounter.Periapsis - mars.MeanRadius - 200000) < 500,
            "Independent native propagation through Venus misses requested Mars arrival periapsis");
        var assistCheck = Mod.GetType("KITN.ManeuverPlanner").GetMethod("AssistPredictionError", BindingFlags.NonPublic | BindingFlags.Static);
        var plan = FlightPlan.CreateUninitialized(KeyHash.Zero);
        Check((string)assistCheck.Invoke(null, new object[] { plan, "Venus", "Mars" }) == "transfer.assist_missed",
            "Native validation accepts a route without the promised gravity assist");
        plan.Patches.Add(new PatchedConic(new UniverseTime(entry), new UniverseTime(high), PatchTransition.Encounter, PatchTransition.Escape, local, KeyHash.Zero));
        Check(assistCheck.Invoke(null, new object[] { plan, "Venus", "Mars" }) == null,
            "Native validation rejects a real planned assist encounter");
        Console.WriteLine($"PASS independent native Sun → Venus → Mars trajectory conserves flyby energy and reaches {encounter.Periapsis - mars.MeanRadius:F0} m arrival periapsis");

        var earth = Body("Earth", 3.986004418e14, 6371000, 9.24e8);
        var localParking = Orbit.CreateFromStateCci(earth, new UniverseTime(0), new double3(7e6, 0, 0),
            new double3(0, Math.Sqrt(earth.Mu / 7e6) * Math.Cos(0.12), Math.Sqrt(earth.Mu / 7e6) * Math.Sin(0.12)), byte4.Zero);
        Set(request, "Parking", Snapshot(localParking)); Set(request, "Source", Snapshot(parking));
        Set(request, "DepartureSafetyRadius", 6500000.0); Set(request, "DepartureSoi", earth.SphereOfInfluence);
        Set(request, "Earliest", 6000.0); Set(request, "Latest", 6e7); Set(request, "MinimumDuration", 1e7); Set(request, "MaximumDuration", 3e7);
        Set(request, "Width", 12); Set(request, "Height", 12); Set(request, "GravityAssists", false);
        var phases = new[] { new FiniteBurnPhase(955522.3125, 222.89703369140625, 52813.765625, 49474.73046875 / 222.89703369140625) };
        Mod.GetType("KITN.TransferBurnRefiner").GetMethod("Attach").Invoke(null, new object[] { request, phases });
        var ejectionGrid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null, new[] { request, (object)default(System.Threading.CancellationToken), null });
        var route = Call(ejectionGrid, "Pick", true, false, double.PositiveInfinity, 0.0, null);
        Check((bool)route.GetType().GetField("Valid").GetValue(route), "Finite interplanetary departure loses all direct solutions");
        departure = Number(route, "Departure"); arrival = departure + Number(route, "Duration");
        var time = new UniverseTime(departure);
        var delta = Native(route.GetType().GetField("DeltaV").GetValue(route));
        var burn = FiniteBurnPredictor.Predict(localParking, time, OrbitalTransfers.CciToVlf(localParking, time, delta), phases);
        var escapeCoast = New("TransferCoast", earth.Mu, Vector(burn.PositionCci), Vector(burn.VelocityCci));
        double escapeSeconds = (double)Call(escapeCoast, "OutgoingRadiusTime", earth.SphereOfInfluence);
        var departureOrbit = Orbit.CreateFromStateCci(earth, burn.Cutoff, burn.PositionCci, burn.VelocityCci, byte4.Zero);
        var exitTime = burn.Cutoff + escapeSeconds;
        var escape = departureOrbit.GetStateVectorsAt(exitTime); var origin = parking.GetStateVectorsAt(exitTime);
        Check(Math.Abs(escape.PositionCci.Length() - earth.SphereOfInfluence) < 1, "Finite departure SOI exit disagrees with native propagation");
        var heliocentric = Orbit.CreateFromStateCci(sun, exitTime, origin.PositionCci + escape.PositionCci, origin.VelocityCci + escape.VelocityCci, byte4.Zero);
        double marsEntry = Entry(heliocentric, targetOrbit, exitTime.Seconds(), arrival, mars.SphereOfInfluence);
        var shipAtMars = heliocentric.GetStateVectorsAt(new UniverseTime(marsEntry)); var marsState = targetOrbit.GetStateVectorsAt(new UniverseTime(marsEntry));
        var marsApproach = Orbit.CreateFromStateCci(mars, new UniverseTime(marsEntry), shipAtMars.PositionCci - marsState.PositionCci,
            shipAtMars.VelocityCci - marsState.VelocityCci, byte4.Zero);
        Check(Math.Abs(marsApproach.Periapsis - mars.MeanRadius - 200000) < 500, "Finite ejection refinement misses native arrival periapsis");
        Console.WriteLine($"PASS native finite Earth departure crosses the moving SOI boundary and reaches Mars at {marsApproach.Periapsis - mars.MeanRadius:F0} m periapsis");
        var circularParking = localParking;
        localParking = Orbit.CreateFromStateCci(earth, new UniverseTime(0), new double3(7e6, 0, 0),
            new double3(0, Math.Sqrt(earth.Mu * 1.2 / 7e6) * Math.Cos(0.12), Math.Sqrt(earth.Mu * 1.2 / 7e6) * Math.Sin(0.12)), byte4.Zero);
        Set(request, "Parking", Snapshot(localParking)); Set(request, "Oberth", true);
        var oberthGrid = Mod.GetType("KITN.TransferMath").GetMethod("Search").Invoke(null, new[] { request, (object)default(System.Threading.CancellationToken), null });
        var oberthRoute = Call(oberthGrid, "Pick", true, false, double.PositiveInfinity, 0.0, null);
        Check((bool)oberthRoute.GetType().GetField("Valid").GetValue(oberthRoute), "Finite interplanetary Oberth search loses all periapsis routes");
        departure = Number(oberthRoute, "Departure"); arrival = departure + Number(oberthRoute, "Duration"); time = new UniverseTime(departure);
        Check(Math.Abs(localParking.GetStateVectorsAt(time).PositionCci.Length() - localParking.Periapsis) < 1,
            "Interplanetary Oberth search rotates the burn away from periapsis");
        delta = Native(oberthRoute.GetType().GetField("DeltaV").GetValue(oberthRoute));
        burn = FiniteBurnPredictor.Predict(localParking, time, OrbitalTransfers.CciToVlf(localParking, time, delta), phases);
        escapeCoast = New("TransferCoast", earth.Mu, Vector(burn.PositionCci), Vector(burn.VelocityCci));
        escapeSeconds = (double)Call(escapeCoast, "OutgoingRadiusTime", earth.SphereOfInfluence); exitTime = burn.Cutoff + escapeSeconds;
        departureOrbit = Orbit.CreateFromStateCci(earth, burn.Cutoff, burn.PositionCci, burn.VelocityCci, byte4.Zero);
        escape = departureOrbit.GetStateVectorsAt(exitTime); origin = parking.GetStateVectorsAt(exitTime);
        heliocentric = Orbit.CreateFromStateCci(sun, exitTime, origin.PositionCci + escape.PositionCci, origin.VelocityCci + escape.VelocityCci, byte4.Zero);
        marsEntry = Entry(heliocentric, targetOrbit, exitTime.Seconds(), arrival, mars.SphereOfInfluence);
        shipAtMars = heliocentric.GetStateVectorsAt(new UniverseTime(marsEntry)); marsState = targetOrbit.GetStateVectorsAt(new UniverseTime(marsEntry));
        marsApproach = Orbit.CreateFromStateCci(mars, new UniverseTime(marsEntry), shipAtMars.PositionCci - marsState.PositionCci,
            shipAtMars.VelocityCci - marsState.VelocityCci, byte4.Zero);
        Check(Math.Abs(marsApproach.Periapsis - mars.MeanRadius - 200000) < 500, "Native periapsis-centered Earth departure misses Mars");
        Console.WriteLine("PASS native eccentric Earth parking orbit departs at periapsis with finite thrust and reaches Mars");
        localParking = circularParking; Set(request, "Parking", Snapshot(localParking)); Set(request, "Oberth", false);
        Set(request, "GravityAssists", true); Set(request, "Earliest", 6000.0); Set(request, "Latest", 400 * day);
        Set(request, "MinimumDuration", 100 * day); Set(request, "MaximumDuration", 800 * day); Set(request, "Width", 32);
        var assistedEjection = New("TransferGrid", request);
        Mod.GetType("KITN.TransferGravityAssist").GetMethod("Search").Invoke(null,
            new object[] { assistedEjection, default(System.Threading.CancellationToken), null });
        var flybys = ((System.Collections.IEnumerable)assistedEjection.GetType().GetField("Candidates").GetValue(assistedEjection)).Cast<object>().ToArray();
        Console.WriteLine($"  finite Earth departure assist candidates={flybys.Length}");
        Check(flybys.Length > 0, "Gravity assists work only for solar parking, not finite planetary departures");
        var assistedRoute = flybys[0]; departure = Number(assistedRoute, "Departure"); arrival = departure + Number(assistedRoute, "Duration");
        time = new UniverseTime(departure); delta = Native(assistedRoute.GetType().GetField("DeltaV").GetValue(assistedRoute));
        burn = FiniteBurnPredictor.Predict(localParking, time, OrbitalTransfers.CciToVlf(localParking, time, delta), phases);
        escapeCoast = New("TransferCoast", earth.Mu, Vector(burn.PositionCci), Vector(burn.VelocityCci));
        escapeSeconds = (double)Call(escapeCoast, "OutgoingRadiusTime", earth.SphereOfInfluence); exitTime = burn.Cutoff + escapeSeconds;
        departureOrbit = Orbit.CreateFromStateCci(earth, burn.Cutoff, burn.PositionCci, burn.VelocityCci, byte4.Zero);
        escape = departureOrbit.GetStateVectorsAt(exitTime); origin = parking.GetStateVectorsAt(exitTime);
        heliocentric = Orbit.CreateFromStateCci(sun, exitTime, origin.PositionCci + escape.PositionCci, origin.VelocityCci + escape.VelocityCci, byte4.Zero);
        metadata = assistedRoute.GetType().GetField("Flyby").GetValue(assistedRoute);
        entry = Entry(heliocentric, assistOrbit, exitTime.Seconds(), Number(metadata, "Time"), venus.SphereOfInfluence);
        shipEntry = heliocentric.GetStateVectorsAt(new UniverseTime(entry)); bodyEntry = assistOrbit.GetStateVectorsAt(new UniverseTime(entry));
        local = Orbit.CreateFromStateCci(venus, new UniverseTime(entry), shipEntry.PositionCci - bodyEntry.PositionCci,
            shipEntry.VelocityCci - bodyEntry.VelocityCci, byte4.Zero);
        Check(local.Periapsis > 6252800, "Finite planetary departure produces an unsafe assist");
        peTime = local.GetNextPeriapsisTime(new UniverseTime(entry)).Value.Seconds(); low = peTime; high = peTime + 10 * day;
        for (int pass = 0; pass < 60; pass++)
        {
            double mid = (low + high) / 2;
            if (local.GetStateVectorsAt(new UniverseTime(mid)).PositionCci.Length() < venus.SphereOfInfluence) low = mid; else high = mid;
        }
        localExit = local.GetStateVectorsAt(new UniverseTime(high)); bodyExit = assistOrbit.GetStateVectorsAt(new UniverseTime(high));
        final = Orbit.CreateFromStateCci(sun, new UniverseTime(high), bodyExit.PositionCci + localExit.PositionCci,
            bodyExit.VelocityCci + localExit.VelocityCci, byte4.Zero);
        targetEntry = Entry(final, targetOrbit, high, arrival, mars.SphereOfInfluence);
        shipArrival = final.GetStateVectorsAt(new UniverseTime(targetEntry)); bodyArrival = targetOrbit.GetStateVectorsAt(new UniverseTime(targetEntry));
        encounter = Orbit.CreateFromStateCci(mars, new UniverseTime(targetEntry), shipArrival.PositionCci - bodyArrival.PositionCci,
            shipArrival.VelocityCci - bodyArrival.VelocityCci, byte4.Zero);
        // Native hyperbolic propagation stops at 1e-8 in anomaly. Metre-scale
        // differences at the first SOI magnify across the subsequent flyby/coast.
        Check(Math.Abs(encounter.Periapsis - mars.MeanRadius - 200000) < 2000,
            "Finite Earth departure via Venus does not reach the predicted native Mars arrival");
        Console.WriteLine($"PASS native finite Earth → Venus → Mars transfer reaches {encounter.Periapsis - mars.MeanRadius:F0} m arrival periapsis");
    }

    private static void NativeRouteValidationChecks(Vehicle vehicle, IParentBody earth, Celestial moon, Orbit incoming, Orbit parking)
    {
        var type = Mod.GetType("KITN.ManeuverPlanner");
        var inspect = type.GetMethod("PredictionError", BindingFlags.NonPublic | BindingFlags.Static);
        FlightPlan Prediction(Orbit outgoing, bool encounter, double entry = 1000, bool impact = false)
        {
            var plan = FlightPlan.CreateUninitialized(KeyHash.Zero);
            plan.Patches.Add(new PatchedConic(new UniverseTime(0), encounter ? new UniverseTime(entry) : UniverseTime.EndOfTime,
                PatchTransition.Burn, impact ? PatchTransition.Impact : encounter ? PatchTransition.Encounter : PatchTransition.Final, outgoing, KeyHash.Zero));
            if (encounter) plan.Patches.Add(new PatchedConic(new UniverseTime(entry), new UniverseTime(10000),
                PatchTransition.Encounter, PatchTransition.Final, incoming, KeyHash.Zero));
            return plan;
        }
        string Error(FlightPlan plan) => (string)inspect.Invoke(null, new object[] { plan, earth, moon });
        var escape = Orbit.CreateFromStateCci(earth, new UniverseTime(0), new double3(7e6, 0, 0), new double3(0, 12000, 0), byte4.Zero);
        Check(Error(new FlightPlan(parking, KeyHash.Zero)) == "transfer.checking", "An uncalculated candidate is accepted or rejected without its burn prediction");
        Check(Error(Prediction(parking, false)) == "transfer.returning", "Bound fallback with no target SOI is accepted");
        Check(Error(Prediction(parking, true, 10000)) == "transfer.returning", "Late encounter beyond the first return is accepted");
        Check(Error(Prediction(escape, false)) == "transfer.no_encounter", "Escape with no target encounter is accepted");
        Check(Error(Prediction(escape, true, impact: true)) == "transfer.impact", "Impact before the target encounter is accepted");
        Check(Error(Prediction(escape, true)) == null, "Necessary escape with a safe target encounter is rejected");
        var circular = Orbit.CreateFromStateCci(earth, new UniverseTime(0), new double3(7e6, 0, 0), new double3(0, Math.Sqrt(earth.Mu / 7e6), 0), byte4.Zero);
        Check(Error(Prediction(circular, true)) == null, "A confirmed encounter before the first return is rejected merely because the transfer is bound");
        Console.WriteLine("PASS native route validation requires a safe encounter before fallback or impact and retains necessary escapes");

        object Vector(double3 value) => New("LandingVector", value.X, value.Y, value.Z);
        object MakeRequest()
        {
            var request = New("TransferRequest");
            var source = parking.GetStateVectorsAt(new UniverseTime(0));
            var destination = moon.Orbit.GetStateVectorsAt(new UniverseTime(0));
            Set(request, "Parking", New("TransferOrbit", earth.Mu, 0.0, Vector(source.PositionCci), Vector(source.VelocityCci)));
            Set(request, "Target", New("TransferOrbit", earth.Mu, 0.0, Vector(destination.PositionCci), Vector(destination.VelocityCci)));
            Set(request, "CentralMu", earth.Mu); Set(request, "CentralSoi", earth.SphereOfInfluence);
            Set(request, "TargetMu", ((IParentBody)moon).Mu); Set(request, "TargetRadius", moon.MeanRadius);
            Set(request, "PeriapsisAltitude", 100000.0); Set(request, "DepartureSafetyRadius", earth.GetNearSurfaceRadius());
            Set(request, "Earliest", 300.0); Set(request, "Latest", 300 + parking.Period * 2);
            Set(request, "MinimumDuration", 180000.0); Set(request, "MaximumDuration", 750000.0);
            return request;
        }
        (object planner, Burn candidate) Candidate(bool execute, bool bestEffort = false)
        {
            Set(vehicle, "FlightPlan", new FlightPlan(parking, KeyHash.Zero));
            var fc = new FlightComputer { RollMode = FlightComputerRollMode.Decoupled }; Set(vehicle, "FlightComputer", fc);
            var request = MakeRequest(); var grid = New("TransferGrid", request);
            var solution = New("TransferSolution", 300.0, 500000.0, Vector(parking.GetStateVectorsAt(new UniverseTime(300)).PositionCci),
                Vector(new double3(3200, 0, 0)), Vector(new double3(600, 0, 0)), 1000.0, false, bestEffort);
            ((Array)grid.GetType().GetField("Cells").GetValue(grid)).SetValue(solution, 0);
            var planner = New("ManeuverPlanner");
            Set(planner, "_vehicle", vehicle); Set(planner, "Target", moon); Set(planner, "UnavailableReason", null);
            Set(planner, "Grid", grid); Set(planner, "_snapshot", request); Set(planner, "SelectedIndex", 0);
            Set(planner, "SearchWindow", parking.Period * 2); Set(planner, "MinimumDuration", 180000.0); Set(planner, "MaximumDuration", 750000.0);
            var burn = Burn.Create(parking.GetPointAt(new UniverseTime(300)), 300, new double3(3200, 0, 0), vehicle.FlightPlan.Patches[0], vehicle);
            burn.PlannedThrottle = 1; fc.AddBurn(burn);
            Set(planner, "_node", burn); Set(planner, "_captureVehicle", vehicle); Set(planner, "_captureTarget", moon); Set(planner, "_captureDeparture", burn);
            type.GetMethod("QueueValidation", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new[] { vehicle, burn, solution, (object)execute });
            return (planner, burn);
        }
        var controllable = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.NonPublic | BindingFlags.Static);
        bool original = (bool)controllable.GetValue(null); controllable.SetValue(null, true);
        try
        {
            Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            var pending = Candidate(true);
            Call(pending.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check((bool)type.GetProperty("IsChecking").GetValue(pending.planner) && vehicle.FlightComputer.BurnMode == FlightComputerBurnMode.Manual
                && vehicle.FlightComputer.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None && !Universe.IsAutoWarpActive,
                "An unverified candidate starts alignment, warp or automatic engines");
            pending.candidate.FlightPlan = Prediction(parking, false); vehicle.BurnPlan.FlightPlansOutOfDate = false;
            Call(pending.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check(!vehicle.BurnPlan.TryGetBurn(pending.candidate) && !(bool)type.GetProperty("IsSearching").GetValue(pending.planner)
                && !(bool)type.GetProperty("IsExecuting").GetValue(pending.planner)
                && (bool)type.GetProperty("CanImproveRoute").GetValue(pending.planner), "Unsafe node starts an unsolicited search or offers no better-route action");
            for (int step = 0; step < 5; step++) Call(pending.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check(vehicle.BurnPlan.BurnCount == 0 && !(bool)type.GetProperty("IsSearching").GetValue(pending.planner), "Waiting for the better-route button creates nodes or restarts search");
            Orbit unchangedParking = vehicle.Orbit;
            Call(pending.planner, "HandleAction", "transfer_improve", vehicle, false);
            Check((bool)type.GetProperty("IsSearching").GetValue(pending.planner) && ReferenceEquals(vehicle.Orbit, unchangedParking),
                "The better-route action does not search or modifies the current parking orbit");
            var worker = (System.Threading.Tasks.Task)type.GetField("_search", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pending.planner);
            Check(worker.Wait(10000), "Requested safe-route search never completes");
            type.GetMethod("Harvest", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(pending.planner, null);
            Check((bool)type.GetProperty("Selected").GetValue(pending.planner).GetType().GetField("Valid").GetValue(type.GetProperty("Selected").GetValue(pending.planner))
                && (bool)type.GetField("_replanPending", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pending.planner),
                "Requested better-route search finds no alternative");
            Call(pending.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check(vehicle.BurnPlan.BurnCount == 0 && !(bool)type.GetProperty("IsExecuting").GetValue(pending.planner)
                && (string)type.GetProperty("Message").GetValue(pending.planner) == "transfer.better_route_ready",
                "Finding a better route starts a burn or creates an unsolicited replacement node");
            Call(pending.planner, "HandleAction", "transfer_cancel", vehicle, false);
            Check(!(bool)type.GetField("_replanPending", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pending.planner), "Cancelled retry can create a replacement node later");
            Console.WriteLine("PASS native unsafe candidate waits for the better-route action; new results require review and preserve the parking orbit");

            var preserved = Candidate(false);
            var correction = Burn.Create(parking.GetPointAt(new UniverseTime(400)), 400, new double3(1, 0, 0), vehicle.FlightPlan.Patches[0], vehicle);
            vehicle.FlightComputer.AddBurn(correction);
            preserved.candidate.FlightPlan = Prediction(parking, false); vehicle.BurnPlan.FlightPlansOutOfDate = false;
            Call(preserved.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check(vehicle.BurnPlan.BurnCount == 1 && ReferenceEquals(vehicle.BurnPlan.FirstBurn, correction)
                && !(bool)type.GetProperty("IsSearching").GetValue(preserved.planner), "Rejection removes or overwrites another maneuver node");
            var accepted = Candidate(true);
            accepted.candidate.FlightPlan = Prediction(escape, true); vehicle.BurnPlan.FlightPlansOutOfDate = false;
            Call(accepted.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check(!(bool)type.GetProperty("IsChecking").GetValue(accepted.planner)
                && (bool)type.GetProperty("IsExecuting").GetValue(accepted.planner)
                && vehicle.FlightComputer.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.PositiveDv,
                "A verified target encounter cannot proceed to alignment");
            Call(accepted.planner, "HandleAction", "transfer_stop", vehicle, false);
            Console.WriteLine("PASS native validation preserves other nodes and releases only verified transfers for execution");
            var missedAssist = Candidate(true);
            var expectedBody = New("TransferBodySnapshot", "Another Moon", MakeRequest().GetType().GetField("Target").GetValue(MakeRequest()),
                ((IParentBody)moon).Mu, moon.MeanRadius, moon.SphereOfInfluence, moon.MeanRadius);
            var expectedFlyby = New("TransferFlyby", expectedBody, 700.0, moon.MeanRadius + 100000);
            var assisted = New("TransferSolution", 300.0, 500000.0, Vector(new double3(7e6, 0, 0)),
                Vector(new double3(3200, 0, 0)), Vector(new double3(600, 0, 0)), 1000.0, true, false, expectedFlyby);
            Set(missedAssist.planner, "_checkingSolution", assisted);
            missedAssist.candidate.FlightPlan = Prediction(escape, true); vehicle.BurnPlan.FlightPlansOutOfDate = false;
            Call(missedAssist.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check(!vehicle.BurnPlan.TryGetBurn(missedAssist.candidate) && !(bool)type.GetProperty("IsExecuting").GetValue(missedAssist.planner)
                && (string)type.GetProperty("Message").GetValue(missedAssist.planner) == "transfer.assist_missed",
                "A safe direct encounter bypasses verification of the promised gravity assist");
            Console.WriteLine("PASS native maneuver validation removes and stops a node whose promised assist is missing");

            var approximate = Candidate(false, true);
            approximate.candidate.FlightPlan = Prediction(parking, false); vehicle.BurnPlan.FlightPlansOutOfDate = false;
            Call(approximate.planner, "UpdateExecution", vehicle, 0.0, true, false);
            Check(vehicle.BurnPlan.TryGetBurn(approximate.candidate) && !(bool)type.GetProperty("IsChecking").GetValue(approximate.planner)
                && (string)type.GetProperty("Message").GetValue(approximate.planner) == "transfer.fallback_created",
                "Best-effort transfer is deleted solely because it returns before an unconfirmed encounter");
            var recheck = type.GetMethod("RecheckDeparture", BindingFlags.Instance | BindingFlags.NonPublic);
            approximate.candidate.FlightPlan = Prediction(parking, false);
            Check((bool)recheck.Invoke(approximate.planner, new object[] { vehicle }), "Updated predictions reinstate the waived return restriction");
            approximate.candidate.FlightPlan = Prediction(escape, true, impact: true);
            Check(!(bool)recheck.Invoke(approximate.planner, new object[] { vehicle }) && !vehicle.BurnPlan.TryGetBurn(approximate.candidate),
                "Best-effort fallback also waives an actual predicted impact");
            Console.WriteLine("PASS native fallback retains and rechecks a returning transfer node while still rejecting impacts");

            {
                double lowRadius = moon.MeanRadius - 1000;
                var lowPeriapsis = Orbit.CreateFromStateCci(moon, new UniverseTime(3600), new double3(lowRadius, 0, 0),
                    new double3(0, Math.Sqrt(2 * ((IParentBody)moon).Mu / lowRadius + 900 * 900), 0), byte4.Zero);
                var lowState = lowPeriapsis.GetStateVectorsAt(new UniverseTime(0));
                var lowIncoming = Orbit.CreateFromStateCci(moon, new UniverseTime(0), lowState.PositionCci, lowState.VelocityCci, byte4.Zero);
                var lowArrival = Candidate(false);
                lowArrival.candidate.FlightPlan = Prediction(escape, true); vehicle.BurnPlan.FlightPlansOutOfDate = false;
                lowArrival.candidate.FlightPlan.Patches[1] = new PatchedConic(new UniverseTime(1000), new UniverseTime(10000),
                    PatchTransition.Encounter, PatchTransition.Final, lowIncoming, KeyHash.Zero);
                Check(Error(lowArrival.candidate.FlightPlan) == "transfer.invalid_periapsis", "Unsafe arrival periapsis loses the altitude cause needed for adjustment");
                Call(lowArrival.planner, "UpdateExecution", vehicle, 0.0, true, false);
                float before = (float)type.GetField("PeriapsisKm").GetValue(lowArrival.planner);
                float suggested = (float)type.GetProperty("SuggestedPeriapsisKm").GetValue(lowArrival.planner);
                Check(suggested > before && before == 100 && vehicle.BurnPlan.BurnCount == 0
                    && !(bool)type.GetProperty("IsSearching").GetValue(lowArrival.planner), "Unsafe arrival changes altitude before consent or offers no measured correction");
                Call(lowArrival.planner, "HandleAction", "transfer_improve", vehicle, false);
                Check((float)type.GetField("PeriapsisKm").GetValue(lowArrival.planner) == suggested
                    && (bool)type.GetProperty("IsSearching").GetValue(lowArrival.planner), "Approved search ignores the proposed arrival altitude");
                var adjustedRequest = type.GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lowArrival.planner);
                Check(Math.Abs((double)adjustedRequest.GetType().GetField("PeriapsisAltitude").GetValue(adjustedRequest) - suggested * 1000.0) < 0.01,
                    "Adjusted periapsis is shown in the UI but omitted from the actual transfer calculation");
                Call(lowArrival.planner, "HandleAction", "transfer_cancel", vehicle, false);
                Call(lowArrival.planner, "UpdateExecution", vehicle, 0.0, true, false);
                Check(!(bool)type.GetProperty("IsSearching").GetValue(lowArrival.planner) && vehicle.BurnPlan.BurnCount == 0,
                    "Cancelled improved-route search publishes or creates a node later");
            }
            Console.WriteLine("PASS native better-route consent applies measured arrival periapsis correction and cancellation creates no node");
        }
        finally { controllable.SetValue(null, original); Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false); }
    }

    private static void ContinuousCaptureChecks(Vehicle vehicle, IParentBody earth, Celestial moon, Orbit incoming)
    {
        var escaping = Orbit.CreateFromStateCci(earth, new UniverseTime(0), new double3(7e6, 0, 0), new double3(0, 12000, 0), byte4.Zero);
        var plan = new FlightPlan(escaping, KeyHash.Zero);
        plan.Patches[0].EndTime = new UniverseTime(1000);
        plan.Patches.Add(new PatchedConic(new UniverseTime(1000), new UniverseTime(10000), PatchTransition.Final, PatchTransition.Final, incoming, KeyHash.Zero));
        Set(vehicle, "FlightPlan", plan);
        Set(vehicle, "Body2Cce", doubleQuat.Identity); Set(vehicle, "BodyRates", double3.Zero);
        var fc = new FlightComputer { RollMode = FlightComputerRollMode.Decoupled, BurnMode = FlightComputerBurnMode.Auto };
        Set(vehicle, "FlightComputer", fc);
        var departure = Burn.Create(escaping.GetPointAt(new UniverseTime(-100)), -100, new double3(100, 0, 0), plan.Patches[0], vehicle);
        fc.AddBurn(departure);
        fc.BurnMode = FlightComputerBurnMode.Auto;
        fc.Burn = new BurnTarget { StartedAt = new UniverseTime(-100), DeltaVTargetCci = new float3(100, 0, 0), DeltaVAccumCci = new float3(100, 0, 0) };
        var planner = New("ManeuverPlanner"); var type = planner.GetType();
        Set(planner, "_vehicle", vehicle); Set(planner, "_executingVehicle", vehicle);
        Set(planner, "_captureVehicle", vehicle); Set(planner, "_captureTarget", moon); Set(planner, "Target", moon);
        Set(planner, "_captureDeparture", departure); Set(planner, "_node", departure); Set(planner, "_departureBurn", fc.Burn);
        Set(planner, "_burnWarpRequested", true); // The departure burn already consumed its warp.
        Set(planner, "_executionPhase", Enum.Parse(type.GetNestedType("ExecutionPhase", BindingFlags.NonPublic), "Burning"));
        var controllable = typeof(Vehicle).GetField("_overrideIsControllable", BindingFlags.NonPublic | BindingFlags.Static);
        bool original = (bool)controllable.GetValue(null); controllable.SetValue(null, true);
        var manual = typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.NonPublic | BindingFlags.Instance);
        bool Active() => (bool)type.GetProperty("IsExecuting").GetValue(planner);
        void Update(double now) => Call(planner, "UpdateExecution", vehicle, now, true, false);
        try
        {
            Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            Update(0);
            Check(Active() && (bool)type.GetProperty("IsCoasting").GetValue(planner) && escaping.IsUnbound()
                && fc.BurnMode == FlightComputerBurnMode.Manual, "Departure completion abandons capture on an unbound transfer");
            manual.SetValue(vehicle, new ManualControlInputs { EngineOn = true, EngineThrottle = 1, ThrusterCommandFlags = ThrusterMapFlags.TranslateForward });
            Update(1);
            Check(Active() && fc.BurnPlan.BurnCount == 1 && fc.BurnPlan.FirstBurn != departure,
                "Continuous transfer fails to replace departure with the predicted capture node");
            var cleared = (ManualControlInputs)manual.GetValue(vehicle);
            Check(Universe.IsAutoWarpActive && Math.Abs(Universe.AutoWarpTime.Value.Seconds() - 1001) < 0.01
                && !cleared.EngineOn && cleared.EngineThrottle == 0 && cleared.ThrusterCommandFlags == ThrusterMapFlags.None
                && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None,
                "Coast warp ignites engines, aligns for capture outside the SOI or misses the SOI boundary");
            Update(2);
            Check(fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None && Active(), "Capture points at the future target frame outside its SOI");
            Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            plan.Patches.RemoveAt(0);
            Update(1001);
            Check(incoming.IsUnbound() && Active() && !(bool)type.GetProperty("IsCoasting").GetValue(planner)
                && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.PositiveDv && fc.BurnMode == FlightComputerBurnMode.Manual,
                "Target SOI entry requires a bound orbit or fails to begin capture alignment");
            fc.Burn.IgnitionTime = new UniverseTime(1060); fc.Burn.BurnDuration = 120; fc.Burn.BurnBody2Cci = doubleQuat.Identity;
            Update(1002);
            Check(!Universe.IsAutoWarpActive && fc.BurnMode == FlightComputerBurnMode.Manual,
                "Capture warps despite an ignition estimate already within its final alignment minute");
            fc.Burn.IgnitionTime = new UniverseTime(3540); // The native duration estimate changes after a physics step.
            Update(1003);
            Check(Universe.IsAutoWarpActive && Math.Abs(Universe.AutoWarpTime.Value.Seconds() - 3480) < 0.01
                && fc.BurnMode == FlightComputerBurnMode.Manual,
                "Capture waiting on a revised ignition estimate fails to request its own warp after departure");
            Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false);
            Update(1004);
            Check(!Universe.IsAutoWarpActive, "Capture keeps restarting an already requested or interrupted warp");
            Update(3480); Update(3539); Update(3540);
            Check(Active() && fc.BurnMode == FlightComputerBurnMode.Auto
                && (string)type.GetProperty("Message").GetValue(planner) == "transfer.capturing", "Capture fails to ignite after stable SOI alignment");
            fc.Burn.StartedAt = new UniverseTime(3540); fc.Burn.DeltaVAccumCci = fc.Burn.DeltaVTargetCci;
            Update(3600);
            Check(!Active() && fc.BurnMode == FlightComputerBurnMode.Manual
                && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None
                && (string)type.GetProperty("Message").GetValue(planner) == "transfer.complete",
                "Capture completion leaves automatic engines enabled or restarts departure");

            var restored = New("ManeuverPlanner");
            var resumedFc = new FlightComputer(); Set(vehicle, "FlightComputer", resumedFc);
            Set(restored, "_vehicle", vehicle); Set(vehicle, "Target", moon);
            type.GetMethod("UpdateCapture", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restored, new object[] { vehicle, 1001.0 });
            Check(incoming.IsUnbound() && (bool)type.GetProperty("CanExecuteCapture").GetValue(restored),
                "A restored capture requires a bound parking orbit to execute");
            Call(restored, "HandleAction", "transfer_capture", vehicle, false);
            Check((bool)type.GetProperty("IsExecuting").GetValue(restored)
                && resumedFc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.PositiveDv
                && resumedFc.BurnMode == FlightComputerBurnMode.Manual,
                "Existing capture cannot start independently of a new transfer search");
            Call(restored, "HandleAction", "transfer_stop", vehicle, false);
            Check(!(bool)type.GetProperty("IsExecuting").GetValue(restored) && resumedFc.BurnMode == FlightComputerBurnMode.Manual,
                "Stopping recovered capture leaves automation active");
        }
        finally { controllable.SetValue(null, original); Universe.AutoWarpStop(true); Universe.SetSimulationSpeed(1, false); }
        Console.WriteLine("PASS native automatic transfer survives unbound coast, aligns only in target SOI and executes capture with safe warp");
        Console.WriteLine("PASS native recovered capture starts on an unbound arrival and honors Stop");
    }

    private static void PlannerChecks()
    {
        var body = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var native = Orbit.CreateFromStateCci(body, new UniverseTime(123), new double3(7e6, 0, 0), new double3(0, 7900, 1200), byte4.Zero);
        object Vector(double3 v) => New("LandingVector", v.X, v.Y, v.Z);
        var ephemeris = New("TransferOrbit", body.Mu, 123.0, Vector(new double3(7e6, 0, 0)), Vector(new double3(0, 7900, 1200)));
        var propagate = ephemeris.GetType().GetMethod("State");
        foreach (double seconds in new[] { 123.0, 900.0, 15000.0, 1e7 })
        {
            object[] values = { seconds, null, null };
            Check((bool)propagate.Invoke(ephemeris, values), "Pure transfer ephemeris failed");
            var state = native.GetStateVectorsAt(new UniverseTime(seconds));
            double3 ReadVector(object vector) => new double3((double)vector.GetType().GetField("X").GetValue(vector),
                (double)vector.GetType().GetField("Y").GetValue(vector), (double)vector.GetType().GetField("Z").GetValue(vector));
            Check((ReadVector(values[1]) - state.PositionCci).Length() < 0.05, "Transfer snapshot position differs from KSA ephemeris");
            Check((ReadVector(values[2]) - state.VelocityCci).Length() < 0.0001, "Transfer snapshot velocity differs from KSA ephemeris");
            var delta = new double3(1234, -56, 78);
            var vlf = OrbitalTransfers.CciToVlf(native, new UniverseTime(seconds), delta);
            var back = vlf.Transform(state.GetVlf2ParentCci().Value);
            Check((back - delta).Length() < 1e-6, "Native maneuver node vector conversion changes the planned impulse");
        }
        Console.WriteLine("PASS native transfer ephemeris agrees with KSA and maneuver vectors round-trip");

        var request = New("TransferRequest");
        Set(request, "Parking", New("TransferOrbit", body.Mu, 0.0, Vector(new double3(7e6, 0, 0)), Vector(new double3(0, Math.Sqrt(body.Mu / 7e6), 0))));
        Set(request, "Target", New("TransferOrbit", body.Mu, 0.0, Vector(new double3(0, 384.4e6, 0)), Vector(new double3(-Math.Sqrt(body.Mu / 384.4e6), 0, 0))));
        Set(request, "CentralMu", body.Mu); Set(request, "TargetMu", 4.9049e12); Set(request, "TargetRadius", 1.7374e6);
        Set(request, "PeriapsisAltitude", 100000.0); Set(request, "DepartureSafetyRadius", 6.5e6);
        double period = (double)request.GetType().GetField("Parking").GetValue(request).GetType().GetField("Period")
            .GetValue(request.GetType().GetField("Parking").GetValue(request));
        Set(request, "Earliest", 300.0); Set(request, "Latest", 300 + 2 * period); Set(request, "MinimumDuration", 180000.0); Set(request, "MaximumDuration", 750000.0);
        Set(request, "Width", 96); Set(request, "Height", 96);
        var search = Mod.GetType("KITN.TransferMath").GetMethod("Search");
        var grid = search.Invoke(null, new[] { request, (object)default(System.Threading.CancellationToken), null });
        var planner = New("ManeuverPlanner");
        Set(planner, "Grid", grid); Set(planner, "UnavailableReason", null); Set(planner, "OriginName", "Earth");
        var moon = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
        typeof(Astronomical).GetProperty("Id").SetValue(moon, "Moon"); moon.Orbit = native;
        var futureMoon = (TerrestrialBody)RuntimeHelpers.GetUninitializedObject(typeof(TerrestrialBody));
        typeof(Astronomical).GetProperty("Id").SetValue(futureMoon, "Mod Moon"); futureMoon.Orbit = native;
        var bodies = (System.Collections.IList)planner.GetType().GetField("Bodies").GetValue(planner);
        bodies.Add(moon); bodies.Add(futureMoon); Set(planner, "Target", moon);
        Call(planner, "SelectBest", false); Set(Window, "Planner", planner); Set(Window, "_tab", 5);
        Set(planner, "MaximumDv", 1f); Call(planner, "SelectBest", true);
        Check((int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner) == -1
            && !(bool)planner.GetType().GetProperty("CanCreate").GetValue(planner), "Impossible earliest budget retains an executable old selection");
        Set(planner, "MaximumDv", 12000f); Call(planner, "SelectBest", false);
        var page = Window.GetType().GetField("_transferPage", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
        if (CaptureOnly)
        {
            foreach (string locale in new[] { "en-US", "de-DE" })
            {
                Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
                foreach (bool small in new[] { false, true })
                    for (int mode = 0; mode < 3; mode++)
                    {
                        Set(page, "_mode", mode);
                        for (int frame = 0; frame < 60; frame++) Frame(true, small);
                        Save($"maneuver-{mode}-{locale}-{(small ? "minimum" : "normal")}.png", small ? 580 : 800, small ? 420 : 670, 12, 12);
                    }
            }
            Set(Window, "Planner", null); ((IDisposable)planner).Dispose();
            return;
        }
        Set(planner, "Grid", null); Set(planner, "SelectedIndex", -1);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Check(HasPlotSurface(), "Porkchop area disappears before a calculation");
        Save("maneuver-porkchop-empty.png", 580, 420, 12, 12);
        Check(Click(80, 318, "autopilot") == null, "Porkchop empty state still has a duplicate Calculate button");
        var completionType = typeof(System.Threading.Tasks.TaskCompletionSource<>).MakeGenericType(grid.GetType());
        var completion = Activator.CreateInstance(completionType);
        Set(planner, "_search", completionType.GetProperty("Task").GetValue(completion)); Set(planner, "_progress", 37);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Check(HasPlotSurface(), "Porkchop area disappears while calculating");
        Save("maneuver-porkchop-calculating.png", 580, 420, 12, 12);
        for (int frame = 0; frame < 60; frame++) Frame(); // Finish the intentional page transition before testing background stability.
        string stableChrome = PlannerChrome();
        for (int frame = 0; frame < 24; frame++)
        {
            Set(planner, "_progress", frame * 4); Frame();
            Check(HasPlotSurface() && PlannerChrome() == stableChrome, "Search progress flickers or moves the mod window, header or navigation");
        }
        Set(planner, "_search", null); Set(planner, "Grid", grid); Call(planner, "SelectBest", false);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("maneuver-porkchop.png", 800, 670, 12, 12);
        var previousPlan = Flight.GetType().GetProperty("LaunchPlan").GetValue(Flight);
        var displayedPlan = New("AdaptiveFlightPlan");
        Set(displayedPlan, "RequestedSettings", Mod.GetType("KITN.FlightPlanSettings").GetMethod("Capture").Invoke(null, new object[] { Profile }));
        Set(displayedPlan, "Success", true); Set(displayedPlan, "MessageKey", "planning.ready");
        Set(displayedPlan, "MessageArgs", new object[] { 2.5, 65.0, 8000.0 });
        Set(Flight, "LaunchPlan", displayedPlan);
        var launchCompletion = Activator.CreateInstance(typeof(System.Threading.Tasks.TaskCompletionSource<>).MakeGenericType(displayedPlan.GetType()));
        var launchTask = launchCompletion.GetType().GetProperty("Task").GetValue(launchCompletion);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        string chrome = PlannerChrome();
        uint pendingInk = ImGui.ColorConvertFloat4ToU32((float4)Mod.GetType("KITN.GameUiColors").GetProperty("Pending").GetValue(null));
        int PendingHeader() => DrawLists("/kitn-card-page-controls").SelectMany(l => l.VtxBuffer.Span.ToArray())
            .Count(vertex => vertex.col == pendingInk);
        int headerInk = PendingHeader();
        string flightPhase = (string)Window.GetType().GetMethod("GuidancePhase", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new[] { (object)Profile, Guidance, Flight });
        string completedMessage = (string)Call(Flight, "PlanningMessage", Profile);
        for (int frame = 0; frame < 24; frame++)
        {
            Set(Flight, "_planning", frame % 2 == 0 ? launchTask : null); Frame();
            Check(PlannerChrome() == chrome && PendingHeader() == headerInk, "Unrelated background flight planning flickers the transfer header or layout");
            Check((string)Call(Flight, "PlanningMessage", Profile) == completedMessage,
                "Background recalculation replaces the completed flight message with Planning");
            Check((string)Window.GetType().GetMethod("GuidancePhase", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new[] { (object)Profile, Guidance, Flight }) == flightPhase,
                "A completed flight preview flashes Planning while refreshing");
        }
        Set(Flight, "_planning", launchTask);
        Check(!(bool)Call(Flight, "CanLaunch", Profile), "Retaining the displayed preview bypasses pending launch verification");
        Set(Flight, "_planning", null); Set(Flight, "LaunchPlan", previousPlan);
        Console.WriteLine("PASS background flight preview preserves completed status and transfer header without bypassing launch verification");
        int oldSelection = (int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner);
        Click(395, 400, "autopilot");
        Check((int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner) != oldSelection, "Porkchop cell click did not change the selection");
        Check(Click(80, 639, "autopilot") == "transfer_create", "Create node does not reach the physics action queue");
        Check(Click(80, 197, "autopilot") == "transfer_search", "Calculate does not reach the physics action queue");
        ManualPorkchopChecks(planner, grid, request);
        Set(planner, "_search", completionType.GetProperty("Task").GetValue(completion));
        for (int frame = 0; frame < 3; frame++) Frame(true);
        stableChrome = PlannerChrome();
        for (int frame = 0; frame < 24; frame++)
        {
            Set(planner, "_progress", frame * 4); Frame();
            Check(PlannerChrome() == stableChrome && ReferenceEquals(planner.GetType().GetProperty("Grid").GetValue(planner), grid),
                "Recalculation clears the displayed plot or makes the UI chrome flicker");
        }
        Set(planner, "_search", null);
        planner.GetType().GetMethod("OfferBetterRoute", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, new object[] { "transfer.returning", null });
        Set(planner, "SelectedIndex", -1);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("maneuver-better-route-minimum.png", 580, 420, 12, 12);
        Check(Click(80, 197, "autopilot") == "transfer_improve", "Better-route confirmation button is unavailable at minimum window size");
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("maneuver-better-route.png", 800, 670, 12, 12);
        planner.GetType().GetMethod("ClearRouteProblem", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(planner, null);
        Call(planner, "SelectBest", false);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        ToggleSearchOptions();
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Check(HasVisiblePlot(), "Expanded search options push the Porkchop out of the minimum window");
        Save("maneuver-porkchop-expanded-minimum.png", 580, 420, 12, 12);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        ToggleSearchOptions();
        Set(page, "_mode", 2); Call(planner, "SelectBest", true);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("maneuver-earliest.png", 800, 670, 12, 12);
        Set(page, "_mode", 1); Call(planner, "SelectBest", false);
        for (int frame = 0; frame < 3; frame++) Frame(true, true);
        Save("maneuver-minimum.png", 580, 420, 12, 12);
        var l10n = Mod.GetType("KITN.L10n"); l10n.GetMethod("SetLocale").Invoke(null, new object[] { "de-DE" });
        Set(page, "_mode", 0);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("maneuver-german.png", 800, 670, 12, 12);
        Click(500, 131, "autopilot");
        Save("maneuver-body-dropdown.png", 800, 670, 12, 12);
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.DownArrow, true); Frame(); io.AddKeyEvent(ImGuiKey.DownArrow, false); Frame();
        io.AddKeyEvent(ImGuiKey.Enter, true); Frame(); io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
        Check(ReferenceEquals(planner.GetType().GetProperty("Target").GetValue(planner), futureMoon), "Body dropdown cannot select a future mod moon");
        Console.WriteLine("PASS native maneuver page renders all modes, mod-body dropdown and clickable Porkchop");
        Console.WriteLine("PASS Porkchop remains visible before/during calculation and above expanded options at minimum size");
        Console.WriteLine("PASS better-route button is reachable and both instrument and main window chrome stay stable across search progress");
        PorkchopUiRegressionChecks(page, planner, request, grid);
        GravityAssistToggleChecks(page, planner, grid);
        var oberthVehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        Set(oberthVehicle, "FlightPlan", new FlightPlan(native, KeyHash.Zero)); Set(planner, "_vehicle", oberthVehicle);
        Set(page, "_mode", 1); Set(planner, "UnavailableReason", null); Set(planner, "Grid", grid);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("maneuver-oberth-button.png", 800, 670, 12, 12);
        Check(Click(130, 197, "autopilot") == "transfer_oberth", "Lowest delta-v Oberth button does not enqueue its dedicated search action");
        LandingSettingsUiChecks();
        Set(Window, "Planner", null); Set(Window, "_tab", 0); ((IDisposable)planner).Dispose();
    }

    private static void PorkchopUiRegressionChecks(object page, object planner, object originalRequest, object originalGrid)
    {
        var request = Call(originalRequest, "CopyForPorkchop"); Set(request, "Oberth", true);
        double mu = (double)request.GetType().GetField("CentralMu").GetValue(request);
        var parking = New("TransferOrbit", mu, 0.0, New("LandingVector", 7e6, 0.0, 0.0), New("LandingVector", 0.0, Math.Sqrt(mu * 1.55 / 7e6), 0.0));
        Set(request, "Parking", parking); Set(request, "Latest", 3 * (double)parking.GetType().GetField("Period").GetValue(parking));
        Set(request, "Width", 24); Set(request, "Height", 24);
        var overview = Call(request, "CopyForPorkchop");
        var result = Mod.GetType("KITN.TransferMath").GetMethod("SearchWithPorkchop").Invoke(null,
            new[] { request, overview, (object)default(System.Threading.CancellationToken), null });
        var plotGrid = result.GetType().GetField("PorkchopGrid").GetValue(result);
        Set(planner, "Grid", result); Call(planner, "SelectBest", false); Set(page, "_mode", 1);
        var selected = planner.GetType().GetProperty("Selected").GetValue(planner);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Click(110, 162, "autopilot");
        Check((int)page.GetType().GetField("_mode", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(page) == 0
            && ReferenceEquals(planner.GetType().GetProperty("Grid").GetValue(planner), result)
            && selected.Equals(planner.GetType().GetProperty("Selected").GetValue(planner)), "Switching to Porkchop loses the selected Oberth route");
        Check(ReferenceEquals(page.GetType().GetField("_plotGrid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(page), plotGrid),
            "Oberth → Porkchop displays the repeated periapsis grid instead of the cached full-time overview");
        Frame(); Save("porkchop-after-oberth.png", 800, 670, 12, 12);
        uint accent = (uint)Mod.GetType("KITN.GameUiColors").GetProperty("AccentU32").GetValue(null);
        Check((accent & 255) > 180 && (accent >> 8 & 255) > 120 && (accent >> 16 & 255) < 120,
            "Action accent is not the game's yellow color");
        Check(DrawLists("/kitn-card-page-controls").SelectMany(l => l.VtxBuffer.Span.ToArray()).Count(v => v.col == accent && v.pos.Y > 180 && v.pos.Y < 215) >= 4,
            "Calculate lost its yellow primary-button background");
        var cells = (Array)plotGrid.GetType().GetField("Cells").GetValue(plotGrid);
        int width = (int)overview.GetType().GetField("Width").GetValue(overview), height = (int)overview.GetType().GetField("Height").GetValue(overview);
        float2 Point(int index)
        {
            var rect = PlotRectangle();
            return new float2(rect.min.X + (rect.max.X - rect.min.X) * (index % width) / (width - 1),
                rect.max.Y - (rect.max.Y - rect.min.Y) * (index / width) / (height - 1));
        }
        int valid = Enumerable.Range(0, cells.Length).First(i => (bool)cells.GetValue(i).GetType().GetField("Valid").GetValue(cells.GetValue(i))
            && i % width > 0 && i % width < width - 1 && i / width > 0 && i / width < height - 1);
        int invalid = Enumerable.Range(0, cells.Length).FirstOrDefault(i => !(bool)cells.GetValue(i).GetType().GetField("Valid").GetValue(cells.GetValue(i))
            && i % width > 0 && i % width < width - 1 && i / width > 0 && i / width < height - 1, -1);
        if (invalid >= 0)
        {
            var unsafePoint = Point(invalid); Click(unsafePoint.X, unsafePoint.Y, "autopilot");
            Check(ReferenceEquals(planner.GetType().GetProperty("Grid").GetValue(planner), result)
                && selected.Equals(planner.GetType().GetProperty("Selected").GetValue(planner)), "An invalid overview cell replaces the safe selected route");
        }
        var point = Point(valid); Click(point.X, point.Y, "autopilot");
        Check(ReferenceEquals(planner.GetType().GetProperty("Grid").GetValue(planner), plotGrid)
            && cells.GetValue(valid).Equals(planner.GetType().GetProperty("Selected").GetValue(planner)), "Plot click selects a snapped Oberth cell instead of the displayed route");
        Set(planner, "Grid", originalGrid); Call(planner, "SelectBest", false);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        ToggleSearchOptions();
        ImGui.GetIO().AddMousePosEvent(-1000, -1000); Frame();
        var before = PlotRectangle(); float plotHeight = before.max.Y - before.min.Y;
        Check(DrawLists("/kitn-navigation").Length == 1 && DrawLists("/kitn-card-page-controls").Length == 1,
            "Navigation and page controls do not have independent fixed regions");
        string fixedHeader = PlannerChrome();
        ImGui.GetIO().AddMousePosEvent(300, 380); Frame();
        ImGui.GetIO().AddMouseWheelEvent(0, -1);
        for (int frame = 0; frame < 4; frame++) Frame();
        var after = PlotRectangle();
        Console.WriteLine($"  plot scroll: before={before.min}/{before.max}, after={after.min}/{after.max}");
        Save("porkchop-scroll-regression.png", 800, 800, 12, 12);
        Check(after.min.Y < before.min.Y - 10 && Math.Abs(after.max.Y - after.min.Y - plotHeight) < 1,
            "Scrolling resizes the Porkchop and changes hovered coordinates");
        Check(PlannerChrome() == fixedHeader, "Scrolling content moves or repaints fixed navigation and page controls");
        for (int frame = 0; frame < 16; frame++)
        {
            ImGui.GetIO().AddMousePosEvent(after.min.X + (after.max.X - after.min.X) * (0.2f + (frame % 5) * 0.1f),
                Math.Max(225, after.min.Y + (after.max.Y - after.min.Y) * (0.25f + (frame % 3) * 0.2f)));
            Frame(); Frame(); // Native tooltips measure their window on the first hover frame.
            var tooltip = TooltipRectangle();
            Check(tooltip.max.X - tooltip.min.X > 320 && tooltip.max.X - tooltip.min.X < 360
                && tooltip.max.Y - tooltip.min.Y < 350 && tooltip.min.Y >= 0 && tooltip.max.Y <= ImGui.GetIO().DisplaySize.Y,
                "Scrolled Porkchop tooltip collapses, becomes a vertical strip or leaves the viewport");
        }
        Save("porkchop-scrolled-tooltip.png", 800, 800, 12, 12);
        ImGui.GetIO().AddMousePosEvent(300, 300); ImGui.GetIO().AddMouseWheelEvent(0, 20);
        for (int frame = 0; frame < 4; frame++) Frame();
        ImGui.GetIO().AddMousePosEvent(-1000, -1000); Frame();
        ToggleSearchOptions();
        Console.WriteLine("PASS cached Oberth → Porkchop switch, exact plot selection, yellow actions and stable tooltip/plot geometry while scrolling");
        Console.WriteLine("PASS navigation and page controls stay fixed while body content scrolls");
    }

    private static unsafe (float2 min, float2 max) TooltipRectangle()
    {
        var list = ImGui.GetDrawData().CmdLists.Span.ToArray().LastOrDefault(l => Marshal.PtrToStringUTF8((IntPtr)l._OwnerName)?.Contains("Tooltip") == true);
        Check(!list.IsNull(), "Porkchop hover tooltip is missing");
        var points = list.VtxBuffer.Span.ToArray().Select(v => v.pos).ToArray();
        return (new float2(points.Min(p => p.X), points.Min(p => p.Y)), new float2(points.Max(p => p.X), points.Max(p => p.Y)));
    }

    private static void LandingSettingsUiChecks()
    {
        var landing = New("LandingController"); var draw = Mod.GetType("KITN.LandingSettingsPage").GetMethod("Draw");
        Set(landing, "AutoWarp", false);
        Set(landing.GetType().GetField("Settings").GetValue(landing), "AutoWarp", false);
        bool locked = false; float2 reset = default;
        void DrawFrame()
        {
            ImGui.NewFrame(); bool open = true;
            ImGui.SetNextWindowPos(new float2(20, 20), ImGuiCond.Always); ImGui.SetNextWindowSize(new float2(780, 650), ImGuiCond.Always);
            ImGui.SetNextWindowFocus(); ImGui.Begin("Landing settings test", ref open, ImGuiWindowFlags.NoSavedSettings);
            ImGui.SetNextItemOpen(true, ImGuiCond.Always); draw.Invoke(null, new[] { landing, Store, (object)locked });
            var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); reset = new float2(100, (min.Y + max.Y) / 2);
            ImGui.End(); ImGui.Render();
        }
        var io = ImGui.GetIO();
        for (int frame = 0; frame < 3; frame++) DrawFrame(); Save("landing-settings.png", 800, 670, 12, 12);
        io.AddMousePosEvent(710, 80); DrawFrame(); io.AddMouseButtonEvent(0, true); DrawFrame(); io.AddMouseButtonEvent(0, false); DrawFrame();
        var settings = landing.GetType().GetField("Settings").GetValue(landing);
        float changed = (float)settings.GetType().GetProperty("TouchdownSpeedMps").GetValue(settings);
        Check(changed > 0.4f && Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "Landing descent-speed slider does not update or save");
        locked = true; io.AddMousePosEvent(630, 80); DrawFrame(); io.AddMouseButtonEvent(0, true); DrawFrame(); io.AddMouseButtonEvent(0, false); DrawFrame();
        Check((float)landing.GetType().GetField("Settings").GetValue(landing).GetType().GetProperty("TouchdownSpeedMps")
            .GetValue(landing.GetType().GetField("Settings").GetValue(landing)) == changed, "Active landing settings can be changed midway through guidance");
        locked = false; DrawFrame(); io.AddMousePosEvent(reset.X, reset.Y); DrawFrame(); io.AddMouseButtonEvent(0, true); DrawFrame(); io.AddMouseButtonEvent(0, false); DrawFrame();
        settings = landing.GetType().GetField("Settings").GetValue(landing);
        Check((bool)Call(settings, "Matches", New("LandingSettings")), "Reset landing settings does not restore defaults");
        io.AddMousePosEvent(-1000, -1000); Frame();
        Console.WriteLine("PASS native landing settings are editable, saved, locked during automation and resettable");
    }

    private static void GravityAssistToggleChecks(object page, object planner, object grid)
    {
        var options = page.GetType().GetMethod("DrawSearchOptions", BindingFlags.NonPublic | BindingFlags.Instance);
        Set(page, "Settings", Store); Set(planner, "GravityAssists", false); Set(planner, "Grid", grid);
        Call(Store, "SetGravityAssists", false, Environment.TickCount64);
        var io = ImGui.GetIO(); float2 toggle = default;
        void OptionsFrame()
        {
            ImGui.NewFrame(); bool open = true;
            ImGui.SetNextWindowPos(new float2(20, 20), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new float2(780, 650), ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
            ImGui.Begin("Transfer options test", ref open, ImGuiWindowFlags.NoSavedSettings);
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            options.Invoke(page, new object[] { planner, false });
            var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
            toggle = new float2(ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 55, (min.Y + max.Y) / 2);
            ImGui.End(); ImGui.Render();
        }
        for (int frame = 0; frame < 3; frame++) OptionsFrame();
        Save("maneuver-gravity-assist-options.png", 800, 670, 12, 12);
        io.AddMousePosEvent(toggle.X, toggle.Y); OptionsFrame();
        io.AddMouseButtonEvent(0, true); OptionsFrame(); io.AddMouseButtonEvent(0, false); OptionsFrame();
        Check((bool)planner.GetType().GetField("GravityAssists").GetValue(planner)
            && (bool)Store.GetType().GetProperty("GravityAssists").GetValue(Store)
            && planner.GetType().GetProperty("Grid").GetValue(planner) == null,
            "Transfer options gravity toggle is unclickable, unsaved or keeps stale direct-only results");
        Call(Store, "Flush", Environment.TickCount64, true);
        string path = (string)Store.GetType().GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Store);
        var restart = New("UiLayoutStore", path); Call(restart, "Load");
        Check((bool)restart.GetType().GetProperty("GravityAssists").GetValue(restart), "Clicked gravity-assist setting is lost on restart");
        io.AddMousePosEvent(-1000, -1000); Frame();
        Console.WriteLine("PASS native gravity-assist toggle changes calculation settings, invalidates stale routes and survives restart");
    }

    private static void ManualPorkchopChecks(object planner, object original, object request)
    {
        var grid = New("TransferGrid", request);
        var cells = (Array)grid.GetType().GetField("Cells").GetValue(grid);
        ((Array)original.GetType().GetField("Cells").GetValue(original)).CopyTo(cells, 0);
        int width = (int)request.GetType().GetField("Width").GetValue(request);
        int height = (int)request.GetType().GetField("Height").GetValue(request);
        double first = (double)request.GetType().GetField("Earliest").GetValue(request);
        double last = (double)request.GetType().GetField("Latest").GetValue(request);
        double minimum = (double)request.GetType().GetField("MinimumDuration").GetValue(request);
        double maximum = (double)request.GetType().GetField("MaximumDuration").GetValue(request);
        const int column = 50;
        for (int row = 0; row <= 12; row++)
            for (int col = column; col <= column + 1; col++)
                cells.SetValue(New("TransferSolution", first + (last - first) * col / (width - 1), minimum + (maximum - minimum) * row / (height - 1),
                    New("LandingVector", 7e6, 0.0, 0.0), New("LandingVector", 1000.0, 0.0, 0.0), New("LandingVector", 0.0, 0.0, 0.0), 0.0, true), row * width + col);
        cells.SetValue(New("TransferSolution"), 6 * width + column);
        Set(planner, "Grid", grid); Set(planner, "_routeProblem", "transfer.returning");
        Call(planner, "Select", column);
        Check((int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner) == column
            && !(bool)planner.GetType().GetProperty("NeedsBetterRoute").GetValue(planner),
            "Manual selection at the shortest-flight row is remapped to a bound cell or blocked after another failed route");
        for (int frame = 0; frame < 3; frame++) Frame(true);
        float2 Point(int row)
        {
            var bounds = PlotRectangle();
            return new float2(bounds.min.X + (bounds.max.X - bounds.min.X) * column / (width - 1),
                bounds.max.Y - (bounds.max.Y - bounds.min.Y) * row / (height - 1));
        }
        float2 bottom = Point(0);
        Call(planner, "Select", 12 * width + column);
        Click(bottom.X, bottom.Y, "autopilot");
        Check((int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner) == column, "Clicking the bottom plot border cannot select the shortest flight time");
        var io = ImGui.GetIO();
        float2 start = Point(12); io.AddMousePosEvent(start.X, start.Y); Frame();
        io.AddMouseButtonEvent(0, true); Frame();
        for (int row = 11; row >= 0; row--)
        {
            float2 point = Point(row); io.AddMousePosEvent(point.X, point.Y); Frame();
        }
        io.AddMouseButtonEvent(0, false); Frame();
        Check((int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner) == column,
            "Dragging downward snaps the manual selection away from the requested escape cell");
        Save("maneuver-manual-shortest-flight.png", 800, 670, 12, 12);
        Call(planner, "Select", 12 * width + column);
        float2 blocked = Point(6);
        Click(blocked.X, blocked.Y, "autopilot");
        Check((int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner) == 12 * width + column,
            "Filled unsafe plot region becomes selectable");
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Save("maneuver-unsafe-regions.png", 800, 670, 12, 12);
        var exclusions = (System.Collections.IList)planner.GetType().GetField("_excludedWindows", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(planner);
        exclusions.Add(New("TransferWindowExclusion", first + (last - first) * column / (width - 1), minimum, 0.1, 0.1));
        Call(planner, "Select", 12 * width + column); Call(planner, "Select", column);
        Check((int)planner.GetType().GetProperty("SelectedIndex").GetValue(planner) == 12 * width + column
            && (string)planner.GetType().GetProperty("Message").GetValue(planner) == "transfer.rejected_window", "Manual choice bypasses a known failed native trajectory");
        exclusions.Clear(); Set(planner, "Grid", original); Call(planner, "SelectBest", false);
        object Vector(double x, double y, double z) => New("LandingVector", x, y, z);
        double refinedDeparture = first + (last - first) * 0.3751, refinedDuration = minimum + (maximum - minimum) * 0.5829;
        var optimized = New("TransferSolution", refinedDeparture, refinedDuration, Vector(7e6, 0, 0), Vector(1, 0, 0), Vector(100, 0, 0), 0.0, false);
        var candidates = (System.Collections.IList)original.GetType().GetField("Candidates").GetValue(original);
        candidates.Add(optimized); Call(planner, "SelectBest", false);
        var selection = planner.GetType().GetProperty("Selected").GetValue(planner);
        Check((double)selection.GetType().GetField("Departure").GetValue(selection) == refinedDeparture,
            "Optimized minimum is rounded back to the nearest cell before node creation");
        Call(planner, "Select", 12 * width + column);
        selection = planner.GetType().GetProperty("Selected").GetValue(planner);
        Check((double)selection.GetType().GetField("Departure").GetValue(selection) != refinedDeparture,
            "Manual selection still executes the previous optimized minimum");
        candidates.Clear(); Call(planner, "SelectBest", false);
        for (int frame = 0; frame < 3; frame++) Frame(true);
        Console.WriteLine("PASS native Porkchop click and drag select the exact shortest-flight escape cell, while failed windows remain blocked");
    }

    private static (float2 min, float2 max) PlotRectangle()
    {
        uint background = ImGui.ColorConvertFloat4ToU32(new float4(0.09f, 0.12f, 0.15f, 1));
        var points = ImGui.GetDrawData().CmdLists.Span.ToArray().SelectMany(list => list.VtxBuffer.Span.ToArray())
            .Where(vertex => vertex.col == background).Select(vertex => vertex.pos).ToArray();
        Check(points.Length >= 4, "Native plot background is missing");
        return (new float2(points.Min(point => point.X), points.Min(point => point.Y)),
            new float2(points.Max(point => point.X), points.Max(point => point.Y)));
    }

    private static unsafe bool HasDrawList(string token)
    {
        foreach (var list in ImGui.GetDrawData().CmdLists.Span)
            if (Marshal.PtrToStringUTF8((IntPtr)list._OwnerName)?.Contains(token, StringComparison.Ordinal) == true) return true;
        return false;
    }

    private static bool HasVisiblePlot()
    {
        foreach (var list in ImGui.GetDrawData().CmdLists.Span)
            foreach (var vertex in list.VtxBuffer.Span)
                if ((vertex.col >> 16 & 255) > 110 && (vertex.col >> 16 & 255) > (vertex.col & 255) * 1.4
                    && (vertex.col >> 8 & 255) > 50 && vertex.pos.X >= 20 && vertex.pos.X < 580 && vertex.pos.Y >= 160 && vertex.pos.Y < 400) return true;
        return false;
    }

    private static bool HasPlotSurface()
    {
        uint guide = ImGui.ColorConvertFloat4ToU32(new float4(1, 1, 1, 0.05f));
        return ImGui.GetDrawData().CmdLists.Span.ToArray().Any(list => list.VtxBuffer.Span.ToArray()
            .Count(vertex => vertex.col == guide && vertex.pos.Y >= 240 && vertex.pos.Y < 400) >= 8);
    }

    private static unsafe ImDrawListPtr[] DrawLists(string token) => ImGui.GetDrawData().CmdLists.Span.ToArray()
        .Where(list => Marshal.PtrToStringUTF8((IntPtr)list._OwnerName)?.Contains(token, StringComparison.Ordinal) == true).ToArray();

    private static string PlannerChrome()
    {
        string Chrome(string id) => string.Join(";", WindowDrawList(id).VtxBuffer.Span.ToArray()
            .Where(vertex => vertex.col == ConsoleStyle.PanelU32 || vertex.col == ConsoleStyle.ActiveToolU32)
            .Select(vertex => $"{vertex.pos.X:R},{vertex.pos.Y:R},{vertex.col}"));
        string Fixed(string token) => string.Join(";", DrawLists(token).SelectMany(list => list.VtxBuffer.Span.ToArray())
            .Select(vertex => $"{vertex.pos.X:R},{vertex.pos.Y:R},{vertex.col}"));
        return Chrome("autopilot") + "|" + Chrome("mission_time") + "|" + Fixed("/kitn-navigation") + "|" + Fixed("/kitn-card-page-controls");
    }

    private static void Frame(bool place = false, bool small = false)
    {
        ImGui.NewFrame();
        if (place) ImGui.GetIO().AddMousePosEvent(-1000, -1000);
        if (Focus == "autopilot") ImGui.SetNextWindowFocus();
        if (place)
        {
            ImGui.SetNextWindowPos(new float2(20, 20), ImGuiCond.Always);
            ImGui.SetWindowSize("KITN###KITN", small ? new float2(560, 400) : new float2(780, 650), ImGuiCond.Always);
        }
        MainAction = (string)Call(Window, "Draw", true, Profile, Guidance, Flight, Clock, true, false, Landing);
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

    private static void Drag(string id, bool alt, float dx, float dy, bool body = false, ImGuiKey altKey = ImGuiKey.Mod_Alt)
    {
        var io = ImGui.GetIO();
        Focus = id; Frame(); Focus = null;
        object before = Call(Store, "Find", id);
        float x = Value(before, "X"), y = Value(before, "Y"), width = Value(before, "Width"), height = Value(before, "Height");
        if (alt) { dx = 800 - width / 2 - x + 5; dy = 500 - height / 2 - y + 4; }
        float startX = x + 100, startY = y + (body ? height * 0.7f : 10);
        for (int frame = 0; frame < 23; frame++)
        {
            float progress = Math.Clamp((frame - 6) / 8f, 0, 1);
            io.AddMousePosEvent(startX + dx * progress, startY + dy * progress);
            if (frame == 4) { io.AddKeyEvent(altKey, alt); io.AddMouseButtonEvent(0, true); }
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
        io.AddKeyEvent(altKey, false); Frame();
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

    private static void ScrollBody(float amount)
    {
        Focus = "autopilot"; Frame(); Focus = null;
        ImGui.GetIO().AddMousePosEvent(420, 355); Frame();
        ImGui.GetIO().AddMouseWheelEvent(0, amount);
        for (int frame = 0; frame < 5; frame++) Frame();
        ImGui.GetIO().AddMousePosEvent(-1000, -1000); Frame();
    }

    private static void ToggleSearchOptions()
    {
        ScrollBody(-20);
        var header = DrawLists("/kitn-card-transfer-options").SelectMany(l => l.VtxBuffer.Span.ToArray()).Take(4).Select(v => v.pos).ToArray();
        Check(header.Length == 4, "Search options header is missing");
        Click((header.Min(p => p.X) + header.Max(p => p.X)) / 2, (header.Min(p => p.Y) + header.Max(p => p.Y)) / 2, "autopilot");
        ScrollBody(20);
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

