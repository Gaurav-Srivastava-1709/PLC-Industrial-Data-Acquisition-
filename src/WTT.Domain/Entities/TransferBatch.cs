using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>History of one bulk transfer attempt to a central server.</summary>
public class TransferBatch
{
    public long Id { get; set; }

    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string TargetServer { get; set; } = string.Empty;
    public string TargetDatabase { get; set; } = string.Empty;

    public int RecordCount { get; set; }
    public TransferStatus Status { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>Security / configuration audit trail.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Username { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Details { get; set; }
    public bool Success { get; set; } = true;
}

/// <summary>Runtime application setting stored in the local database (key/value).</summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
