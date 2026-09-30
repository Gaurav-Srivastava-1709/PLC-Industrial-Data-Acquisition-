using System.ComponentModel.DataAnnotations;
using Serilog;
using Serilog.Events;

namespace WTT.UI.Logging;

/// <summary>
/// Configuration options for industry-grade logging.
/// All settings can be overridden via appsettings.json or environment variables (WTT_Logging__*).
/// </summary>
public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    /// <summary>
    /// Master switch to enable/disable file logging entirely.
    /// Set to false in test environments (CI/CD, local dev) to avoid disk I/O and storage bloat.
    /// Default: true (enabled in production).
    /// </summary>
    public bool EnableFileLogging { get; set; } = true;

    /// <summary>
    /// Root directory for log files.
    /// Supports environment variable expansion (e.g., %LocalAppData%, %ProgramData%).
    /// </summary>
    [Required]
    public string LogDirectory { get; set; } = "%LocalAppData%/WTT/Logs";

    /// <summary>
    /// Base filename (without extension). Date and sequence number will be appended.
    /// </summary>
    public string FileName { get; set; } = "wtt";

    /// <summary>
    /// Rolling interval for log files.
    /// </summary>
    public RollingInterval RollingInterval { get; set; } = RollingInterval.Day;

    /// <summary>
    /// Maximum number of log files to retain (per rolling interval).
    /// Older files are deleted automatically.
    /// </summary>
    [Range(1, 3650)]
    public int RetainedFileCountLimit { get; set; } = 30;

    /// <summary>
    /// Maximum size of a single log file in bytes before rolling.
    /// Default: 20 MB.
    /// </summary>
    [Range(1048576, long.MaxValue)]
    public long FileSizeLimitBytes { get; set; } = 20_971_520;

    /// <summary>
    /// Whether to roll on file size limit in addition to time interval.
    /// </summary>
    public bool RollOnFileSizeLimit { get; set; } = true;

    /// <summary>
    /// Flush interval for buffered writes.
    /// </summary>
    public TimeSpan FlushToDiskInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Minimum log level for file sink.
    /// </summary>
    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;

    /// <summary>
    /// Override minimum level for specific namespaces.
    /// </summary>
    public Dictionary<string, string> LevelOverrides { get; set; } = new()
    {
        ["Microsoft"] = "Warning",
        ["System"] = "Warning",
        ["Microsoft.EntityFrameworkCore"] = "Warning",
        ["Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
    };

    /// <summary>
    /// Enable structured JSON output (for log aggregation systems like Seq, ELK, Datadog).
    /// When false, uses human-readable text template.
    /// </summary>
    public bool UseJsonFormatter { get; set; } = false;

    /// <summary>
    /// Whether to include scoping properties (CorrelationId, DeviceId, UserId, etc.) in log output.
    /// </summary>
    public bool IncludeScopingProperties { get; set; } = true;

    /// <summary>
    /// Enable PII masking for sensitive fields (passwords, tokens, connection strings).
    /// </summary>
    public bool EnablePiiMasking { get; set; } = true;

    /// <summary>
    /// Additional sinks to configure (e.g., Seq, Elasticsearch, Datadog).
    /// Each entry is a sink configuration dictionary.
    /// </summary>
    public List<Dictionary<string, object>> AdditionalSinks { get; set; } = new();

    /// <summary>
    /// Whether to write logs to Debug output (Visual Studio Output window).
    /// Useful for local development.
    /// </summary>
    public bool EnableDebugSink { get; set; } = true;

    /// <summary>
    /// Expands environment variables in LogDirectory path.
    /// </summary>
    public string GetExpandedLogDirectory()
    {
        return Environment.ExpandEnvironmentVariables(LogDirectory);
    }
}