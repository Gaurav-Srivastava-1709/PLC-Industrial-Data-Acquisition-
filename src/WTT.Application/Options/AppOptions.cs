namespace WTT.Application.Options;

/// <summary>Bound from the "Capture" configuration section.</summary>
public sealed class CaptureOptions
{
    public const string SectionName = "Capture";

    /// <summary>How often the supervisor reconciles workers with the device table.</summary>
    public int ReconcileIntervalSeconds { get; set; } = 5;

    /// <summary>Lower bound for device poll intervals.</summary>
    public int MinPollIntervalMs { get; set; } = 100;

    /// <summary>Initial delay before reconnecting after a connection loss.</summary>
    public int ReconnectInitialDelaySeconds { get; set; } = 2;

    /// <summary>Upper bound for exponential reconnect backoff.</summary>
    public int ReconnectMaxDelaySeconds { get; set; } = 60;

    /// <summary>How often live current values are flushed to the database.</summary>
    public int CurrentValueFlushSeconds { get; set; } = 5;

    /// <summary>Delete buffered readings that were already transferred and are older than this.</summary>
    public int TransferredRetentionDays { get; set; } = 7;

    /// <summary>Safety valve: readings never transferred but older than this are purged (0 = never).</summary>
    public int UntransferredMaxAgeDays { get; set; } = 0;
}

/// <summary>Bound from the "Transfer" configuration section.</summary>
public sealed class TransferOptions
{
    public const string SectionName = "Transfer";

    public int IntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 10000;
    public int BulkCopyBatchSize { get; set; } = 1000;
    public int BulkCopyTimeoutSeconds { get; set; } = 60;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int RetryCount { get; set; } = 3;
    public int CircuitBreakerFailures { get; set; } = 5;
    public int CircuitBreakerDurationSeconds { get; set; } = 60;
    public int BatchHistoryRetentionDays { get; set; } = 30;
}

/// <summary>Bound from the "Security" configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public int SessionTimeoutMinutes { get; set; } = 30;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 8;
    public bool RequireMixedCasePassword { get; set; } = true;
    public bool RequireDigitPassword { get; set; } = true;
    public int BcryptWorkFactor { get; set; } = 12;
}

/// <summary>Bound from the "Database" configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Path to the SQLite file. Supports %LocalAppData% style environment variables.
    /// Default: %LocalAppData%\WTT\wtt.db
    /// </summary>
    public string Path { get; set; } = "%LocalAppData%\\WTT\\wtt.db";
}

/// <summary>Bound from the "Simulation" configuration section.</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    /// <summary>Route every device to the built-in simulator instead of a real protocol driver.</summary>
    public bool UseMockPlc { get; set; }
}
