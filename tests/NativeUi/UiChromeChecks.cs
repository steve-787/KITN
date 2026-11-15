using System;
using System.Linq;
using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KITN;
using KSA;

internal static partial class Program
{
    private static void HeaderWidgetChecks()
    {
        bool Flag(string name) => (bool)Window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window);
        float2 HeaderPoint(int index)
        {
            object bounds = Call(Store, "Find", "autopilot");
            float scale = ConsoleStyle.EffectiveInterfaceScale;
            float start = 60 * scale;
            return new float2(Value(bounds, "X") + 16 * scale + start + (index * 32 + 15) * scale, Value(bounds, "Y") + 31 * scale);
        }
        void SettingsClick()
        {
            object bounds = Call(Store, "Find", "autopilot");
            Click(Value(bounds, "X") + Value(bounds, "Width") - 63 * ConsoleStyle.EffectiveInterfaceScale,
                Value(bounds, "Y") + 31 * ConsoleStyle.EffectiveInterfaceScale, "autopilot");
            Check(Flag("_settingsOpen"), "Settings is obscured by header navigation");
            for (int frame = 0; frame < 75; frame++) Frame();
        }
        foreach (bool small in new[] { false, true })
        foreach (string locale in new[] { "en-US", "de-DE" })
        {
            Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { locale });
            Frame(true, small);
            int[] expectedPages = { -1, 0, 1, 2, 3, 6, 4, 5, 7 };
            for (int page = 0; page < expectedPages.Length; page++)
            {
                SettingsClick();
                float2 point = HeaderPoint(page); Click(point.X, point.Y, "autopilot");
                Check(page == 0 ? Flag("_homeOpen") && !Flag("_settingsOpen")
                    : expectedPages[page] == 7 ? Flag("_diagnosticsOpen")
                    : !Flag("_homeOpen") && !Flag("_settingsOpen") && (int)Window.GetType().GetField("_tab", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window) == expectedPages[page],
                    "Combined header cannot select page " + page + " in " + locale);
                for (int frame = 0; frame < 45; frame++) Frame();
                Check(ImGui.GetDrawData().TotalVtxCount > 1000, "Page content did not render under the custom header");
                Check(DrawLists("/kitn-navigation").Length == 0, "The old navigation container still occupies a second row");
                ImGui.GetIO().AddMousePosEvent(-1000, -1000); Frame(); Frame();
                Save($"header-{page}-{locale}-{(small ? "small" : "normal")}.png", small ? 580 : 800, small ? 420 : 670, 12, 12);
            }
            SettingsClick();
            float2 home = HeaderPoint(0); ImGui.GetIO().AddMousePosEvent(home.X, home.Y);
            for (int frame = 0; frame < 45; frame++) Frame();
            Check(HasDrawList("Tooltip"), "Header icon tooltip is missing");
        }
        Mod.GetType("KITN.L10n").GetMethod("SetLocale").Invoke(null, new object[] { "en-US" });
        Frame(true);
        // Exercise the actual title drag handle and the existing Alt alignment behavior.
        float scaleNow = ConsoleStyle.EffectiveInterfaceScale;
        var io = ImGui.GetIO();
        object titleBefore = Call(Store, "Find", "autopilot");
        float2 title = new float2(Value(titleBefore, "X") + 60 * scaleNow, Value(titleBefore, "Y") + 25 * scaleNow);
        for (int frame = 0; frame < 18; frame++)
        {
            float amount = Math.Clamp((frame - 4) / 8f, 0, 1);
            io.AddMousePosEvent(title.X + 80 * amount, title.Y + 55 * amount);
            if (frame == 2) io.AddMouseButtonEvent(0, true);
            if (frame == 15) io.AddMouseButtonEvent(0, false);
            Frame();
        }
        object titleAfter = Call(Store, "Find", "autopilot");
        Check(Math.Abs(Value(titleAfter, "X") - Value(titleBefore, "X") - 80) < 1
            && Math.Abs(Value(titleAfter, "Y") - Value(titleBefore, "Y") - 55) < 1, "Custom title dragging loses its click offset");
        bool closed = false;
        float2 close = new float2(Value(titleAfter, "X") + Value(titleAfter, "Width") - 31 * scaleNow, Value(titleAfter, "Y") + 31 * scaleNow);
        void CloseFrame()
        {
            ImGui.NewFrame();
            object[] arguments = { true, Profile, Guidance, Flight, Clock, true, false, Landing };
            Call(Window, "Draw", arguments); closed |= !(bool)arguments[0];
            ImGui.Render();
        }
        io.AddMousePosEvent(close.X, close.Y); CloseFrame(); io.AddMouseButtonEvent(0, true); CloseFrame(); io.AddMouseButtonEvent(0, false); CloseFrame();
        Check(closed, "Custom X does not minimize the app");

        object widget = New("MinimizedWindow", Store);
        object widgetLayout = widget.GetType().GetProperty("Layout").GetValue(widget);
        Call(widget, "PlaceAt", titleAfter);
        object planner = New("ManeuverPlanner"), rendezvous = New("RendezvousController");
        object ReadActivity(bool pending = false, bool instrumentsEnabled = false) => Mod.GetType("KITN.ModActivity").GetMethod("Read").Invoke(null,
            new object[] { Guidance, Flight, Clock, Landing, rendezvous, planner, pending, instrumentsEnabled });
        bool Active(object activity) => (bool)activity.GetType().GetField("Active").GetValue(activity);
        object activity = ReadActivity();
        Check(!Active(activity), "An elapsed mission clock is incorrectly reported as current activity");
        bool WidgetFrame()
        {
            ImGui.NewFrame(); bool restore = (bool)Call(widget, "Draw", activity); ImGui.Render(); return restore;
        }
        for (int frame = 0; frame < 3; frame++) WidgetFrame();
        object collapsed = Call(Store, "Find", "minimized_icon");
        Check(Value(collapsed, "X") == Value(titleAfter, "X") && Value(collapsed, "Y") == Value(titleAfter, "Y"), "Minimized K does not retain the app position");
        Check(Value(collapsed, "Width") == 40 * scaleNow && Value(collapsed, "Height") == 40 * scaleNow, "Idle widget has an unexpected size");
        Save("minimized-idle.png", 72, 72, (int)Value(collapsed, "X") - 16, (int)Value(collapsed, "Y") - 16);
        float2 start = new float2(Value(collapsed, "X") + 20 * scaleNow, Value(collapsed, "Y") + 20 * scaleNow);
        bool reopened = false;
        for (int frame = 0; frame < 22; frame++)
        {
            float amount = Math.Clamp((frame - 5) / 8f, 0, 1);
            io.AddMousePosEvent(start.X + 120 * amount, start.Y + 90 * amount);
            if (frame == 3) io.AddMouseButtonEvent(0, true);
            if (frame == 17) io.AddMouseButtonEvent(0, false);
            reopened |= WidgetFrame();
        }
        object moved = Call(Store, "Find", "minimized_icon");
        Check(!reopened && Math.Abs(Value(moved, "X") - Value(collapsed, "X") - 120) < 1
            && Math.Abs(Value(moved, "Y") - Value(collapsed, "Y") - 90) < 1, "Dragging the minimized icon reopens it or loses placement");
        Call(Clock, "BeginCountdown", 10, 300.0); activity = ReadActivity();
        Check(Active(activity), "Countdown is missing from the activity widget"); WidgetFrame();
        object countdownBounds = Call(Store, "Find", "minimized_activity");
        Check(Value(countdownBounds, "Width") == 280 * scaleNow && Value(countdownBounds, "Height") == 108 * scaleNow
            && Value(countdownBounds, "X") == Value(titleAfter, "X") && Value(countdownBounds, "Y") == Value(titleAfter, "Y"), "First activity appearance does not start at the main window position");
        object clockHidden = ReadActivity(instrumentsEnabled: true);
        Check(!((string)clockHidden.GetType().GetField("Phase").GetValue(clockHidden)).Contains("T-"), "The enabled mission widget leaves a duplicate countdown clock");
        io.AddMousePosEvent(-1000, -1000); WidgetFrame(); WidgetFrame();
        Save("minimized-countdown.png", 312, 140, (int)Value(countdownBounds, "X") - 16, (int)Value(countdownBounds, "Y") - 16);
        Call(Clock, "MarkLaunched", 300.0);
        foreach (AscentGuidance.AscentState phase in new[] { AscentGuidance.AscentState.VerticalClimb, AscentGuidance.AscentState.GravityTurn, AscentGuidance.AscentState.CoastToApoapsis, AscentGuidance.AscentState.Circularizing })
        {
            Set(Guidance, "CurrentState", phase); activity = ReadActivity(); WidgetFrame();
            Check(Active(activity) && (bool)activity.GetType().GetField("Controlling").GetValue(activity), "Active ascent phase is not identified as controlling");
            object bounds = Call(Store, "Find", "minimized_activity");
            Check(Value(bounds, "Width") == Value(countdownBounds, "Width") && Value(bounds, "Height") == Value(countdownBounds, "Height"), "Activity phase changes resize the widget");
        }
        Save("minimized-active.png", 312, 140, (int)Value(countdownBounds, "X") - 16, (int)Value(countdownBounds, "Y") - 16);
        activity = ReadActivity(instrumentsEnabled: true); WidgetFrame();
        Check(activity.GetType().GetField("Detail").GetValue(activity) == null, "Altitude/time remain duplicated with the mission widget enabled");
        Save("minimized-with-mission-widget.png", 312, 140, (int)Value(countdownBounds, "X") - 16, (int)Value(countdownBounds, "Y") - 16);
        activity = ReadActivity(); WidgetFrame();
        Check(((string)activity.GetType().GetField("Detail").GetValue(activity)).Contains("km")
            && ((string)activity.GetType().GetField("Detail").GetValue(activity)).Contains("T+"), "Disabling the mission widget does not restore the minimized flight readouts");
        // Moving the activity card must leave the saved icon position untouched.
        float2 activeStart = new float2(Value(countdownBounds, "X") + 20, Value(countdownBounds, "Y") + 20);
        for (int frame = 0; frame < 22; frame++)
        {
            float amount = Math.Clamp((frame - 5) / 8f, 0, 1);
            io.AddMousePosEvent(activeStart.X + 220 * amount, activeStart.Y + 110 * amount);
            if (frame == 3) io.AddMouseButtonEvent(0, true);
            if (frame == 17) io.AddMouseButtonEvent(0, false);
            Check(!WidgetFrame(), "Dragging the activity widget reopens the app");
        }
        object activeMoved = Call(Store, "Find", "minimized_activity");
        Check(Math.Abs(Value(activeMoved, "X") - Value(countdownBounds, "X") - 220) < 1
            && Math.Abs(Value(activeMoved, "Y") - Value(countdownBounds, "Y") - 110) < 1
            && Value(Call(Store, "Find", "minimized_icon"), "X") == Value(moved, "X"), "Activity dragging changes the icon's independent position");
        // Single-click restores, and restoring preserves the full app's saved dimensions.
        io.AddMousePosEvent(Value(activeMoved, "X") + 20, Value(activeMoved, "Y") + 20); WidgetFrame();
        io.AddMouseButtonEvent(0, true); WidgetFrame(); io.AddMouseButtonEvent(0, false);
        Check(WidgetFrame(), "A single click does not restore the minimized app");
        Call(MainLayout, "PlaceAt", new float2(Value(activeMoved, "X"), Value(activeMoved, "Y")));
        Call(Window, "OnVisibilityChanged", false); Frame();
        object restored = Call(Store, "Find", "autopilot");
        Check(Value(restored, "X") == Value(activeMoved, "X") && Value(restored, "Y") == Value(activeMoved, "Y")
            && Value(restored, "Width") == Value(titleAfter, "Width") && Value(restored, "Height") == Value(titleAfter, "Height"), "Restoring loses the app position or full window size");
        Set(Guidance, "CurrentState", AscentGuidance.AscentState.OrbitAchieved); activity = ReadActivity();
        Check(!Active(activity), "Completed guidance keeps the activity widget expanded"); WidgetFrame();
        object iconReturned = Call(Store, "Find", "minimized_icon");
        Check(Value(iconReturned, "Width") == 40 * scaleNow && Value(iconReturned, "X") == Value(moved, "X")
            && Value(iconReturned, "Y") == Value(moved, "Y"), "Completed activity does not return to the saved K position");
        Call(widget, "PlaceAt", titleBefore); WidgetFrame();
        Check(Value(Call(Store, "Find", "minimized_icon"), "X") == Value(moved, "X"), "Closing the main window again overwrites the saved icon position");
        activity = ReadActivity(true); WidgetFrame();
        Check(Value(Call(Store, "Find", "minimized_activity"), "X") == Value(activeMoved, "X")
            && Value(Call(Store, "Find", "minimized_activity"), "Y") == Value(activeMoved, "Y"), "Closing the main window again overwrites the activity position");
        Check(Active(ReadActivity(true)), "Pending actions are missing from the widget");
        Set(Landing, "State", Enum.Parse(Mod.GetType("KITN.LandingState"), "Planning"));
        activity = ReadActivity();
        Check(Active(activity) && !(bool)activity.GetType().GetField("Controlling").GetValue(activity), "Landing calculation is mislabeled as rocket control");
        Set(Landing, "State", Enum.Parse(Mod.GetType("KITN.LandingState"), "PoweredDescent"));
        activity = ReadActivity();
        Check(Active(activity) && (bool)activity.GetType().GetField("Controlling").GetValue(activity), "Powered descent is missing from the activity widget");
        Set(Landing, "State", Enum.Parse(Mod.GetType("KITN.LandingState"), "Idle"));
        // Both independently positioned views must remain reachable near the screen edge.
        activity = ReadActivity(); WidgetFrame();
        Call(widgetLayout, "PlaceAt", new float2(1550, 950)); WidgetFrame();
        activity = ReadActivity(true); WidgetFrame();
        object edge = Call(Store, "Find", "minimized_activity");
        Check(Value(edge, "X") + Value(edge, "Width") <= io.DisplaySize.X && Value(edge, "Y") + Value(edge, "Height") <= io.DisplaySize.Y,
            "Activity expansion extends outside the viewport");
        Check(Call(Store, "Flush", Environment.TickCount64, true).ToString() == "Saved", "Minimized position did not persist");
        string path = (string)Store.GetType().GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Store);
        object reload = New("UiLayoutStore", path); Call(reload, "Load");
        object savedIcon = Call(Store, "Find", "minimized_icon");
        widget = New("MinimizedWindow", reload); Call(widget, "PlaceAt", titleBefore); WidgetFrame();
        Check(Value(Call(reload, "Find", "minimized_activity"), "X") == Value(edge, "X")
            && Value(Call(reload, "Find", "minimized_activity"), "Y") == Value(edge, "Y"), "Activity position does not survive restart");
        activity = ReadActivity(); WidgetFrame();
        Check(Value(Call(reload, "Find", "minimized_icon"), "X") == Value(savedIcon, "X")
            && Value(Call(reload, "Find", "minimized_icon"), "Y") == Value(savedIcon, "Y"), "Icon position does not survive restart independently");
        object legacy = New("UiLayoutStore", path + ".legacy"); Call(legacy, "Load");
        Call(legacy, "Record", "minimized", moved, Environment.TickCount64);
        widget = New("MinimizedWindow", legacy); activity = ReadActivity(true); WidgetFrame();
        Check(Value(Call(legacy, "Find", "minimized_activity"), "X") == Value(moved, "X"), "Existing shared widget position does not migrate to the activity view");
        activity = ReadActivity(); WidgetFrame();
        Check(Value(Call(legacy, "Find", "minimized_icon"), "X") == Value(moved, "X"), "Existing shared widget position does not migrate to the icon view");
        Frame(true, true); Frame(true, true);
        Call(MainLayout, "PlaceAt", new float2(1000, 600)); Frame();
        object smallRestored = Call(Store, "Find", "autopilot");
        Check(Value(smallRestored, "Width") == 560 && Value(smallRestored, "Height") == 400
            && Value(smallRestored, "X") == 1000 && Value(smallRestored, "Y") == 600, "Restoring a resized window clamps it using the old default size");
        ((IDisposable)planner).Dispose();
        Console.WriteLine("PASS combined header: every mode, settings, tooltips and X in English/German at both widths; custom title dragging");
        Console.WriteLine("PASS minimized widget: independent activity/icon positions across closing and restart, migration, duplicate readout visibility, drag versus click, restore and viewport bounds");
    }
}
