namespace WTT.Domain.Entities;

public sealed class ServiceRuntimeState
{
    public int Id { get; set; } = 1;
    public Guid InstanceId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime HeartbeatAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class DeviceRuntimeState
{
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
    public Guid InstanceId { get; set; }
    public bool WorkerRunning { get; set; }
    public bool IsConnected { get; set; }
    public bool IsCapturing { get; set; }
    public bool IsMonitoring { get; set; }
    public bool? LastTriggerState { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public DateTime? LastSuccessfulReadUtc { get; set; }
    public DateTime StatusUpdatedAtUtc { get; set; }
    public long ScanCount { get; set; }
    public long ReadingsWritten { get; set; }
    public long ErrorCount { get; set; }
    public int ReconnectAttempts { get; set; }
    public string? LastError { get; set; }
}
