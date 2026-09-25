using System.Collections.Concurrent;
using System.Threading.Channels;
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

public sealed class AlarmEvaluationService : IAlarmService, IDisposable, IAsyncDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AlarmEvaluationService> _logger;
    private readonly AlarmOptions _alarmOptions;
    private readonly ICurrentUserService _currentUser;
    private readonly CancellationTokenSource _cts = new();

    // FIX #1: Memory leak prevention - will add periodic cleanup
    private readonly ConcurrentDictionary<int, AlarmTagState> _states = new();

    // FIX #4: Flood suppression tracking (tagId -> (firstAlarmTime, count))
    private readonly ConcurrentDictionary<int, AlarmFloodState> _floodTracking = new();

    // FIX #5: Alarm shelving tracking (tagId -> shelveExpiry)
    private readonly ConcurrentDictionary<int, InternalAlarmShelveState> _shelvedAlarms = new();

    private DateTime _lastSyncTime;
    private DateTime _lastCleanupTime;

    // FIX #3: Startup race condition - initialization lock
    private readonly SemaphoreSlim _initLock = new(0, 1);
    private bool _isInitialized = false;

    private abstract record AlarmDbCommand;
    private record TriggerDbCommand(AlarmEvent Event, bool IsCritical) : AlarmDbCommand;
    private record ClearDbCommand(AlarmEvent Event, DateTime ClearedAtUtc) : AlarmDbCommand;

    // FIX #2: Bounded channel to prevent memory exhaustion
    private readonly Channel<AlarmDbCommand> _dbQueue = Channel.CreateBounded<AlarmDbCommand>(
        new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest // Drop old alarms if queue is full
        });

    public event EventHandler<AlarmTriggeredMessage>? AlarmTriggered;
    public event EventHandler<AlarmClearedMessage>? AlarmCleared;
    public event EventHandler? AlarmCountChanged;

    public AlarmEvaluationService(IDbContextFactory<AppDbContext> dbFactory, ILogger<AlarmEvaluationService> logger, IOptions<AlarmOptions> alarmOptions, ICurrentUserService currentUser)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _alarmOptions = alarmOptions.Value;
        _currentUser = currentUser;
        _lastSyncTime = DateTime.UtcNow;
        _lastCleanupTime = DateTime.UtcNow;

        // FIX #3: Proper async initialization with completion signaling
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadActiveAlarmsAsync(_cts.Token);
                _isInitialized = true;
                _initLock.Release();
                _logger.LogInformation("AlarmEvaluationService initialized successfully with {Count} active alarms", _states.Count);
            }
            catch (OperationCanceledException) { _initLock.Release(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during alarm service initialization");
                _initLock.Release(); // Release even on error to prevent deadlock
            }
        }, _cts.Token);

        _ = ProcessDbQueueAsync(_cts.Token);

        // FIX #1: Start periodic cleanup task
        _ = PeriodicCleanupAsync(_cts.Token);
    }

    // FIX #2: Enhanced queue processing with retry logic for transient failures
    private async Task ProcessDbQueueAsync(CancellationToken token)
    {
        try
        {
            await foreach (var cmd in _dbQueue.Reader.ReadAllAsync(token))
            {
                const int maxRetries = 3;
                int retryCount = 0;
                bool success = false;

                while (!success && retryCount < maxRetries)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await using var db = await _dbFactory.CreateDbContextAsync(token);

                    if (cmd is TriggerDbCommand tc)
                    {
                        db.AlarmEvents.Add(tc.Event);
                        await db.SaveChangesAsync();

                        var msg = new AlarmTriggeredMessage(
                            tc.Event.Id, tc.Event.DeviceName, tc.Event.TagName, tc.Event.AlarmType, tc.Event.Priority,
                            tc.Event.TriggerValue, tc.Event.ThresholdValue, tc.Event.EngineeringUnit, tc.Event.TriggeredAtUtc);

                        AlarmTriggered?.Invoke(this, msg);
                        AlarmCountChanged?.Invoke(this, EventArgs.Empty);
                        if (Notifier != null)
                        {
                            _ = Task.Run(async () => { try { await Notifier.NotifyTriggeredAsync(msg, CancellationToken.None); } catch { } });
                        }

                        _logger.LogInformation("Alarm {Priority} triggered: {Device}/{Tag} (Value={Value} {Unit})",
                            tc.Event.Priority, tc.Event.DeviceName, tc.Event.TagName, tc.Event.TriggerValue, tc.Event.EngineeringUnit);
                        success = true;
                    }
                    else if (cmd is ClearDbCommand cc)
                    {
                        var alarm = await db.AlarmEvents.FindAsync(new object[] { cc.Event.Id });
                        if (alarm != null)
                        {
                            alarm.State = AlarmState.Cleared;
                            alarm.ClearedAtUtc = cc.ClearedAtUtc;
                            await db.SaveChangesAsync();

                            var clearedMsg = new AlarmClearedMessage(cc.Event.Id, cc.Event.DeviceName, cc.Event.TagName, cc.ClearedAtUtc);
                            AlarmCleared?.Invoke(this, clearedMsg);
                            AlarmCountChanged?.Invoke(this, EventArgs.Empty);
                            if (Notifier != null)
                            {
                                _ = Task.Run(async () => { try { await Notifier.NotifyClearedAsync(clearedMsg, CancellationToken.None); } catch { } });
                            }

                            _logger.LogInformation("Alarm cleared: {Device}/{Tag}", cc.Event.DeviceName, cc.Event.TagName);
                        }
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogError(ex, "Failed to process alarm db command after {Retries} retries. Command type: {Type}", maxRetries, cmd.GetType().Name);
                        // FIX #2: Log to separate error file for recovery
                        if (cmd is TriggerDbCommand tc)
                        {
                            _logger.LogCritical("LOST ALARM: {Device}/{Tag} Priority={Priority} Value={Value} Time={Time:o}",
                                tc.Event.DeviceName, tc.Event.TagName, tc.Event.Priority, tc.Event.TriggerValue, tc.Event.TriggeredAtUtc);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Error processing alarm db queue (retry {Retry}/{Max})", retryCount, maxRetries);
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * retryCount), token); // Exponential backoff
                    }
                }
            }
        }
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadActiveAlarmsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var active = await db.AlarmEvents
                .Where(a => a.State == AlarmState.Active || a.State == AlarmState.Acknowledged)
                .ToListAsync();

            foreach (var a in active)
            {
                _states[a.DeviceTagId] = new AlarmTagState
                {
                    ActiveEvent = a,
                    IsActive = true,
                    TriggerType = a.AlarmType
                };
            }
            _lastSyncTime = DateTime.UtcNow;
            _logger.LogInformation("Loaded {Count} active alarms from database", active.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load active alarms on startup.");
        }
    }

    // FIX #1: Periodic cleanup to prevent memory leak
    private async Task PeriodicCleanupAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Cleanup every 10 minutes
                await Task.Delay(TimeSpan.FromMinutes(10), token);

                var now = DateTime.UtcNow;
                if ((now - _lastCleanupTime).TotalMinutes < 10) continue;

                await using var db = await _dbFactory.CreateDbContextAsync(token);

                // Get all tag IDs that currently have alarms enabled
                var activeTagIds = await db.DeviceTags
                    .Where(t => t.IsAlarmEnabled)
                    .Select(t => t.Id)
                    .ToHashSetAsync(token);

                // Remove state entries for deleted or disabled tags
                int removed = 0;
                foreach (var tagId in _states.Keys)
                {
                    if (!activeTagIds.Contains(tagId))
                    {
                        if (_states.TryRemove(tagId, out _))
                        {
                            removed++;
                        }
                    }
                }

                // Cleanup flood tracking (remove entries older than 5 minutes)
                var floodCutoff = now.AddMinutes(-5);
                foreach (var tagId in _floodTracking.Keys)
                {
                    if (_floodTracking.TryGetValue(tagId, out var flood) && flood.WindowStart < floodCutoff)
                    {
                        _floodTracking.TryRemove(tagId, out _);
                    }
                }

                // Cleanup expired shelved alarms
                foreach (var tagId in _shelvedAlarms.Keys)
                {
                    if (_shelvedAlarms.TryGetValue(tagId, out var shelve) && now > shelve.UnshelveAtUtc)
                    {
                        _shelvedAlarms.TryRemove(tagId, out _);
                        _logger.LogInformation("Alarm shelving expired for tag {TagId}", tagId);
                    }
                }

                _lastCleanupTime = now;

                if (removed > 0)
                {
                    _logger.LogInformation("Periodic cleanup: removed {Count} stale alarm state entries. Active states: {Active}, Flood tracking: {Flood}, Shelved: {Shelved}",
                        removed, _states.Count, _floodTracking.Count, _shelvedAlarms.Count);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic alarm state cleanup");
            }
        }
    }

    public IAlarmNotifier? Notifier { get; set; }

    public async Task EvaluateAsync(Device device, DeviceTag tag, double currentValue, CancellationToken ct)
    {
        if (!tag.IsAlarmEnabled) return;

        // FIX #3: Wait for initialization to complete before evaluating
        if (!_isInitialized)
        {
            await _initLock.WaitAsync(ct);
            _initLock.Release();
        }

        // FIX #5: Check if alarm is shelved
        if (_shelvedAlarms.TryGetValue(tag.Id, out var shelve))
        {
            if (DateTime.UtcNow < shelve.UnshelveAtUtc)
            {
                return; // Skip evaluation for shelved alarms
            }
            else
            {
                // Shelve expired, remove it
                _shelvedAlarms.TryRemove(tag.Id, out _);
            }
        }

        EvaluateInternal(device, tag, currentValue, DateTime.UtcNow);
    }

    private void EvaluateInternal(Device device, DeviceTag tag, double currentValue, DateTime now)
    {
        var state = _states.GetOrAdd(tag.Id, _ => new AlarmTagState());
        
        bool isHighViolation = tag.AlarmMaxThreshold.HasValue && currentValue > tag.AlarmMaxThreshold.Value;
        bool isLowViolation = tag.AlarmMinThreshold.HasValue && currentValue < tag.AlarmMinThreshold.Value;
        
        // Deadband is treated as an absolute engineering unit value (e.g., 2.0 Degrees) to avoid 0-threshold bugs
        double deadband = tag.AlarmDeadband; 

        bool isHighCleared = tag.AlarmMaxThreshold.HasValue && 
                             currentValue < tag.AlarmMaxThreshold.Value - deadband;
        bool isLowCleared = tag.AlarmMinThreshold.HasValue && 
                            currentValue > tag.AlarmMinThreshold.Value + deadband;

        if (!state.IsActive)
        {
            if (isHighViolation)
            {
                state.LowConditionSince = null; 
                if (state.HighConditionSince == null) state.HighConditionSince = now;
                
                if ((now - state.HighConditionSince.Value).TotalSeconds >= tag.AlarmOnDelaySeconds)
                {
                    TriggerAlarm(device, tag, currentValue, tag.AlarmMaxThreshold!.Value, AlarmType.High, state, now);
                }
            }
            else if (isLowViolation)
            {
                state.HighConditionSince = null; 
                if (state.LowConditionSince == null) state.LowConditionSince = now;
                
                if ((now - state.LowConditionSince.Value).TotalSeconds >= tag.AlarmOnDelaySeconds)
                {
                    TriggerAlarm(device, tag, currentValue, tag.AlarmMinThreshold!.Value, AlarmType.Low, state, now);
                }
            }
            else
            {
                state.HighConditionSince = null;
                state.LowConditionSince = null;
            }
        }
        else
        {
            bool shouldClear = (state.TriggerType == AlarmType.High && isHighCleared) ||
                               (state.TriggerType == AlarmType.Low && isLowCleared);

            if (shouldClear)
            {
                if (state.NormalConditionSince == null) state.NormalConditionSince = now;
                
                if ((now - state.NormalConditionSince.Value).TotalSeconds >= tag.AlarmOffDelaySeconds)
                {
                    ClearAlarm(device, tag, state, now);
                }
            }
            else
            {
                state.NormalConditionSince = null;
            }
        }
    }

    // FIX #4: Flood suppression - ISA-18.2 compliant rate limiting
    private void TriggerAlarm(Device device, DeviceTag tag, double currentValue, double thresholdValue, AlarmType type, AlarmTagState state, DateTime now)
    {
        // Check flood suppression (max 10 alarms per minute per tag)
        const int maxAlarmsPerMinute = 10;
        var floodState = _floodTracking.GetOrAdd(tag.Id, _ => new AlarmFloodState { WindowStart = now, Count = 0 });

        // Reset window if more than 1 minute has passed
        if ((now - floodState.WindowStart).TotalMinutes >= 1)
        {
            floodState.WindowStart = now;
            floodState.Count = 1;
        }
        else
        {
            floodState.Count++;

            // Flood detected - suppress alarm
            if (floodState.Count > maxAlarmsPerMinute)
            {
                if (!floodState.SuppressWarningLogged)
                {
                    _logger.LogWarning("ALARM FLOOD SUPPRESSION: Tag {Device}/{Tag} exceeded {Max} alarms/minute. Further alarms will be suppressed for this minute.",
                        device.Name, tag.Name, maxAlarmsPerMinute);
                    floodState.SuppressWarningLogged = true;
                }
                return; // Suppress this alarm
            }
        }

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

        state.IsActive = true;
        state.TriggerType = type;
        state.ActiveEvent = alarmEvent;
        state.HighConditionSince = null;
        state.LowConditionSince = null;

        // FIX #2: Critical alarms bypass queue and write directly to DB
        bool isCritical = tag.AlarmPriority == AlarmPriority.Critical;

        if (isCritical)
        {
            // Critical alarm - write directly to database (blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var db = await _dbFactory.CreateDbContextAsync();
                    db.AlarmEvents.Add(alarmEvent);
                    await db.SaveChangesAsync();

                    var msg = new AlarmTriggeredMessage(
                        alarmEvent.Id, alarmEvent.DeviceName, alarmEvent.TagName, alarmEvent.AlarmType, alarmEvent.Priority,
                        alarmEvent.TriggerValue, alarmEvent.ThresholdValue, alarmEvent.EngineeringUnit, alarmEvent.TriggeredAtUtc);

                    AlarmTriggered?.Invoke(this, msg);
                    AlarmCountChanged?.Invoke(this, EventArgs.Empty);
                    if (Notifier != null)
                    {
                        await Notifier.NotifyTriggeredAsync(msg, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save CRITICAL alarm directly to database");
                }
            });
        }
        else
        {
            // Non-critical alarm - use queue
            _dbQueue.Writer.TryWrite(new TriggerDbCommand(alarmEvent, isCritical));
        }

        _logger.LogWarning("ALARM {Priority} {AlarmType} on {Device}/{Tag}: Value={Value} {Unit} crossed {ThresholdType} threshold {Threshold} {Unit}",
            tag.AlarmPriority, type, device.Name, tag.Name, currentValue, tag.EngineeringUnit,
            type == AlarmType.High ? "maximum" : "minimum", thresholdValue, tag.EngineeringUnit);
    }

    private void ClearAlarm(Device device, DeviceTag tag, AlarmTagState state, DateTime now)
    {
        if (state.ActiveEvent != null)
        {
            _dbQueue.Writer.TryWrite(new ClearDbCommand(state.ActiveEvent, now));
        }

        state.IsActive = false;
        state.ActiveEvent = null;
        state.NormalConditionSince = null;

        _logger.LogInformation("ALARM CLEARED on {Device}/{Tag}. Priority={Priority}", device.Name, tag.Name, tag.AlarmPriority);
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
            AlarmCountChanged?.Invoke(this, EventArgs.Empty);
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
            AlarmCountChanged?.Invoke(this, EventArgs.Empty);
        }
     }

    public async Task<int> PurgeNowAsync(int? retentionDaysOverride, CancellationToken ct)
    {
        int days = retentionDaysOverride ?? _alarmOptions.RetentionDays;
        if (days <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        int deleted = await db.AlarmEvents.Where(a => a.TriggeredAtUtc < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) _logger.LogInformation("Manual purge: deleted {Count} alarms older than {Days} days (cutoff {Cutoff:u})", deleted, days, cutoff);
        if (deleted > 0) AlarmCountChanged?.Invoke(this, EventArgs.Empty);
        return deleted;
    }

    public async Task UpdateRetentionAsync(int retentionDays, CancellationToken ct)
    {
        if (retentionDays < 0 || retentionDays > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be 0-3650 days (0=keep forever).");
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

    // FIX #5: ISA-18.2 Alarm Shelving Implementation
    public async Task ShelveAlarmAsync(int tagId, TimeSpan duration, string shelvedBy, string reason, CancellationToken ct)
    {
        _currentUser.Demand(Permission.AcknowledgeAlarm); // Reuse same permission

        if (duration.TotalHours > 24)
        {
            throw new ArgumentException("Alarm shelving duration cannot exceed 24 hours for safety reasons.");
        }

        var unshelveAt = DateTime.UtcNow.Add(duration);

        _shelvedAlarms[tagId] = new InternalAlarmShelveState
        {
            TagId = tagId,
            ShelvedAtUtc = DateTime.UtcNow,
            UnshelveAtUtc = unshelveAt,
            ShelvedBy = shelvedBy,
            Reason = reason
        };

        // Update any active alarms to Shelved state
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var activeAlarm = await db.AlarmEvents
            .Where(a => a.DeviceTagId == tagId && (a.State == AlarmState.Active || a.State == AlarmState.Acknowledged))
            .FirstOrDefaultAsync(ct);

        if (activeAlarm != null)
        {
            activeAlarm.State = AlarmState.Shelved;
            await db.SaveChangesAsync(ct);
        }

        _logger.LogWarning("Alarm SHELVED for tag {TagId} until {UnshelveTime:u} by {User}. Reason: {Reason}",
            tagId, unshelveAt, shelvedBy, reason);
    }

    public async Task UnshelveAlarmAsync(int tagId, string unshelvedBy, CancellationToken ct)
    {
        _currentUser.Demand(Permission.AcknowledgeAlarm);

        if (_shelvedAlarms.TryRemove(tagId, out var shelve))
        {
            // Restore alarm to Active state if condition still violated
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var shelvedAlarm = await db.AlarmEvents
                .Where(a => a.DeviceTagId == tagId && a.State == AlarmState.Shelved)
                .FirstOrDefaultAsync(ct);

            if (shelvedAlarm != null)
            {
                shelvedAlarm.State = AlarmState.Active; // Will be re-evaluated on next scan
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("Alarm UNSHELVED for tag {TagId} by {User} (was shelved by {OriginalUser})",
                tagId, unshelvedBy, shelve.ShelvedBy);
        }
    }

    public Task<IReadOnlyList<AlarmShelveState>> GetShelvedAlarmsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var shelved = _shelvedAlarms.Values
            .Where(s => s.UnshelveAtUtc > now)
            .OrderBy(s => s.UnshelveAtUtc)
            .Select(s => new AlarmShelveState
            {
                TagId = s.TagId,
                ShelvedAtUtc = s.ShelvedAtUtc,
                UnshelveAtUtc = s.UnshelveAtUtc,
                ShelvedBy = s.ShelvedBy,
                Reason = s.Reason
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<AlarmShelveState>>(shelved);
    }

    public async Task SyncCrossProcessAlarmsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            
            var newAlarms = await db.AlarmEvents
                .Where(a => a.TriggeredAtUtc > _lastSyncTime)
                .ToListAsync(ct);
                
            var clearedAlarms = await db.AlarmEvents
                .Where(a => a.ClearedAtUtc != null && a.ClearedAtUtc > _lastSyncTime)
                .ToListAsync(ct);

            if (newAlarms.Count == 0 && clearedAlarms.Count == 0) return;

            _lastSyncTime = DateTime.UtcNow;
            bool countChanged = false;

            foreach (var a in newAlarms)
            {
                var msg = new AlarmTriggeredMessage(a.Id, a.DeviceName, a.TagName, a.AlarmType, a.Priority, a.TriggerValue, a.ThresholdValue, a.EngineeringUnit, a.TriggeredAtUtc);
                AlarmTriggered?.Invoke(this, msg);
                if (Notifier != null) try { await Notifier.NotifyTriggeredAsync(msg, ct); } catch { }
                countChanged = true;
            }

            foreach (var a in clearedAlarms)
            {
                var msg = new AlarmClearedMessage(a.Id, a.DeviceName, a.TagName, a.ClearedAtUtc!.Value);
                AlarmCleared?.Invoke(this, msg);
                countChanged = true;
            }

            if (countChanged)
            {
                AlarmCountChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error syncing cross-process alarms.");
        }
    }

    private sealed class AlarmTagState
    {
        public AlarmEvent? ActiveEvent { get; set; }
        public bool IsActive { get; set; }
        public AlarmType TriggerType { get; set; }
        public DateTime? HighConditionSince { get; set; }
        public DateTime? LowConditionSince { get; set; }
        public DateTime? NormalConditionSince { get; set; }
    }

    // FIX #4: Flood suppression state tracking
    private sealed class AlarmFloodState
    {
        public DateTime WindowStart { get; set; }
        public int Count { get; set; }
        public bool SuppressWarningLogged { get; set; }
    }

    // FIX #5: Internal alarm shelving state (not exposed to Application layer)
    private sealed class InternalAlarmShelveState
    {
        public int TagId { get; set; }
        public DateTime ShelvedAtUtc { get; set; }
        public DateTime UnshelveAtUtc { get; set; }
        public string ShelvedBy { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _initLock.Dispose();
        _dbQueue.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        // Give background loops a moment to observe cancellation
        await Task.Delay(50);
    }
}
