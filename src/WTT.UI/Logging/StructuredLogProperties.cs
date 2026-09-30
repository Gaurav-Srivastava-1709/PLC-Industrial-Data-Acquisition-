using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Context;
using WTT.Domain.Enums;

namespace WTT.UI.Logging;

/// <summary>
/// Standard structured logging property names used across the application.
/// Using these constants ensures consistency and enables reliable querying in log aggregation systems.
/// </summary>
public static class StructuredLogProperties
{
    // Correlation & Tracing
    public const string CorrelationId = "CorrelationId";
    public const string RequestId = "RequestId";
    public const string TraceId = "TraceId";
    public const string SpanId = "SpanId";
    public const string ParentId = "ParentId";

    // User & Security
    public const string UserId = "UserId";
    public const string Username = "Username";
    public const string UserRole = "UserRole";
    public const string SessionId = "SessionId";
    public const string IpAddress = "IpAddress";

    // Device & PLC Context
    public const string DeviceId = "DeviceId";
    public const string DeviceName = "DeviceName";
    public const string DeviceProtocol = "DeviceProtocol";
    public const string TagId = "TagId";
    public const string TagName = "TagName";
    public const string PlcAddress = "PlcAddress";

    // Operation Context
    public const string Operation = "Operation";
    public const string OperationId = "OperationId";
    public const string OperationDurationMs = "OperationDurationMs";
    public const string OperationStatus = "OperationStatus"; // Started, Completed, Failed, Cancelled

    // Error & Diagnostics
    public const string ErrorCode = "ErrorCode";
    public const string ErrorMessage = "ErrorMessage";
    public const string ExceptionType = "ExceptionType";
    public const string StackTrace = "StackTrace";

    // Performance
    public const string ElapsedMs = "ElapsedMs";
    public const string CpuMs = "CpuMs";
    public const string MemoryBytes = "MemoryBytes";

    // Business Context
    public const string AlarmId = "AlarmId";
    public const string AlarmPriority = "AlarmPriority";
    public const string AlarmState = "AlarmState";
    public const string TransferBatchId = "TransferBatchId";
    public const string RecordsTransferred = "RecordsTransferred";
    public const string RetryCount = "RetryCount";
    public const string CircuitBreakerState = "CircuitBreakerState";
}

/// <summary>
/// Helper class for pushing structured logging properties into LogContext.
/// Usage:
/// <code>
/// using (StructuredLog.PushDevice(deviceId, deviceName))
/// using (StructuredLog.PushUser(userId, username))
/// using (StructuredLog.PushOperation("WriteTag", operationId))
/// {
///     logger.Information("Writing tag {TagName} = {Value}", tagName, value);
/// }
/// </code>
/// </summary>
public static class StructuredLog
{
    private static readonly List<IDisposable> _scopes = new();

    /// <summary>Pushes device context. Auto-popped on Dispose.</summary>
    public static IDisposable PushDevice(int deviceId, string deviceName, string? protocol = null)
    {
        var scope1 = LogContext.PushProperty(StructuredLogProperties.DeviceId, deviceId);
        var scope2 = LogContext.PushProperty(StructuredLogProperties.DeviceName, deviceName);
        if (protocol != null)
        {
            var scope3 = LogContext.PushProperty(StructuredLogProperties.DeviceProtocol, protocol);
            return new CompositeDisposable(scope1, scope2, scope3);
        }
        return new CompositeDisposable(scope1, scope2);
    }

    /// <summary>Pushes tag context. Auto-popped on Dispose.</summary>
    public static IDisposable PushTag(int tagId, string tagName, string? plcAddress = null)
    {
        var scope1 = LogContext.PushProperty(StructuredLogProperties.TagId, tagId);
        var scope2 = LogContext.PushProperty(StructuredLogProperties.TagName, tagName);
        if (plcAddress != null)
        {
            var scope3 = LogContext.PushProperty(StructuredLogProperties.PlcAddress, plcAddress);
            return new CompositeDisposable(scope1, scope2, scope3);
        }
        return new CompositeDisposable(scope1, scope2);
    }

    /// <summary>Pushes user context. Auto-popped on Dispose.</summary>
    public static IDisposable PushUser(int userId, string username, string? role = null)
    {
        var scope1 = LogContext.PushProperty(StructuredLogProperties.UserId, userId);
        var scope2 = LogContext.PushProperty(StructuredLogProperties.Username, username);
        if (role != null)
        {
            var scope3 = LogContext.PushProperty(StructuredLogProperties.UserRole, role);
            return new CompositeDisposable(scope1, scope2, scope3);
        }
        return new CompositeDisposable(scope1, scope2);
    }

    /// <summary>Pushes operation context with automatic timing. Auto-popped on Dispose.</summary>
    public static IDisposable PushOperation(string operation, string? operationId = null)
    {
        var id = operationId ?? Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();

        var scope1 = LogContext.PushProperty(StructuredLogProperties.Operation, operation);
        var scope2 = LogContext.PushProperty(StructuredLogProperties.OperationId, id);
        var scope3 = LogContext.PushProperty(StructuredLogProperties.OperationStatus, "Started");

        return new OperationScope(stopwatch, id, scope1, scope2, scope3);
    }

    /// <summary>Pushes alarm context. Auto-popped on Dispose.</summary>
    public static IDisposable PushAlarm(long alarmId, AlarmPriority? priority = null, AlarmState? state = null)
    {
        var scopes = new List<IDisposable>
        {
            LogContext.PushProperty(StructuredLogProperties.AlarmId, alarmId)
        };
        if (priority.HasValue) scopes.Add(LogContext.PushProperty(StructuredLogProperties.AlarmPriority, priority.Value.ToString()));
        if (state.HasValue) scopes.Add(LogContext.PushProperty(StructuredLogProperties.AlarmState, state.Value.ToString()));
        return new CompositeDisposable(scopes);
    }

    /// <summary>Pushes transfer batch context. Auto-popped on Dispose.</summary>
    public static IDisposable PushTransferBatch(long batchId, int recordsCount)
    {
        var scope1 = LogContext.PushProperty(StructuredLogProperties.TransferBatchId, batchId);
        var scope2 = LogContext.PushProperty(StructuredLogProperties.RecordsTransferred, recordsCount);
        return new CompositeDisposable(scope1, scope2);
    }

    /// <summary>Sets a property without automatic cleanup (manual cleanup required).</summary>
    public static void SetProperty(string key, object value, bool destructureObjects = false)
    {
        LogContext.PushProperty(key, value, destructureObjects);
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly List<IDisposable> _disposables;
        public CompositeDisposable(params IDisposable[] disposables) => _disposables = disposables.ToList();
        public CompositeDisposable(List<IDisposable> disposables) => _disposables = disposables;
        public void Dispose() { foreach (var d in _disposables) d.Dispose(); }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly Stopwatch _stopwatch;
        private readonly string _operationId;
        private readonly IDisposable _scope1;
        private readonly IDisposable _scope2;
        private readonly IDisposable _scope3;

        public OperationScope(Stopwatch stopwatch, string operationId, IDisposable s1, IDisposable s2, IDisposable s3)
        {
            _stopwatch = stopwatch;
            _operationId = operationId;
            _scope1 = s1; _scope2 = s2; _scope3 = s3;
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            LogContext.PushProperty(StructuredLogProperties.OperationStatus, "Completed");
            LogContext.PushProperty(StructuredLogProperties.OperationDurationMs, _stopwatch.ElapsedMilliseconds);
            _scope3.Dispose();
            _scope2.Dispose();
            _scope1.Dispose();
        }
    }
}