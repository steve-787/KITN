using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KSAAutopilot
{
    /// <summary>The text log and CSV use the same named, culture-independent measurements.</summary>
    internal sealed class TelemetryRecord
    {
        private readonly List<string> _names = new List<string>();
        private readonly List<string> _values = new List<string>();
        private readonly HashSet<string> _uniqueNames = new HashSet<string>();
        private readonly List<KeyValuePair<string, string>> _sections = new List<KeyValuePair<string, string>>();
        private readonly StringBuilder _text = new StringBuilder();
        private string _section;

        public string CsvHeader => string.Join(",", _names);
        public string CsvRow => string.Join(",", _values);

        public IReadOnlyList<KeyValuePair<string, string>> Sections
        {
            get { FinishSection(); return _sections; }
        }

        public void Section(string name)
        {
            FinishSection();
            _section = name;
        }

        public void Add(string name, double value, string textFormat = "F3")
        {
            AddCell(name, value.ToString("R", CultureInfo.InvariantCulture),
                value.ToString(textFormat, CultureInfo.InvariantCulture));
        }

        public void Add(string name, long value)
        {
            string formatted = value.ToString(CultureInfo.InvariantCulture);
            AddCell(name, formatted, formatted);
        }

        public void Add(string name, bool value)
        {
            AddCell(name, value ? "true" : "false", value ? "true" : "false");
        }

        public void Add(string name, string value)
        {
            string escaped = EscapeLine(value ?? "n/a");
            AddCell(name, EscapeCsv(escaped), "\"" + escaped.Replace("\"", "\\\"") + "\"");
        }

        private void AddCell(string name, string csv, string text)
        {
            if (!_uniqueNames.Add(name)) throw new InvalidOperationException("Duplicate telemetry column: " + name);
            _names.Add(name);
            _values.Add(csv);
            if (_text.Length > 0) _text.Append(' ');
            _text.Append(name).Append('=').Append(text);
        }

        private void FinishSection()
        {
            if (_section == null) return;
            _sections.Add(new KeyValuePair<string, string>(_section, _text.ToString()));
            _text.Clear();
            _section = null;
        }

        private static string EscapeLine(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string EscapeCsv(string value)
        {
            return value.IndexOfAny(new[] { ',', '"' }) < 0
                ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
