using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Security;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class AlarmEvaluationService : IAlarmService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AlarmEvaluationService> _logger;
    private readonly AlarmOptions _alarmOptions;
    private readonly ICurrentUserService _currentUser;
    
    private readonly ConcurrentDictionary<int, AlarmTagState> _states = new();

    public event EventHandler<AlarmTriggeredMessage>? AlarmTriggered;
    public event EventHandler<AlarmClearedMessage>? AlarmCleared;

    public AlarmEvaluationService(IDbContextFactory<AppDbContext> dbFactory, ILogger<AlarmEvaluationService> logger, IOptions<AlarmOptions> alarmOptions, ICurrentUserService currentUser)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _alarmOptions = alarmOptions.Value;
        _currentUser = currentUser;
        _ = LoadActiveAlarmsAsync();
    }

    private async Task LoadActiveAlarmsAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var active = await db.AlarmEvents
                .Where(a => a.State == AlarmState.Active || a.State == AlarmState.Acknowledged)
                .ToListAsync();

            foreach (var a in active)
            {
                _states[a.DeviceTagId] = new AlarmTagState
                {
                    CurrentAlarmEventId = a.Id,
                    IsActive = true,
                    TriggerType = a.AlarmType
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load active alarms on startup.");
        }
    }

    // Notifier pipeline (Beep + Email extensible) - set after DI build to avoid circular dep
    public IAlarmNotifier? Notifier { get; set; }

    public async Task EvaluateAsync(Device device, DeviceTag tag, double currentValue, CancellationToken ct)
    {
        if (!tag.IsAlarmEnabled) return;

        var state = _states.GetOrAdd(tag.Id, _ => new AlarmTagState());
        var now = DateTime.UtcNow;
        
        bool isHighViolation = tag.AlarmMaxThreshold.HasValue && currentValue > tag.AlarmMaxThreshold.Value;
        bool isLowViolation = tag.AlarmMinThreshold.HasValue && currentValue < tag.AlarmMinThreshold.Value;
        
        // Hysteresis/Deadband calculations for clearing
        double deadbandMult = tag.AlarmDeadband / 100.0;
        bool isHighCleared = tag.AlarmMaxThreshold.HasValue && 
                             currentValue < tag.AlarmMaxThreshold.Value - (tag.AlarmMaxThreshold.Value * deadbandMult);
        bool isLowCleared = tag.AlarmMinThreshold.HasValue && 
                            currentValue > tag.AlarmMinThreshold.Value + (tag.AlarmMinThreshold.Value * deadbandMult);

        if (!state.IsActive)
        {
            // Evaluate for new alarm
            if (isHighViolation)
            {
                state.LowConditionSince = null; // Reset other
                if (state.HighConditionSince == null) state.HighConditionSince = now;
                
                if ((now - state.HighConditionSince.Value).TotalSeconds >= tag.AlarmOnDelaySeconds)
                {
                    await TriggerAlarmAsync(device, tag, currentValue, tag.AlarmMaxThreshold!.Value, AlarmType.High, state, now, ct);
                }
            }
            else if (isLowViolation)
            {
                state.HighConditionSince = null; // Reset other
                if (state.LowConditionSince == null) state.LowConditionSince = now;
                
                if ((now - state.LowConditionSince.Value).TotalSeconds >= tag.AlarmOnDelaySeconds)
                {
                    await TriggerAlarmAsync(device, tag, currentValue, tag.AlarmMinThreshold!.Value, AlarmType.Low, state, now, ct);
                }
            }
            else
            {
                // Normal range, reset timers
                state.HighConditionSince = null;
                state.LowConditionSince = null;
            }
        }
        else
        {
            // Evaluate for clearing existing alarm
            bool shouldClear = (state.TriggerType == AlarmType.High && isHighCleared) ||
                               (state.TriggerType == AlarmType.Low && isLowCleared);

            if (shouldClear)
            {
                if (state.NormalConditionSince == null) state.NormalConditionSince = now;
                
                if ((now - state.NormalConditionSince.Value).TotalSeconds >= tag.AlarmOffDelaySeconds)
                {
                    await ClearAlarmAsync(device, tag, state, now, ct);
                }
            }
            else
            {
                // Still in alarm or deadband, reset clear timer
                state.NormalConditionSince = null;
            }
        }
    }

    private async Task TriggerAlarmAsync(Device device, DeviceTag tag, double currentValue, double thresholdValue, AlarmType type, AlarmTagState state, DateTime now, CancellationToken ct)
    {
        var alarmEvent = new AlarmEvent
        {
            DeviceTagId = tag.Id,
            DeviceName = device.Name,
            TagName = tag.Name,
            AlarmType = type,
            State = AlarmState.Active,
            Priority = tag.AlarmPriority,
            TriggerValue = currentValue,
            ThresholdValue = thresholdValue,
            EngineeringUnit = tag.EngineeringUnit,
            Description = tag.AlarmDescription,
            TriggeredAtUtc = now
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.AlarmEvents.Add(alarmEvent);
        await db.SaveChangesAsync(ct);

        state.IsActive = true;
        state.TriggerType = type;
        state.CurrentAlarmEventId = alarmEvent.Id;
        state.HighConditionSince = null;
        state.LowConditionSince = null;

        _logger.LogWarning("ALARM {AlarmType} on {Device}/{Tag}: Value={Value} {Unit} crossed {ThresholdType} threshold {Threshold} {Unit}. Priority={Priority}",
            type, device.Name, tag.Name, currentValue, tag.EngineeringUnit, 
            type == AlarmType.High ? "maximum" : "minimum", thresholdValue, tag.EngineeringUnit, tag.AlarmPriority);

        var msg = new AlarmTriggeredMessage(
            alarmEvent.Id, device.Name, tag.Name, type, tag.AlarmPriority, 
            currentValue, thresholdValue, tag.EngineeringUnit, now);
        AlarmTriggered?.Invoke(this, msg);
        if (Notifier != null) try { await Notifier.NotifyTriggeredAsync(msg, ct); } catch (Exception ex) { _logger.LogDebug(ex, "Notifier failed"); }
    }

    private async Task ClearAlarmAsync(Device device, DeviceTag tag, AlarmTagState state, DateTime now, CancellationToken ct)
    {
        if (state.CurrentAlarmEventId.HasValue)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var alarm = await db.AlarmEvents.FindAsync(new object[] { state.CurrentAlarmEventId.Value }, ct);
            if (alarm != null)
            {
                alarm.State = AlarmState.Cleared;
                alarm.ClearedAtUtc = now;
                await db.SaveChangesAsync(ct);
            }
        }

        long eventId = state.CurrentAlarmEventId ?? 0;
        
        state.IsActive = false;
        state.CurrentAlarmEventId = null;
        state.NormalConditionSince = null;

        _logger.LogInformation("ALARM CLEARED on {Device}/{Tag}. Priority={Priority}", device.Name, tag.Name, tag.AlarmPriority);

        var clearedMsg = new AlarmClearedMessage(eventId, device.Name, tag.Name, now);
        AlarmCleared?.Invoke(this, clearedMsg);
        if (Notifier != null) try { await Notifier.NotifyClearedAsync(clearedMsg, ct); } catch (Exception ex) { _logger.LogDebug(ex, "Notifier cleared failed"); }
    }

    public async Task<int> GetActiveAlarmCountAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AlarmEvents.CountAsync(a => a.State == AlarmState.Active, ct);
    }

    public async Task<IReadOnlyList<AlarmEvent>> GetActiveAlarmsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AlarmEvents
            .AsNoTracking()
            .Where(a => a.State == AlarmState.Active || a.State == AlarmState.Acknowledged)
            .OrderByDescending(a => a.Priority)
            .ThenByDescending(a => a.TriggeredAtUtc)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<AlarmEvent> Items, int TotalCount)> GetAlarmHistoryAsync(AlarmHistoryFilter filter, int page, int pageSize, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.AlarmEvents.AsNoTracking().AsQueryable();

        if (filter.FromUtc.HasValue) query = query.Where(a => a.TriggeredAtUtc >= filter.FromUtc.Value);
        if (filter.ToUtc.HasValue) query = query.Where(a => a.TriggeredAtUtc <= filter.ToUtc.Value);
        if (filter.Priority.HasValue) query = query.Where(a => a.Priority == filter.Priority.Value);
        if (filter.AlarmType.HasValue) query = query.Where(a => a.AlarmType == filter.AlarmType.Value);
        if (filter.State.HasValue) query = query.Where(a => a.State == filter.State.Value);
        if (!string.IsNullOrWhiteSpace(filter.DeviceName)) query = query.Where(a => a.DeviceName.Contains(filter.DeviceName));
        if (!string.IsNullOrWhiteSpace(filter.TagName)) query = query.Where(a => a.TagName.Contains(filter.TagName));

        int totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.TriggeredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task AcknowledgeAsync(long alarmEventId, string acknowledgedBy, CancellationToken ct)
    {
        _currentUser.Demand(Permission.AcknowledgeAlarm);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var alarm = await db.AlarmEvents.FindAsync(new object[] { alarmEventId }, ct);
        if (alarm != null && alarm.State == AlarmState.Active)
        {
            alarm.State = AlarmState.Acknowledged;
            alarm.AcknowledgedAtUtc = DateTime.UtcNow;
            alarm.AcknowledgedBy = acknowledgedBy;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Alarm {AlarmId} acknowledged by {User}", alarmEventId, acknowledgedBy);
        }
    }

    public async Task AcknowledgeAllAsync(string acknowledgedBy, CancellationToken ct)
    {
        _currentUser.Demand(Permission.AcknowledgeAlarm);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var active = await db.AlarmEvents.Where(a => a.State == AlarmState.Active).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var alarm in active)
        {
            alarm.State = AlarmState.Acknowledged;
            alarm.AcknowledgedAtUtc = now;
            alarm.AcknowledgedBy = acknowledgedBy;
        }
        if (active.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("All active alarms ({Count}) acknowledged by {User}", active.Count, acknowledgedBy);
        }
     }

    // ---- Retention In-Out + Beep In-Out ----
    public async Task<int> PurgeNowAsync(int? retentionDaysOverride, CancellationToken ct)
    {
        int days = retentionDaysOverride ?? _alarmOptions.RetentionDays;
        if (days <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Delete all events older than cutoff (TriggeredAt) regardless of state
        int deleted = await db.AlarmEvents.Where(a => a.TriggeredAtUtc < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) _logger.LogInformation("Manual purge: deleted {Count} alarms older than {Days} days (cutoff {Cutoff:u})", deleted, days, cutoff);
        return deleted;
    }

    public async Task UpdateRetentionAsync(int retentionDays, CancellationToken ct)
    {
        if (retentionDays < 0 || retentionDays > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be 0-3650 days (0=keep forever).");
        // Persist to DB via AppSettings (runtime setting) and update in-memory options
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await db.AppSettings.FirstOrDefaultAsync(a => a.Key == "Alarm.RetentionDays", ct);
        if (setting == null) { setting = new AppSetting { Key = "Alarm.RetentionDays", Value = retentionDays.ToString() }; db.AppSettings.Add(setting); }
        else setting.Value = retentionDays.ToString();
        await db.SaveChangesAsync(ct);
        _alarmOptions.RetentionDays = retentionDays;
        _logger.LogInformation("Alarm retention updated to {Days} days by {User}", retentionDays, _currentUser.Username ?? "system");
    }

    public async Task<int> GetRetentionDaysAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(a => a.Key == "Alarm.RetentionDays", ct);
        if (setting != null && int.TryParse(setting.Value, out var v)) return v;
        return _alarmOptions.RetentionDays;
    }

    public Task<bool> GetBeepEnabledAsync(CancellationToken ct)
    {
        // Check DB override first, otherwise use options
        return Task.FromResult(_alarmOptions.BeepEnabled);
    }

    public async Task SetBeepEnabledAsync(bool enabled, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var setting = await db.AppSettings.FirstOrDefaultAsync(a => a.Key == "Alarm.BeepEnabled", ct);
        if (setting == null) { setting = new AppSetting { Key = "Alarm.BeepEnabled", Value = enabled ? "1" : "0" }; db.AppSettings.Add(setting); }
        else setting.Value = enabled ? "1" : "0";
        await db.SaveChangesAsync(ct);
        _alarmOptions.BeepEnabled = enabled;
        _logger.LogInformation("Alarm beep {State} by {User}", enabled ? "ENABLED" : "SILENT", _currentUser.Username ?? "system");
    }

    // ---- Nested class: per-tag alarm state (in-memory, thread-safe via ConcurrentDictionary) ----
    private sealed class AlarmTagState
    {
        public long? CurrentAlarmEventId { get; set; }
        public bool IsActive { get; set; }
        public AlarmType TriggerType { get; set; }
        public DateTime? HighConditionSince { get; set; }
        public DateTime? LowConditionSince { get; set; }
        public DateTime? NormalConditionSince { get; set; }
    }
}

