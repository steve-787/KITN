using System;
using System.IO;
using System.Text;

namespace KSAAutopilot
{
    /// <summary>A live-readable UTF-8 file with bounded archives and optional CSV header.</summary>
    internal sealed class DiagnosticFile : IDisposable
    {
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _archiveCount;
        private readonly string _header;
        private bool _started;
        private bool _hasEntries;
        private bool _disposed;

        public DiagnosticFile(string path, long maxBytes, int archiveCount, string header = null)
        {
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (archiveCount < 1) throw new ArgumentOutOfRangeException(nameof(archiveCount));
            _path = path;
            _maxBytes = maxBytes;
            _archiveCount = archiveCount;
            _header = header;
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiagnosticFile));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            if (File.Exists(_path) && new FileInfo(_path).Length > 0) Rotate();
            _started = true;
            _hasEntries = false;
            using (StreamWriter writer = Open()) { }
        }

        public void Write(string entry)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiagnosticFile));
            if (!_started) Start();

            long entryBytes = Encoding.UTF8.GetByteCount(entry) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            long fileBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (_hasEntries && fileBytes + entryBytes > _maxBytes)
            {
                Rotate();
                _hasEntries = false;
            }

            // Keep a single large entry intact, even if it exceeds the rotation limit.
            // Close after each entry so ordinary Windows readers can open the file, too.
            using (StreamWriter writer = Open()) { writer.WriteLine(entry); }
            _hasEntries = true;
        }

        private StreamWriter Open()
        {
            var stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            bool empty = stream.Length == 0;
            var writer = new StreamWriter(stream, new UTF8Encoding(false));
            if (empty && _header != null) writer.WriteLine(_header);
            return writer;
        }

        private void Rotate()
        {
            string oldest = _path + "." + _archiveCount;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = _archiveCount - 1; i >= 1; i--)
            {
                string source = _path + "." + i;
                if (File.Exists(source)) File.Move(source, _path + "." + (i + 1));
            }
            if (File.Exists(_path)) File.Move(_path, _path + ".1");
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
