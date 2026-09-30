using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var loggerConfig = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.With<CorrelationIdEnricher>()
            .Enrich.With<PiiMaskingEnricher>()
            .MinimumLevel.ControlledBy(new LoggingLevelSwitch(GetMinimumLevel(options.MinimumLevel)));

        // Apply level overrides
        foreach (var overrideItem in options.LevelOverrides)
        {
            if (Enum.TryParse<LogEventLevel>(overrideItem.Value, true, out var level))
            {
                loggerConfig.MinimumLevel.Override(overrideItem.Key, level);
            }
        }

        // 3. Configure sinks based on options
        ConfigureSinks(loggerConfig, options);

        // 3. Build logger
        var logger = loggerConfig.CreateLogger();

        // 4. Register globally
        Log.Logger = logger;

        return loggerConfig;
    }

    private static void ConfigureSinks(LoggerConfiguration loggerConfig, LoggingOptions options)
    {
        // File sink (if enabled)
        if (options.EnableFileLogging)
        {
            var formatter = options.UseJsonFormatter
                ? (ITextFormatter)new JsonFormatter(renderMessage: true)
                : new MessageTemplateTextFormatter(
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    CultureInfo.InvariantCulture);

            var sink = new DateFolderFileSink(
                baseDirectory: options.GetExpandedLogDirectory(),
                fileName: options.FileName,
                rollingInterval: options.RollingInterval,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: options.RollOnFileSizeLimit,
                flushInterval: options.FlushToDiskInterval,
                formatter: formatter);

            loggerConfig.WriteTo.Sink(sink, restrictedToMinimumLevel: GetMinimumLevel(options.MinimumLevel));
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
    public static IDisposable PushCorrelationId(this ILogger logger, string? correlationId = null)
    {
        var id = correlationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..12];
        return Serilog.Context.LogContext.PushProperty(CorrelationIdEnricher.PropertyName, id);
    }
}