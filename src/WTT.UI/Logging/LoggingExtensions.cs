using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;
using Serilog.Sinks.Debug;
using System.Diagnostics;
using System.Globalization;

namespace WTT.UI.Logging;

/// <summary>
/// Extension methods for configuring industry-grade logging in the application.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Configures Serilog with industry-grade settings from <see cref="LoggingOptions"/>.
    /// Call this in App.xaml.cs during host configuration.
    /// </summary>
    /// <param name="services">Service collection for DI.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The LoggerConfiguration for further customization.</param>
    public static LoggerConfiguration AddWttLogging(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. Bind options from configuration
        var options = new LoggingOptions();
        configuration.GetSection(LoggingOptions.SectionName).Bind(options);

        // Register options for DI
        services.Configure<LoggingOptions>(configuration.GetSection(LoggingOptions.SectionName));
        services.AddSingleton(options);

        // 2. Create base logger configuration
        var minimumLevel = options.GetMinimumLogEventLevel();

        // LoggingLevelSwitch — the official Serilog way to change the level at runtime
        // It is a singleton; LoggingController uses it for the runtime toggle
        var levelSwitch = new LoggingLevelSwitch(minimumLevel);

        // LoggingController — runtime logging toggle (manager requirement #3, NO restart needed)
        var loggingController = new LoggingController(levelSwitch, options);
        services.AddSingleton(levelSwitch);         // in case direct access is needed
        services.AddSingleton(loggingController);   // SettingsViewModel injects this

        var loggerConfig = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.With<CorrelationIdEnricher>()
            .Enrich.With<PiiMaskingEnricher>()
            .MinimumLevel.ControlledBy(levelSwitch);

        // Apply level overrides (specific namespace ko alag level do)
        foreach (var overrideItem in options.LevelOverrides)
        {
            if (Enum.TryParse<LogEventLevel>(overrideItem.Value, true, out var overrideLevel))
            {
                loggerConfig.MinimumLevel.Override(overrideItem.Key, overrideLevel);
            }
        }

        // 3. Configure sinks based on options
        ConfigureSinks(loggerConfig, options);

        // 4. Build the logger and register it globally
        var logger = loggerConfig.CreateLogger();
        Log.Logger = logger;

        // Redirect Microsoft.Extensions.Logging (ILogger<T>) to Serilog
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddSerilog(logger, dispose: true);
        });

        return loggerConfig;
    }

    private static void ConfigureSinks(LoggerConfiguration loggerConfig, LoggingOptions options)
    {
        var minimumLevel = options.GetMinimumLogEventLevel();

        // --- File sink (Date-folder structure: Logs/yyyy-MM-dd/wtt-yyyyMMdd-HHmmss.log) ---
        if (options.EnableFileLogging)
        {
            try
            {
                // Industry-grade output template:
                // - Timestamp with timezone
                // - Log level (3-char: INF/WRN/ERR/FTL)
                // - Thread ID (helpful when debugging concurrent issues)
                // - Source class name (where the log came from)
                // - Message + Exception stack trace (manager req #1: descriptive logs)
                var plainTemplate =
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [T{ThreadId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

                ITextFormatter formatter = options.UseJsonFormatter
                    ? (ITextFormatter)new JsonFormatter(renderMessage: true)
                    : new MessageTemplateTextFormatter(plainTemplate, CultureInfo.InvariantCulture);

                var expandedDir = options.GetExpandedLogDirectory();

                var sink = new DateFolderFileSink(
                    baseDirectory: expandedDir,
                    fileName: options.FileName,
                    rollingInterval: options.RollingInterval,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: options.RollOnFileSizeLimit,
                    flushInterval: options.FlushToDiskInterval,
                    formatter: formatter);

                loggerConfig.WriteTo.Sink(sink, restrictedToMinimumLevel: minimumLevel);

                System.Diagnostics.Debug.WriteLine(
                    $"[WTT Logging] File sink initialized → {expandedDir}/yyyy-MM-dd/{options.FileName}-*.log");
            }
            catch (Exception ex)
            {
                // If the file sink fails, the app must not crash.
                // Show the error in debug output — logs will still be available via the debug sink.
                System.Diagnostics.Debug.WriteLine(
                    $"[WTT Logging] WARNING: File sink initialization failed: {ex.Message}. " +
                    $"Check permissions for: {options.GetExpandedLogDirectory()}");
            }
        }

        // Debug sink (for development)
        if (options.EnableDebugSink)
        {
            loggerConfig.WriteTo.Debug(
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{ThreadId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
        }

        // Additional sinks (Seq, Elasticsearch, Datadog, etc.)
        foreach (var sinkConfig in options.AdditionalSinks)
        {
            // Example: sinkConfig = { "Name": "Seq", "Args": { "serverUrl": "http://seq:5341", "apiKey": "..." } }
            if (sinkConfig.TryGetValue("Name", out var nameObj) && nameObj is string sinkName)
            {
                // This would require the specific sink packages to be installed
                // For now, log a warning that additional sinks need manual setup
                System.Diagnostics.Debug.WriteLine($"[WTT Logging] Additional sink '{sinkName}' configured but requires manual Serilog.Sinks.* package installation.");
            }
        }
    }

    private static LogEventLevel GetMinimumLevel(string levelString)
    {
        return Enum.TryParse<LogEventLevel>(levelString, true, out var level) ? level : LogEventLevel.Information;
    }

    // Overload kept for backward compat — delegates to string version
    private static LogEventLevel GetMinimumLevel(LogEventLevel level) => level;
}

/// <summary>
/// Extension for adding correlation ID to the current logical operation.
/// </summary>
public static class CorrelationIdExtensions
{
    /// <summary>
    /// Creates a scope with a new CorrelationId (or uses existing Activity.Id).
    /// </summary>
    public static IDisposable PushCorrelationId(this Serilog.ILogger logger, string? correlationId = null)
    {
        var id = correlationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..12];
        return Serilog.Context.LogContext.PushProperty(CorrelationIdEnricher.PropertyName, id);
    }

    /// <summary>
    /// Creates a scope with a new CorrelationId for Microsoft.Extensions.Logging.
    /// </summary>
    public static IDisposable? PushCorrelationId(this Microsoft.Extensions.Logging.ILogger logger, string? correlationId = null)
    {
        var id = correlationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..12];
        return Serilog.Context.LogContext.PushProperty(CorrelationIdEnricher.PropertyName, id);
    }
}