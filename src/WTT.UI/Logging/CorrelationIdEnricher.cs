using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Context;
using System.Diagnostics;

namespace WTT.UI.Logging;

/// <summary>
/// Enriches log events with a CorrelationId.
/// - If a CorrelationId already exists in LogContext, uses it.
/// - Otherwise generates a new one (Activity.Id if available, else new Guid).
/// This allows tracing a single logical operation across multiple log statements and services.
/// </summary>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public const string PropertyName = "CorrelationId";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // 1. Check if CorrelationId already exists in the log event (from LogContext)
        if (logEvent.Properties.ContainsKey(PropertyName))
        {
            return; // Already set by LogContext
        }

        // 2. Try to get from current Activity (OpenTelemetry / System.Diagnostics.Activity)
        var activity = Activity.Current;
        if (activity?.Id is { } activityId)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, activityId));
            return;
        }

        // 3. Fallback: generate a new correlation ID for this log event
        var correlationId = ActivityTraceId.CreateRandom().ToString();
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, correlationId));
    }
}