using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.ValueObjects;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Capture;

public sealed class SqlCaptureRuntimeStatusService : ICaptureRuntimeStatusReader, ICaptureRuntimeStatusWriter
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<SqlCaptureRuntimeStatusService> _logger;

    public SqlCaptureRuntimeStatusService(IDbContextFactory<AppDbContext> factory, ILogger<SqlCaptureRuntimeStatusService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task BeginSessionAsync(Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var state = await db.ServiceRuntimeStates.FindAsync([1], ct);
        var now = DateTime.UtcNow;
        if (state is null) db.ServiceRuntimeStates.Add(new ServiceRuntimeState { Id = 1, InstanceId = instanceId, StartedAtUtc = now, HeartbeatAtUtc = now });
        else { state.InstanceId = instanceId; state.StartedAtUtc = now; state.HeartbeatAtUtc = now; state.StoppedAtUtc = null; state.LastError = null; }
        await db.SaveChangesAsync(ct);
    }

    public async Task PublishAsync(Guid instanceId, IReadOnlyCollection<DeviceCaptureStatus> statuses, string? serviceError, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var service = await db.ServiceRuntimeStates.FindAsync([1], ct);
        if (service is null || service.InstanceId != instanceId) return;
        service.HeartbeatAtUtc = now;
        service.LastError = serviceError?.Length > 2000 ? serviceError[..2000] : serviceError;

        var ids = statuses.Select(s => s.DeviceId).ToList();
        var existing = await db.DeviceRuntimeStates.Where(s => ids.Contains(s.DeviceId)).ToDictionaryAsync(s => s.DeviceId, ct);
        foreach (var status in statuses)
        {
            if (!existing.TryGetValue(status.DeviceId, out var row)) { row = new DeviceRuntimeState { DeviceId = status.DeviceId }; db.DeviceRuntimeStates.Add(row); }
            Copy(row, status, instanceId, now);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task EndSessionAsync(Guid instanceId, IReadOnlyCollection<DeviceCaptureStatus> statuses, CancellationToken ct = default)
    {
        await PublishAsync(instanceId, statuses, null, ct);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var state = await db.ServiceRuntimeStates.FindAsync([1], ct);
        if (state?.InstanceId != instanceId) return;
        state.HeartbeatAtUtc = DateTime.UtcNow;
        state.StoppedAtUtc = DateTime.UtcNow;
        await db.DeviceRuntimeStates.Where(s => s.InstanceId == instanceId)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.WorkerRunning, false).SetProperty(s => s.IsConnected, false), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<CaptureRuntimeSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var service = await db.ServiceRuntimeStates.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
            if (service is null) return new(true, false, null, null, null, null, []);
            var alive = service.StoppedAtUtc is null && DateTime.UtcNow - service.HeartbeatAtUtc <= StaleAfter;
            var rows = await db.DeviceRuntimeStates.AsNoTracking().Include(s => s.Device).ToListAsync(ct);
            var statuses = rows.Select(row => new DeviceCaptureStatus(
                row.DeviceId, row.Device?.Name ?? $"Device {row.DeviceId}", alive && row.InstanceId == service.InstanceId && row.WorkerRunning,
                alive && row.InstanceId == service.InstanceId && row.IsConnected, row.IsCapturing, row.IsMonitoring, row.LastTriggerState,
                row.LastScanUtc, row.LastSuccessfulReadUtc, row.ScanCount, row.ReadingsWritten, row.ErrorCount, row.LastError, row.ReconnectAttempts)).ToList();
            return new(true, alive, service.InstanceId, service.StartedAtUtc, service.HeartbeatAtUtc, service.LastError, statuses);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read pipeline runtime status.");
            return new(false, false, null, null, null, ex.Message, []);
        }
    }

    private static void Copy(DeviceRuntimeState row, DeviceCaptureStatus status, Guid instanceId, DateTime now)
    {
        row.InstanceId = instanceId; row.WorkerRunning = status.IsRunning; row.IsConnected = status.IsConnected;
        row.IsCapturing = status.IsCapturing; row.IsMonitoring = status.IsMonitoring; row.LastTriggerState = status.LastTriggerState;
        row.LastScanUtc = status.LastScanUtc; row.LastSuccessfulReadUtc = status.LastSuccessfulReadUtc; row.StatusUpdatedAtUtc = now;
        row.ScanCount = status.ScanCount; row.ReadingsWritten = status.ReadingsWritten; row.ErrorCount = status.ErrorCount;
        row.ReconnectAttempts = status.ReconnectAttempts; row.LastError = status.LastError?.Length > 2000 ? status.LastError[..2000] : status.LastError;
    }
}
