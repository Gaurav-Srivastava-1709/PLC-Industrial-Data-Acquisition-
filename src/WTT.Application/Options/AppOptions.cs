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
    public int UntransferredMaxAgeDays { get; set; }
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
    public int MaxBatchesPerDevicePerCycle { get; set; } = 4;
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

/// <summary>Local SQL Server Express persistence settings.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Uses Windows authentication; no database password is stored by WTT.</summary>
    public string ConnectionString { get; set; } =
        "Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT;Connect Timeout=10;Pooling=True";
}

/// <summary>Bound from the "Simulation" configuration section.</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    /// <summary>Route every device to the built-in simulator instead of a real protocol driver.</summary>
    public bool UseMockPlc { get; set; }
}

/// <summary>Bound from the "Alarm" configuration section.</summary>
public sealed class AlarmOptions
{
    public const string SectionName = "Alarm";

    /// <summary>How many days to keep alarm history (configurable In-Out). 0 = keep forever.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Whether audible beep is enabled for Critical/High alarms. User can toggle silent.</summary>
    public bool BeepEnabled { get; set; } = true;

    /// <summary>Email notifications enabled for Critical/High priority alarms.</summary>
    public bool EmailEnabled { get; set; } = false;

    /// <summary>SMTP server hostname or IP address.</summary>
    public string? SmtpHost { get; set; }

    /// <summary>SMTP server port (default: 587 for TLS, 25 for unencrypted, 465 for SSL).</summary>
    public int? SmtpPort { get; set; } = 587;

    /// <summary>Use SSL/TLS for SMTP connection.</summary>
    public bool? SmtpUseSsl { get; set; } = true;

    /// <summary>SMTP authentication username (leave null for anonymous).</summary>
    public string? SmtpUsername { get; set; }

    /// <summary>SMTP authentication password.</summary>
    public string? SmtpPassword { get; set; }

    /// <summary>Email sender address (From field).</summary>
    public string? EmailFrom { get; set; }

    /// <summary>Comma-separated list of recipient email addresses.</summary>
    public string? EmailToCsv { get; set; }

    // Legacy properties for backward compatibility
    [Obsolete("Use SmtpHost instead")]
    public string? EmailSmtpHost { get => SmtpHost; set => SmtpHost = value; }

    [Obsolete("Use SmtpPort instead")]
    public int EmailSmtpPort { get => SmtpPort ?? 587; set => SmtpPort = value; }
}

