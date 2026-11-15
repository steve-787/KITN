using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KITN
{
    [JsonConverter(typeof(JsonStringEnumConverter<UiTheme>))]
    internal enum UiTheme { Retro, Modern }

    internal sealed class UiWidgetPreferences
    {
        public bool Enabled { get; set; }
        public UiTheme Theme { get; set; } = UiTheme.Retro;
        public bool AutoHideFrame { get; set; }
        [JsonIgnore] public bool IsValid => Enum.IsDefined(Theme);
        public UiWidgetPreferences Copy() => new UiWidgetPreferences { Enabled = Enabled, Theme = Theme, AutoHideFrame = AutoHideFrame };
    }

    internal sealed class UiPreferences
    {
        public UiTheme Theme { get; set; } = UiTheme.Retro;
        public bool AutoHideInstrumentFrame { get; set; }
        public bool AllowRcs { get; set; } = true;
        public bool AllowRemoteControl { get; set; } = true;
        public bool AllowPhysicsWarp { get; set; }
        public bool DetachedWindows { get; set; }
        public bool GravityAssists { get; set; }
        public LandingSettings Landing { get; set; } = new LandingSettings();
        public RendezvousSettings Rendezvous { get; set; } = new RendezvousSettings();
        public DockingSettings Docking { get; set; } = new DockingSettings();
        public HohmannSettings Hohmann { get; set; } = new HohmannSettings();
        public bool MainWindowOpen { get; set; } = true;
        public Dictionary<string, UiWidgetPreferences> Widgets { get; set; }
    }

    internal sealed class UiWindowBounds
    {
        public UiWindowBounds() { }
        [JsonRequired] public float X { get; set; }
        [JsonRequired] public float Y { get; set; }
        [JsonRequired] public float Width { get; set; }
        [JsonRequired] public float Height { get; set; }
        [JsonRequired] public float InterfaceScale { get; set; } = 1;

        [JsonIgnore]
        public bool IsValid => float.IsFinite(X) && float.IsFinite(Y)
            && float.IsFinite(Width) && Width > 0 && float.IsFinite(Height) && Height > 0
            && float.IsFinite(InterfaceScale) && InterfaceScale > 0;

        public bool Matches(UiWindowBounds other) => other != null && X == other.X && Y == other.Y
            && Width == other.Width && Height == other.Height && InterfaceScale == other.InterfaceScale;

        public UiWindowBounds Fit(float scale, float viewportX, float viewportY, float viewportWidth, float viewportHeight)
        {
            if (!IsValid || !float.IsFinite(scale) || scale <= 0
                || !float.IsFinite(viewportX) || !float.IsFinite(viewportY)
                || !float.IsFinite(viewportWidth) || viewportWidth < 1
                || !float.IsFinite(viewportHeight) || viewportHeight < 1) return null;
            double ratio = (double)scale / InterfaceScale;
            float width = (float)Math.Clamp(Width * ratio, 1, viewportWidth);
            float height = (float)Math.Clamp(Height * ratio, 1, viewportHeight);
            return new UiWindowBounds
            {
                X = (float)Math.Clamp(X * ratio, viewportX, (double)viewportX + viewportWidth - width),
                Y = (float)Math.Clamp(Y * ratio, viewportY, (double)viewportY + viewportHeight - height),
                Width = width, Height = height, InterfaceScale = scale
            };
        }

        // Desktop coordinates include monitor offsets; scaling those offsets moves windows
        // onto a different monitor. Only scale the window size in detached mode.
        public UiWindowBounds FitDesktop(float scale, float x, float y, float width, float height)
        {
            UiWindowBounds fitted = Fit(scale, x, y, width, height);
            if (fitted == null) return null;
            fitted.X = Math.Clamp(X, x, x + Math.Max(0, width - fitted.Width));
            fitted.Y = Math.Clamp(Y, y, y + Math.Max(0, height - fitted.Height));
            return fitted;
        }
    }

    internal sealed class UiLayoutDocument
    {
        public UiLayoutDocument() { }
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public Dictionary<string, UiWindowBounds> Windows { get; set; } = new Dictionary<string, UiWindowBounds>();
        public UiPreferences Settings { get; set; } = new UiPreferences();
    }

    internal enum UiLayoutSaveResult { Unchanged, Saved, Failed }

    /// <summary>Window layout and mod preferences. Saves independently of paused or warped simulation time.</summary>
    internal sealed class UiLayoutStore
    {
        private readonly string _path;
        private Dictionary<string, UiWindowBounds> _windows = new Dictionary<string, UiWindowBounds>();
        private long _lastChangeMs, _retryAfterMs;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 8 };
        public bool IsLoaded { get; private set; }
        public bool IsDirty { get; private set; }
        public Exception LastError { get; private set; }
        public int WindowCount => _windows.Count;
        private Dictionary<string, UiWidgetPreferences> _widgets = new Dictionary<string, UiWidgetPreferences>();
        public UiTheme Theme => Widget("mission_time").Theme;
        public bool AutoHideInstrumentFrame => Widget("mission_time").AutoHideFrame;
        public bool MainWindowOpen { get; private set; } = true;
        public bool MissionTimeOpen => Widget("mission_time").Enabled;
        public bool AllowRcs { get; private set; } = true;
        public bool AllowRemoteControl { get; private set; } = true;
        public bool AllowPhysicsWarp { get; private set; }
        public bool DetachedWindows { get; private set; }
        public bool GravityAssists { get; private set; }
        public LandingSettings Landing { get; private set; } = new LandingSettings();
        public RendezvousSettings Rendezvous { get; private set; } = new RendezvousSettings();
        public DockingSettings Docking { get; private set; } = new DockingSettings();
        public HohmannSettings Hohmann { get; private set; } = new HohmannSettings();
        public UiLayoutStore(string path) { _path = Path.GetFullPath(path); }
        public UiWindowBounds Find(string id) => _windows.TryGetValue(id, out UiWindowBounds bounds) ? bounds : null;
        public UiWidgetPreferences Widget(string id) => _widgets.TryGetValue(id, out var widget) ? widget.Copy() : new UiWidgetPreferences();

        public bool Load()
        {
            IsLoaded = false; LastError = null;
            try
            {
                var windows = new Dictionary<string, UiWindowBounds>();
                UiTheme theme = UiTheme.Retro;
                bool autoHide = false;
                bool allowRcs = true;
                bool allowRemote = true;
                bool physicsWarp = false;
                bool detachedWindows = false;
                bool gravityAssists = false;
                var landing = new LandingSettings();
                var rendezvous = new RendezvousSettings();
                var docking = new DockingSettings();
                var hohmann = new HohmannSettings();
                bool mainOpen = true;
                var widgets = new Dictionary<string, UiWidgetPreferences>();
                if (File.Exists(_path))
                {
                    if (new FileInfo(_path).Length > 65536) throw new InvalidDataException("UI layout file exceeds 64 KiB");
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    UiLayoutDocument file = JsonSerializer.Deserialize<UiLayoutDocument>(stream, Json);
                    if (file == null || file.Version != 1 || file.Windows == null || file.Windows.Count > 16
                        || file.Settings == null || !Enum.IsDefined(file.Settings.Theme) || file.Settings.Landing?.IsValid != true
                        || file.Settings.Rendezvous?.IsValid != true || file.Settings.Docking?.IsValid != true || file.Settings.Hohmann?.IsValid != true)
                        throw new InvalidDataException("Unsupported UI layout format");
                    foreach (var window in file.Windows)
                        if (string.IsNullOrWhiteSpace(window.Key) || window.Key.Length > 80 || window.Value?.IsValid != true)
                            throw new InvalidDataException("Invalid UI window bounds");
                    windows = file.Windows;
                    theme = file.Settings.Theme;
                    autoHide = file.Settings.AutoHideInstrumentFrame;
                    allowRcs = file.Settings.AllowRcs;
                    allowRemote = file.Settings.AllowRemoteControl;
                    physicsWarp = file.Settings.AllowPhysicsWarp;
                    detachedWindows = file.Settings.DetachedWindows;
                    gravityAssists = file.Settings.GravityAssists;
                    landing = file.Settings.Landing.Copy();
                    rendezvous = file.Settings.Rendezvous.Copy(); docking = file.Settings.Docking.Copy();
                    hohmann = file.Settings.Hohmann.Copy();
                    mainOpen = file.Settings.MainWindowOpen;
                    if (file.Settings.Widgets != null)
                    {
                        if (file.Settings.Widgets.Count > 32) throw new InvalidDataException("Too many widget preferences");
                        foreach (var widget in file.Settings.Widgets)
                        {
                            if (string.IsNullOrWhiteSpace(widget.Key) || widget.Key.Length > 80 || widget.Value?.IsValid != true)
                                throw new InvalidDataException("Invalid widget preferences");
                            widgets.Add(widget.Key, widget.Value.Copy());
                        }
                    }
                }
                // Migrate old theme/frame fields without changing saved window positions.
                if (!widgets.ContainsKey("mission_time")) widgets.Add("mission_time", new UiWidgetPreferences { Theme = theme, AutoHideFrame = autoHide });
                _widgets = widgets; MainWindowOpen = mainOpen;
                _windows = windows; AllowRcs = allowRcs; AllowRemoteControl = allowRemote; AllowPhysicsWarp = physicsWarp;
                DetachedWindows = detachedWindows;
                GravityAssists = gravityAssists;
                Landing = landing;
                Rendezvous = rendezvous; Docking = docking;
                Hohmann = hohmann;
                IsLoaded = true; IsDirty = false; _retryAfterMs = 0;
                return true;
            }
            catch (Exception ex) { LastError = ex; return false; }
        }

        public bool SetTheme(UiTheme theme, long nowMs)
        {
            var widget = Widget("mission_time"); widget.Theme = theme;
            return SetWidget("mission_time", widget, nowMs);
        }

        public bool SetAutoHideInstrumentFrame(bool enabled, long nowMs)
        {
            var widget = Widget("mission_time"); widget.AutoHideFrame = enabled;
            return SetWidget("mission_time", widget, nowMs);
        }

        public bool SetWidget(string id, UiWidgetPreferences widget, long nowMs)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(id) || id.Length > 80 || widget?.IsValid != true
                || (!_widgets.ContainsKey(id) && _widgets.Count >= 32)) return false;
            var old = Widget(id);
            if (_widgets.ContainsKey(id) && old.Enabled == widget.Enabled && old.Theme == widget.Theme && old.AutoHideFrame == widget.AutoHideFrame) return false;
            _widgets[id] = widget.Copy(); IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool SetMissionTimeOpen(bool enabled, long nowMs)
        {
            var widget = Widget("mission_time"); widget.Enabled = enabled;
            return SetWidget("mission_time", widget, nowMs);
        }

        public bool SetMainWindowOpen(bool open, long nowMs)
        {
            if (!IsLoaded || MainWindowOpen == open) return false;
            MainWindowOpen = open; IsDirty = true; _lastChangeMs = nowMs; return true;
        }

        public bool SetAllowRcs(bool enabled, long nowMs)
        {
            if (!IsLoaded || AllowRcs == enabled) return false;
            AllowRcs = enabled; IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool SetDetachedWindows(bool enabled, long nowMs)
        {
            if (!IsLoaded || DetachedWindows == enabled) return false;
            DetachedWindows = enabled; IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool SetAllowRemoteControl(bool enabled, long nowMs)
        {
            if (!IsLoaded || AllowRemoteControl == enabled) return false;
            AllowRemoteControl = enabled; IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool SetAllowPhysicsWarp(bool enabled, long nowMs)
        {
            if (!IsLoaded || AllowPhysicsWarp == enabled) return false;
            AllowPhysicsWarp = enabled; IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool SetGravityAssists(bool enabled, long nowMs)
        {
            if (!IsLoaded || GravityAssists == enabled) return false;
            GravityAssists = enabled; IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool SetLanding(LandingSettings settings, long nowMs)
        {
            if (!IsLoaded || settings?.IsValid != true || Landing.Matches(settings)) return false;
            Landing = settings.Copy(); IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool Record(string id, UiWindowBounds bounds, long nowMs)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || bounds?.IsValid != true
                || bounds.Matches(Find(id))) return false;
            _windows[id] = new UiWindowBounds
            { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height, InterfaceScale = bounds.InterfaceScale };
            IsDirty = true; _lastChangeMs = nowMs;
            return true;
        }

        public bool SetRendezvous(RendezvousSettings settings, long nowMs)
        {
            if (!IsLoaded || settings?.IsValid != true || Rendezvous.Matches(settings)) return false;
            Rendezvous = settings.Copy(); IsDirty = true; _lastChangeMs = nowMs; return true;
        }

        public bool SetDocking(DockingSettings settings, long nowMs)
        {
            if (!IsLoaded || settings?.IsValid != true || Docking.Matches(settings)) return false;
            Docking = settings.Copy(); IsDirty = true; _lastChangeMs = nowMs; return true;
        }

        public bool SetHohmann(HohmannSettings settings, long nowMs)
        {
            if (!IsLoaded || settings?.IsValid != true || Hohmann.Matches(settings)) return false;
            Hohmann = settings.Copy(); IsDirty = true; _lastChangeMs = nowMs; return true;
        }

        public UiLayoutSaveResult Flush(long nowMs, bool force = false)
        {
            if (!IsLoaded || !IsDirty || (!force && (nowMs - _lastChangeMs < 500 || nowMs < _retryAfterMs)))
                return UiLayoutSaveResult.Unchanged;
            string temporary = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new UiLayoutDocument { Windows = _windows,
                        Settings = new UiPreferences { Theme = Theme, AutoHideInstrumentFrame = AutoHideInstrumentFrame, AllowRcs = AllowRcs, AllowRemoteControl = AllowRemoteControl, AllowPhysicsWarp = AllowPhysicsWarp, DetachedWindows = DetachedWindows, GravityAssists = GravityAssists, Landing = Landing.Copy(), Rendezvous = Rendezvous.Copy(), Docking = Docking.Copy(), Hohmann = Hohmann.Copy(), MainWindowOpen = MainWindowOpen, Widgets = _widgets } }, Json);
                    if (stream.Length > 65536) throw new InvalidDataException("UI layout file exceeds 64 KiB");
                    stream.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak"); else File.Move(temporary, _path);
                IsDirty = false; LastError = null; _retryAfterMs = 0;
                return UiLayoutSaveResult.Saved;
            }
            catch (Exception ex) { LastError = ex; _retryAfterMs = nowMs + 5000; return UiLayoutSaveResult.Failed; }
            finally
            {
                if (temporary != null)
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
