using WTT.Application.DTOs;
using WTT.Domain.Entities;
using WTT.Domain.ValueObjects;

namespace WTT.Application.Interfaces;

public interface IAlarmService
{
    /// <summary>Ek tag ki value check karo threshold ke against (Called by CaptureWorker)</summary>
    Task EvaluateAsync(Device device, DeviceTag tag, double currentValue, CancellationToken ct);
    
    /// <summary>Active alarms ka count (badge ke liye)</summary>
    Task<int> GetActiveAlarmCountAsync(CancellationToken ct);
    
    /// <summary>Active alarms list</summary>
    Task<IReadOnlyList<AlarmEvent>> GetActiveAlarmsAsync(CancellationToken ct);
    
    /// <summary>Alarm history with pagination</summary>
    Task<(IReadOnlyList<AlarmEvent> Items, int TotalCount)> GetAlarmHistoryAsync(
        AlarmHistoryFilter filter, int page, int pageSize, CancellationToken ct);
    
    /// <summary>Operator acknowledge kare</summary>
    Task AcknowledgeAsync(long alarmEventId, string acknowledgedBy, CancellationToken ct);
    
    /// <summary>Bulk acknowledge</summary>
    Task AcknowledgeAllAsync(string acknowledgedBy, CancellationToken ct);

    /// <summary>Retention In-Out: Purge now (user clicked Delete Now) or set custom days.</summary>
    Task<int> PurgeNowAsync(int? retentionDaysOverride, CancellationToken ct);
    Task UpdateRetentionAsync(int retentionDays, CancellationToken ct);
    Task<int> GetRetentionDaysAsync(CancellationToken ct);

    /// <summary>Beep toggle In-Out</summary>
    Task<bool> GetBeepEnabledAsync(CancellationToken ct);
    Task SetBeepEnabledAsync(bool enabled, CancellationToken ct);

    // Events for UI subscription
    event EventHandler<AlarmTriggeredMessage>? AlarmTriggered;
    event EventHandler<AlarmClearedMessage>? AlarmCleared;
}
