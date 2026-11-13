using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using KSA;

namespace KSAAutopilot
{
    /// <summary>
    /// Session logs, sampled telemetry and API information next to KSAAutopilot.dll.
    /// </summary>
    internal static class Diag
    {
        private static string _dir = ".";
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, double> _last = new Dictionary<string, double>();
        private const long MAX_FILE_BYTES = 10 * 1024 * 1024;
        private const int ARCHIVE_COUNT = 3;
        private static DiagnosticFile _log;
        private static DiagnosticFile _csv;
        private static double _lastFileError = double.NegativeInfinity;

        public static string SessionId { get; private set; } = "uninitialized";

        private static string LogPath => Path.Combine(_dir, "ksa_autopilot.log");

        public static void Init()
        {
            try
            {
                lock (_lock)
                {
                    _log?.Dispose();
                    _csv?.Dispose();
                    _csv = null;
                    _last.Clear();
                    _dir = Path.GetDirectoryName(typeof(Diag).Assembly.Location) ?? ".";
                    SessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)
                        + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    _log = new DiagnosticFile(LogPath, MAX_FILE_BYTES, ARCHIVE_COUNT);
                    _log.Start();
                }
            }
            catch (Exception ex) { ReportFileError(ex); }

            Info("SESSION", F($"session_id={SessionId} mod_version={typeof(Diag).Assembly.GetName().Version} game_version={typeof(Vehicle).Assembly.GetName().Version} runtime={Environment.Version} max_file_bytes={MAX_FILE_BYTES} archives={ARCHIVE_COUNT} log_dir=\"{_dir}\""));
        }

        public static void Log(string message)
        {
            Info("GENERAL", message);
        }

        public static string F(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
        public static void Info(string category, string message) => Write("INFO", category, message, true);
        public static void Warn(string category, string message) => Write("WARN", category, message, true);
        public static void Debug(string category, string message) => Write("DEBUG", category, message, false);

        public static void Error(string category, Exception exception, string context)
        {
            Write("ERROR", category, context + Environment.NewLine + exception, true);
        }

        private static void Write(string level, string category, string message, bool console)
        {
            string entry = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
                + " [" + level + "] [" + category + "] "
                + message.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    ");
            try
            {
                lock (_lock)
                {
                    if (_log == null) _log = new DiagnosticFile(LogPath, MAX_FILE_BYTES, ARCHIVE_COUNT);
                    _log.Write(entry);
                }
            }
            catch (Exception ex) { ReportFileError(ex); }
            if (console) WriteConsole("[KSAAutopilot] " + entry);
        }

        public static void Csv(TelemetryRecord record)
        {
            try
            {
                lock (_lock)
                {
                    if (_csv == null)
                    {
                        _csv = new DiagnosticFile(Path.Combine(_dir, "ksa_telemetry.csv"),
                            MAX_FILE_BYTES, ARCHIVE_COUNT, record.CsvHeader);
                    }
                    _csv.Write(record.CsvRow);
                }
            }
            catch (Exception ex) { ReportFileError(ex); }
        }

        private static void ReportFileError(Exception exception)
        {
            double now = Environment.TickCount64 / 1000.0;
            lock (_lock)
            {
                if (now - _lastFileError < 5.0) return;
                _lastFileError = now;
            }
            WriteConsole("[KSAAutopilot] [ERROR] Writing diagnostic files failed: " + exception);
        }

        private static void WriteConsole(string message)
        {
            try { Console.WriteLine(message); } catch { /* No console is available in some game hosts. */ }
        }

        public static void Shutdown()
        {
            Info("SESSION", "session_id=" + SessionId + " event=mod_unloaded");
            try
            {
                lock (_lock)
                {
                    _log?.Dispose();
                    _csv?.Dispose();
                    _log = null;
                    _csv = null;
                }
            }
            catch (Exception ex) { ReportFileError(ex); }
        }

        private static bool Due(string key, double seconds)
        {
            double now = Environment.TickCount64 / 1000.0;
            lock (_lock)
            {
                if (_last.TryGetValue(key, out double previous) && now - previous < seconds) return false;
                _last[key] = now;
                return true;
            }
        }

        /// <summary>Logs at most once per 'seconds' (real time) for the given key.</summary>
        public static void Every(string key, double seconds, Func<string> message)
        {
            if (!Due(key, seconds)) return;
            try { Log(message()); }
            catch (Exception ex) { Error("DIAGNOSTICS", ex, "Failed to build diagnostic message: " + key); }
        }

        public static void EveryError(string key, double seconds, string category, Exception exception, string context)
        {
            if (Due(key, seconds)) Error(category, exception, context);
        }

        public static void DumpApi()
        {
            try
            {
                var sb = new StringBuilder();
                DumpType(sb, MemberType(typeof(Vehicle), "FlightComputer"), null);
                DumpType(sb, typeof(FlightComputerAttitudeTrackTarget), null);
                DumpType(sb, MemberType(typeof(Vehicle), "_manualControlInputs"), null);
                DumpType(sb, MemberType(typeof(Vehicle), "NavBallData"), null);
                DumpType(sb, typeof(ThrusterMapFlags), null);
                DumpType(sb, MemberType(typeof(Vehicle), "PhysicsEnvironment"), null);
                DumpType(sb, MemberType(typeof(Vehicle), "KinematicStates"), null);
                DumpType(sb, typeof(ActiveEnginePerformance), null);
                DumpType(sb, typeof(SequenceList), null);
                DumpType(sb, typeof(Celestial), "Location|Radius|Terrain|Angular|Ccf|Cci|Latitude|Longitude|Atmosphere");
                DumpType(sb, typeof(LandmarkReference), null);
                DumpType(sb, typeof(PlumbedCore), "Mix|Feed|Throttle|Pulse|Propellant");
                DumpType(sb, typeof(Tank), "Mass|Mole|Propellant");
                DumpType(sb, typeof(Orbit), "Altitude|Eccentricity|Inclination|Period|Mu|Parent|StateVectors");
                DumpType(sb, typeof(Vehicle), "Id|Parent|Position|Mass|Physics|Acceleration|Angular|NavBall|Pressure|Altitude|Orbit|Apoapsis|Periapsis|Velocity|Attitude|Control|Throttle|Engine|Flight|Surface|Thrust|Parts|BodyRates|BoundingBox|TerrainContact|LaunchGameTime");
                DumpType(sb, typeof(Universe), "Warp|Time|Speed");
                File.WriteAllText(Path.Combine(_dir, "ksa_api_dump.txt"), sb.ToString());
                Info("API", "API dump written to ksa_api_dump.txt");
            }
            catch (Exception ex)
            {
                Error("API", ex, "API dump failed");
            }
        }

        private static Type MemberType(Type owner, string name)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo f = owner.GetField(name, F);
            if (f != null) return f.FieldType;
            PropertyInfo p = owner.GetProperty(name, F);
            return p != null ? p.PropertyType : null;
        }

        private static bool Matches(string name, string[] keys)
        {
            if (keys == null) return true;
            for (int i = 0; i < keys.Length; i++)
            {
                if (name.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static void DumpType(StringBuilder sb, Type type, string filter)
        {
            if (type == null) { sb.AppendLine("(Typ nicht gefunden)"); sb.AppendLine(); return; }
            // KSA exposes NavBallData and physics states as ref-returning properties.
            if (type.IsByRef) type = type.GetElementType();
            string[] keys = filter == null ? null : filter.Split('|');
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            sb.AppendLine("=== " + type.FullName + " ===");
            if (type.IsEnum)
            {
                foreach (string n in Enum.GetNames(type)) sb.AppendLine("  " + n);
                sb.AppendLine();
                return;
            }

            for (Type t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
            {
                if (t != type) sb.AppendLine("  -- von " + t.FullName);
                foreach (FieldInfo f in t.GetFields(F))
                {
                    if (Matches(f.Name, keys)) sb.AppendLine("  field " + f.FieldType.Name + " " + f.Name);
                }
                foreach (PropertyInfo p in t.GetProperties(F))
                {
                    if (Matches(p.Name, keys)) sb.AppendLine("  prop  " + p.PropertyType.Name + " " + p.Name + (p.CanWrite ? " {get;set;}" : " {get;}"));
                }
                foreach (MethodInfo m in t.GetMethods(F))
                {
                    if (m.IsSpecialName || !Matches(m.Name, keys)) continue;
                    var ps = new StringBuilder();
                    foreach (ParameterInfo pi in m.GetParameters())
                    {
                        if (ps.Length > 0) ps.Append(", ");
                        ps.Append(pi.ParameterType.Name + " " + pi.Name);
                    }
                    sb.AppendLine("  method " + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
                }
            }
            sb.AppendLine();
        }
    }
}
