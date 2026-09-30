using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;
using System.Globalization;
using System.IO;

namespace WTT.UI.Logging;

/// <summary>
/// Custom file sink that writes logs to date-based folders: Logs/yyyy-MM-dd/wtt.log
/// Supports both text and JSON formatting, size-based rolling, and retention.
/// </summary>
public sealed class DateFolderFileSink : ILogEventSink, IDisposable
{
    private readonly string _baseDirectory;
    private readonly string _fileName;
    private readonly RollingInterval _rollingInterval;
    private readonly int _retainedFileCountLimit;
    private readonly long _fileSizeLimitBytes;
    private readonly bool _rollOnFileSizeLimit;
    private readonly TimeSpan _flushInterval;
    private readonly ITextFormatter _formatter;
    private readonly object _lock = new();

    private FileLifecycleManager? _currentFile;
    private DateTime _currentPeriodStart;
    private Timer? _flushTimer;
    private bool _disposed;

    public DateFolderFileSink(
        string baseDirectory,
        string fileName,
        RollingInterval rollingInterval,
        int retainedFileCountLimit,
        long fileSizeLimitBytes,
        bool rollOnFileSizeLimit,
        TimeSpan flushInterval,
        ITextFormatter formatter)
    {
        _baseDirectory = baseDirectory;
        _fileName = fileName;
        _rollingInterval = rollingInterval;
        _retainedFileCountLimit = retainedFileCountLimit;
        _fileSizeLimitBytes = fileSizeLimitBytes;
        _rollOnFileSizeLimit = rollOnFileSizeLimit;
        _flushInterval = flushInterval;
        _formatter = formatter;

        // Create base directory
        Directory.CreateDirectory(_baseDirectory);

        // Initialize first file
        RollFile();

        // Start flush timer
        _flushTimer = new Timer(_ => Flush(), null, _flushInterval, _flushInterval);
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed) return;

        lock (_lock)
        {
            CheckRoll();
            _currentFile?.Write(logEvent, _formatter);
        }
    }

    public void Flush()
    {
        if (_disposed) return;
        lock (_lock)
        {
            _currentFile?.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer?.Dispose();
        _flushTimer = null;

        lock (_lock)
        {
            _currentFile?.Dispose();
            _currentFile = null;
        }
    }

    private void CheckRoll()
    {
        var now = DateTime.Now;
        var periodStart = GetPeriodStart(now);

        bool shouldRoll = false;

        // Time-based roll
        if (periodStart != _currentPeriodStart)
        {
            shouldRoll = true;
        }

        // Size-based roll
        if (!shouldRoll && _rollOnFileSizeLimit && _currentFile != null)
        {
            if (_currentFile.Size >= _fileSizeLimitBytes)
            {
                shouldRoll = true;
            }
        }

        if (shouldRoll)
        {
            RollFile();
            CleanupOldFiles();
        }
    }

    private void RollFile()
    {
        _currentFile?.Dispose();

        var now = DateTime.Now;
        _currentPeriodStart = GetPeriodStart(now);

        // Create date-based subdirectory: Logs/yyyy-MM-dd/
        var dateFolder = Path.Combine(_baseDirectory, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dateFolder);

        // Filename: wtt-20260930-000000.log (or wtt-20260930-000001.log if rolling by size)
        var timestamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{_fileName}-{timestamp}.log";
        var filePath = Path.Combine(dateFolder, fileName);

        _currentFile = new FileLifecycleManager(filePath, _fileSizeLimitBytes);
    }

    private DateTime GetPeriodStart(DateTime dateTime)
    {
        return _rollingInterval switch
        {
            RollingInterval.Day => dateTime.Date,
            RollingInterval.Hour => new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, dateTime.Hour, 0, 0),
            RollingInterval.Month => new DateTime(dateTime.Year, dateTime.Month, 1),
            RollingInterval.Year => new DateTime(dateTime.Year, 1, 1),
            _ => dateTime.Date
        };
    }

    private void CleanupOldFiles()
    {
        try
        {
            var allLogFiles = Directory
                .GetFiles(_baseDirectory, $"{_fileName}-*.log", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(_retainedFileCountLimit)
                .ToList();

            foreach (var file in allLogFiles)
            {
                try { file.Delete(); } catch { /* Ignore deletion errors */ }
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    private sealed class FileLifecycleManager : IDisposable
    {
        private readonly string _filePath;
        private readonly long _sizeLimit;
        private FileStream? _stream;
        private StreamWriter? _writer;
        private long _size;

        public long Size => _size;

        public FileLifecycleManager(string filePath, long sizeLimit)
        {
            _filePath = filePath;
            _sizeLimit = sizeLimit;
            _stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream, new System.Text.UTF8Encoding(false)) { AutoFlush = false };
        }

        public void Write(LogEvent logEvent, ITextFormatter formatter)
        {
            if (_writer == null) return;

            using var buffer = new StringWriter();
            formatter.Format(logEvent, buffer);
            var text = buffer.ToString();

            _writer.Write(text);
            _size += System.Text.Encoding.UTF8.GetByteCount(text);
        }

        public void Flush()
        {
            _writer?.Flush();
            _stream?.Flush();
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _stream?.Dispose();
            _writer = null;
            _stream = null;
        }
    }
}