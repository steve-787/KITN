using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSAAutopilot
{
    /// <summary>Only editable launch settings; telemetry, diagnostics and mission state stay live.</summary>
    internal sealed class FlightPlanSettings
    {
        public float TargetPeriapsisKm { get; set; } = 200;
        public float TargetApoapsisKm { get; set; } = 200;
        public float GravityTurnStartM { get; set; } = 2500;
        public float GravityTurnEndM { get; set; } = 65000;
        public float GravityTurnMinSpeedMps { get; set; } = 125;
        public float LaunchHeadingDeg { get; set; } = 90;
        public float GravityTurnShape { get; set; } = 1.1f;
        public float MaxPitchRateDegS { get; set; } = 1;
        public int LaunchCountdownSeconds { get; set; } = 10;
        public bool AutoStaging { get; set; } = true;
        public bool HotStaging { get; set; }
        public float HotStagingFuelPercent { get; set; } = 5;
        public bool AutoTimeskipToApoapsis { get; set; } = true;

        public FlightPlanSettings() { }
        public static FlightPlanSettings Capture(LaunchProfile p) => new FlightPlanSettings
        {
            TargetPeriapsisKm = p.TargetPeriapsisKm, TargetApoapsisKm = p.TargetApoapsisKm,
            GravityTurnStartM = p.GravityTurnStartM, GravityTurnEndM = p.GravityTurnEndM,
            GravityTurnMinSpeedMps = p.GravityTurnMinSpeedMps, LaunchHeadingDeg = p.LaunchHeadingDeg,
            GravityTurnShape = p.GravityTurnShape, MaxPitchRateDegS = p.MaxPitchRateDegS,
            LaunchCountdownSeconds = p.LaunchCountdownSeconds, AutoStaging = p.AutoStaging,
            HotStaging = p.HotStaging, HotStagingFuelPercent = p.HotStagingFuelPercent,
            AutoTimeskipToApoapsis = p.AutoTimeskipToApoapsis
        };

        [JsonIgnore]
        public bool IsValid => float.IsFinite(TargetApoapsisKm) && float.IsFinite(TargetPeriapsisKm)
            && TargetPeriapsisKm >= 100 && TargetApoapsisKm >= TargetPeriapsisKm
            && float.IsFinite(GravityTurnStartM) && GravityTurnStartM >= 0
            && float.IsFinite(GravityTurnEndM) && GravityTurnEndM > GravityTurnStartM
            && float.IsFinite(GravityTurnMinSpeedMps) && GravityTurnMinSpeedMps >= 0
            && float.IsFinite(LaunchHeadingDeg) && LaunchHeadingDeg >= 0 && LaunchHeadingDeg <= 360
            && float.IsFinite(GravityTurnShape) && GravityTurnShape > 0
            && float.IsFinite(MaxPitchRateDegS) && MaxPitchRateDegS > 0
            && LaunchCountdownSeconds >= 1 && LaunchCountdownSeconds <= 3600
            && float.IsFinite(HotStagingFuelPercent) && HotStagingFuelPercent >= 0 && HotStagingFuelPercent <= 100;

        public void Apply(LaunchProfile p)
        {
            if (!IsValid) throw new InvalidDataException("Invalid flight preset settings");
            p.TargetPeriapsisKm = TargetPeriapsisKm; p.TargetApoapsisKm = TargetApoapsisKm;
            p.GravityTurnStartM = GravityTurnStartM; p.GravityTurnEndM = GravityTurnEndM;
            p.GravityTurnMinSpeedMps = GravityTurnMinSpeedMps; p.LaunchHeadingDeg = LaunchHeadingDeg;
            p.GravityTurnShape = GravityTurnShape; p.MaxPitchRateDegS = MaxPitchRateDegS;
            p.LaunchCountdownSeconds = LaunchCountdownSeconds; p.AutoStaging = AutoStaging;
            p.HotStaging = HotStaging; p.HotStagingFuelPercent = HotStagingFuelPercent;
            p.AutoTimeskipToApoapsis = AutoTimeskipToApoapsis;
        }

        public bool Matches(LaunchProfile p) => TargetPeriapsisKm == p.TargetPeriapsisKm && TargetApoapsisKm == p.TargetApoapsisKm
            && GravityTurnStartM == p.GravityTurnStartM && GravityTurnEndM == p.GravityTurnEndM
            && GravityTurnMinSpeedMps == p.GravityTurnMinSpeedMps && LaunchHeadingDeg == p.LaunchHeadingDeg
            && GravityTurnShape == p.GravityTurnShape && MaxPitchRateDegS == p.MaxPitchRateDegS
            && LaunchCountdownSeconds == p.LaunchCountdownSeconds && AutoStaging == p.AutoStaging
            && HotStaging == p.HotStaging && HotStagingFuelPercent == p.HotStagingFuelPercent
            && AutoTimeskipToApoapsis == p.AutoTimeskipToApoapsis;
    }

    internal sealed class FlightPreset
    {
        [JsonRequired] public string Name { get; set; }
        [JsonRequired] public FlightPlanSettings Settings { get; set; }
        public FlightPreset() { }
    }

    internal sealed class FlightPresetDocument
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public List<FlightPreset> Presets { get; set; } = new List<FlightPreset>();
        public FlightPresetDocument() { }
    }

    /// <summary>Named presets beside the DLL, committed atomically with the preceding revision kept.</summary>
    internal sealed class FlightPresetStore
    {
        private const long MaximumFileBytes = 1024 * 1024;
        private readonly string _path;
        private List<FlightPreset> _presets = new List<FlightPreset>();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 16 };
        public IReadOnlyList<FlightPreset> Presets => _presets;
        public bool IsLoaded { get; private set; }
        public Exception LastError { get; private set; }
        public string FilePath => _path;
        public FlightPresetStore(string path) { _path = Path.GetFullPath(path); }

        public static bool ValidName(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80 || name.Contains("##", StringComparison.Ordinal)) return false;
            foreach (char c in name) if (char.IsControl(c)) return false;
            return true;
        }
        private static string NormalizedName(string name) => name.Trim().Normalize(NormalizationForm.FormC);
        public FlightPreset Find(string name) => string.IsNullOrWhiteSpace(name) ? null
            : _presets.Find(p => string.Equals(p.Name, NormalizedName(name), StringComparison.OrdinalIgnoreCase));

        public bool Load()
        {
            LastError = null; IsLoaded = false;
            try
            {
                if (!File.Exists(_path)) { _presets = new List<FlightPreset>(); IsLoaded = true; return true; }
                if (new FileInfo(_path).Length > MaximumFileBytes) throw new InvalidDataException("Flight preset file exceeds 1 MiB");
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var file = JsonSerializer.Deserialize<FlightPresetDocument>(stream, Json);
                if (file == null || file.Version != 1 || file.Presets == null)
                    throw new InvalidDataException("Unsupported flight preset file format");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FlightPreset preset in file.Presets)
                {
                    if (preset == null || !ValidName(preset.Name) || preset.Settings?.IsValid != true)
                        throw new InvalidDataException("Flight preset contains an invalid name or settings");
                    preset.Name = NormalizedName(preset.Name);
                    if (!names.Add(preset.Name)) throw new InvalidDataException("Duplicate flight preset name");
                }
                file.Presets.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                _presets = file.Presets; IsLoaded = true;
                return true;
            }
            catch (Exception ex) { LastError = ex; return false; }
        }

        public bool Save(string name, LaunchProfile profile)
        {
            string temporary = null;
            LastError = null;
            try
            {
                if (!IsLoaded) throw new InvalidOperationException("Presets could not be loaded; existing data will be preserved");
                if (!ValidName(name)) throw new ArgumentException("Enter a preset name of 1 to 80 characters without control characters or ##");
                FlightPlanSettings settings = FlightPlanSettings.Capture(profile);
                if (!settings.IsValid) throw new InvalidDataException("Flight plan settings are invalid");
                name = NormalizedName(name);
                var next = new List<FlightPreset>(_presets);
                int existing = next.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                var preset = new FlightPreset { Name = name, Settings = settings };
                if (existing < 0) next.Add(preset); else next[existing] = preset;
                next.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new FlightPresetDocument { Presets = next }, Json);
                    if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Flight preset file exceeds 1 MiB");
                    stream.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak"); else File.Move(temporary, _path);
                _presets = next;
                return true;
            }
            catch (Exception ex) { LastError = ex; return false; }
            finally
            {
                if (temporary != null)
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
