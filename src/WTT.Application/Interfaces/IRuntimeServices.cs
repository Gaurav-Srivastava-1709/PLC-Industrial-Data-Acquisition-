using WTT.Application.DTOs;
using WTT.Domain.Entities;
using WTT.Domain.ValueObjects;

namespace WTT.Application.Interfaces;

public interface IInstallationIdentity
{
    Guid SourceId { get; }
}

/// <summary>Control surface and telemetry for the data capture pipeline.</summary>
public interface IDataCaptureController
{
    /// <summary>Global pause switch (workers stay alive but do not poll).</summary>
    bool IsPaused { get; }
    void Pause();
    void Resume();

    /// <summary>Force the supervisor to re-read device configuration now.</summary>
    void RequestReconcile();

    IReadOnlyList<DeviceCaptureStatus> GetStatuses();
    DeviceCaptureStatus? GetStatus(int deviceId);

    /// <summary>Latest value of every tag of a device (in-memory cache).</summary>
    IReadOnlyList<LiveTagValue> GetLiveValues(int deviceId);

    Task<CaptureStatistics> GetStatisticsAsync(CancellationToken ct = default);

    event EventHandler<LiveTagValue>? TagValueChanged;
    event EventHandler<DeviceCaptureStatus>? DeviceStatusChanged;
}

/// <summary>Control surface and telemetry for the central transfer pipeline.</summary>
public interface IDataTransferController
{
    bool IsPaused { get; }
    void Pause();
    void Resume();

    /// <summary>Run one transfer cycle immediately (all enabled servers, or one device).</summary>
    Task<int> TransferNowAsync(int? deviceId = null, CancellationToken ct = default);

    Task<TransferStatistics> GetStatisticsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TransferBatch>> GetRecentBatchesAsync(int take = 100, CancellationToken ct = default);

    event EventHandler<TransferBatch>? BatchCompleted;
}

/// <summary>Reads the rolling Serilog log files.</summary>
public interface ILogReaderService
{
    string LogDirectory { get; }
    Task<IReadOnlyList<LogEntry>> ReadRecentAsync(int maxLines = 1000, string? minimumLevel = null, string? contains = null, CancellationToken ct = default);
}

/// <summary>Cached runtime flags that background services consult on each cycle (backed by <see cref="IAppSettingsService"/>).</summary>
public interface IRuntimeSettings
{
    bool UseMockPlc { get; }
    int SessionTimeoutMinutes { get; }
    int TransferredRetentionDays { get; }
    int TransferIntervalSeconds { get; }

    Task ReloadAsync(CancellationToken ct = default);
    event EventHandler? Changed;
}
