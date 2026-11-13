using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KITN
{
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
    }

    internal sealed class UiLayoutDocument
    {
        public UiLayoutDocument() { }
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public Dictionary<string, UiWindowBounds> Windows { get; set; } = new Dictionary<string, UiWindowBounds>();
    }

    internal enum UiLayoutSaveResult { Unchanged, Saved, Failed }

    /// <summary>Mod windows only. Writes after movement settles, independently of paused or warped simulation time.</summary>
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
        public UiLayoutStore(string path) { _path = Path.GetFullPath(path); }
        public UiWindowBounds Find(string id) => _windows.TryGetValue(id, out UiWindowBounds bounds) ? bounds : null;

        public bool Load()
        {
            IsLoaded = false; LastError = null;
            try
            {
                var windows = new Dictionary<string, UiWindowBounds>();
                if (File.Exists(_path))
                {
                    if (new FileInfo(_path).Length > 65536) throw new InvalidDataException("UI layout file exceeds 64 KiB");
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    UiLayoutDocument file = JsonSerializer.Deserialize<UiLayoutDocument>(stream, Json);
                    if (file == null || file.Version != 1 || file.Windows == null || file.Windows.Count > 16)
                        throw new InvalidDataException("Unsupported UI layout format");
                    foreach (var window in file.Windows)
                        if (string.IsNullOrWhiteSpace(window.Key) || window.Key.Length > 80 || window.Value?.IsValid != true)
                            throw new InvalidDataException("Invalid UI window bounds");
                    windows = file.Windows;
                }
                _windows = windows; IsLoaded = true; IsDirty = false; _retryAfterMs = 0;
                return true;
            }
            catch (Exception ex) { LastError = ex; return false; }
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
                    JsonSerializer.Serialize(stream, new UiLayoutDocument { Windows = _windows }, Json);
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
