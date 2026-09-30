using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;

namespace WTT.Infrastructure.Logging;

/// <summary>
/// WTT log files ko padhta hai — both old flat structure aur new date-folder structure support karta hai.
/// 
/// Supported path patterns:
///   Old: LogDirectory/wtt-20260930-220000.log
///   New: LogDirectory/2026-09-30/wtt-20260930-220000.log  ← DateFolderFileSink ka output
/// 
/// Manager Requirement #2: Date-based folders se logs padhna.
/// </summary>
public sealed partial class LogReaderService : ILogReaderService
{
    public static readonly string DefaultLogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WTT", "Logs");

    // Serilog 3-char level abbreviations: VRB DBG INF WRN ERR FTL
    private static readonly string[] LevelOrder = ["VRB", "DBG", "INF", "WRN", "ERR", "FTL"];

    public string LogDirectory { get; }

    public LogReaderService(string? logDirectory = null)
    {
        LogDirectory = string.IsNullOrWhiteSpace(logDirectory) ? DefaultLogDirectory : logDirectory;
    }

    public async Task<IReadOnlyList<LogEntry>> ReadRecentAsync(
        int maxLines = 1000,
        string? minimumLevel = null,
        string? contains = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(LogDirectory)) return Array.Empty<LogEntry>();

        // --- Date-folder structure se files dhundho ---
        // New style:  Logs/yyyy-MM-dd/wtt-*.log   (DateFolderFileSink ka output)
        // Old style:  Logs/wtt-*.log               (flat, backward compatibility)
        var logDir = new DirectoryInfo(LogDirectory);

        // Pichle 7 din ke date folders bhi scan karo + root folder
        var allFiles = logDir
            .GetFiles("wtt-*.log", SearchOption.AllDirectories)  // recursive = date sub-folders bhi
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .GroupBy(f => f.FullName)          // duplicates hatao (agar same file 2 baar aa jaaye)
            .Select(g => g.First())
            .Take(10)                           // performance: sirf last 10 files (zyada kaafi hai)
            .ToList();

        var minIndex = minimumLevel is null ? 0 : Math.Max(0, Array.IndexOf(LevelOrder, NormalizeLevel(minimumLevel)));
        var result = new List<LogEntry>(maxLines);

        foreach (var file in allFiles)
        {
            var lines = await ReadAllLinesSharedAsync(file.FullName, ct);
            LogEntry? current = null;
            var buffer = new List<LogEntry>();

            foreach (var line in lines)
            {
                var m = LineRegex().Match(line);
                if (m.Success)
                {
                    if (current is not null) buffer.Add(current);
                    var ts = DateTime.ParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
                    current = new LogEntry(ts, m.Groups["lvl"].Value, ShortSource(m.Groups["src"].Value), m.Groups["msg"].Value);
                }
                else if (current is not null)
                {
                    // continuation line (exception stack trace)
                    current = current with { Message = current.Message + Environment.NewLine + line };
                }
            }
            if (current is not null) buffer.Add(current);

            // newest first
            buffer.Reverse();
            foreach (var e in buffer)
            {
                if (Array.IndexOf(LevelOrder, e.Level) < minIndex) continue;
                if (!string.IsNullOrWhiteSpace(contains) &&
                    !e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase) &&
                    !e.Source.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(e);
                if (result.Count >= maxLines) return result;
            }
        }
        return result;
    }

    private static async Task<List<string>> ReadAllLinesSharedAsync(string path, CancellationToken ct)
    {
        // Serilog keeps the file open; we must allow shared read/write.
        var lines = new List<string>();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (await reader.ReadLineAsync(ct) is { } line) lines.Add(line);
        return lines;
    }

    private static string NormalizeLevel(string level) => level.Trim().ToUpperInvariant() switch
    {
        "VERBOSE" or "TRACE" => "VRB",
        "DEBUG" => "DBG",
        "INFORMATION" or "INFO" => "INF",
        "WARNING" or "WARN" => "WRN",
        "ERROR" => "ERR",
        "FATAL" or "CRITICAL" => "FTL",
        var v => v,
    };

    private static string ShortSource(string source)
    {
        var i = source.LastIndexOf('.');
        return i >= 0 ? source[(i + 1)..] : source;
    }

    // Log line format (DateFolderFileSink output):
    // 2026-10-01 00:01:56.454 +05:30 [INF] [T2] [WTT.Infrastructure.Services.AuthService] message
    // T prefix is optional for backward compatibility with older log files
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<lvl>[A-Z]{3})\] \[T?(?<thread>\d+)\] \[(?<src>[^\]]*)\] (?<msg>.*)$")]
    private static partial Regex LineRegex();
}
