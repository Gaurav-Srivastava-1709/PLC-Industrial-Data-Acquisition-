using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Security;
using WTT.Domain.Entities;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Capture;

/// <summary>
/// Supervisor: keeps exactly one <see cref="DeviceCaptureWorker"/> per enabled device, restarts
/// workers when their configuration changes and runs periodic retention cleanup.
/// </summary>
public sealed class DataCaptureBackgroundService : BackgroundService, IDataCaptureController
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IPlcDriverFactory _driverFactory;
    private readonly IExternalQueryService _externalQueries;
    private readonly IRuntimeSettings _runtime;
    private readonly LiveValueCache _cache;
    private readonly CaptureOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DataCaptureBackgroundService> _logger;
    private readonly ICurrentUserService _currentUser;
    private readonly ICaptureRuntimeStatusWriter _runtimeStatus;
    private readonly IAlarmService _alarmService;
    private readonly Guid _instanceId = Guid.NewGuid();
    private string? _serviceError;

    private readonly Dictionary<int, (DeviceCaptureWorker Worker, string Fingerprint)> _workers = new();
    private readonly Dictionary<int, DeviceCaptureStatus> _lastStatus = new();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly Channel<bool> _wakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private volatile bool _paused;
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public DataCaptureBackgroundService(
        IDbContextFactory<AppDbContext> dbFactory,
        IPlcDriverFactory driverFactory,
        IExternalQueryService externalQueries,
        IRuntimeSettings runtime,
        LiveValueCache cache,
        IOptions<CaptureOptions> options,
        ICurrentUserService currentUser,
        ICaptureRuntimeStatusWriter runtimeStatus,
        IAlarmService alarmService,
        ILoggerFactory loggerFactory,
        ILogger<DataCaptureBackgroundService> logger)
    {
        _dbFactory = dbFactory;
        _driverFactory = driverFactory;
        _externalQueries = externalQueries;
        _runtime = runtime;
        _cache = cache;
        _options = options.Value;
        _currentUser = currentUser;
        _runtimeStatus = runtimeStatus;
        _alarmService = alarmService;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _runtime.Changed += (_, _) => RequestReconcile();
    }

    // ---------------------------------------------------------------- IDataCaptureController

    public bool IsPaused => _paused;
    public void Pause() { _currentUser.Demand(Permission.ControlCapture); _paused = true; _logger.LogWarning("Data capture paused by user."); }
    public void Resume() { _currentUser.Demand(Permission.ControlCapture); _paused = false; _logger.LogInformation("Data capture resumed."); }
    public void RequestReconcile() => _wakeup.Writer.TryWrite(true);

    public event EventHandler<LiveTagValue>? TagValueChanged;
    public event EventHandler<DeviceCaptureStatus>? DeviceStatusChanged;

    public IReadOnlyList<DeviceCaptureStatus> GetStatuses()
    {
        _currentUser.Demand(Permission.ViewDashboard);
        lock (_workers) return _workers.Values.Select(w => w.Worker.Status).OrderBy(s => s.DeviceName).ToList();
    }

    public DeviceCaptureStatus? GetStatus(int deviceId)
    {
        _currentUser.Demand(Permission.ViewDashboard);
        lock (_workers) return _workers.TryGetValue(deviceId, out var w) ? w.Worker.Status : null;
    }

    public IReadOnlyList<LiveTagValue> GetLiveValues(int deviceId) { _currentUser.Demand(Permission.ViewLiveData); return _cache.GetAll(deviceId); }

    public async Task<CaptureStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ViewDashboard);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var todayLocalStartUtc = DateTime.Today.ToUniversalTime();
        var total = await db.Devices.CountAsync(d => d.IsEnabled, ct);
        var readingsToday = await db.TagReadings.LongCountAsync(r => r.TimestampUtc >= todayLocalStartUtc, ct);
        var buffered = await db.TagReadings.LongCountAsync(r => !r.IsTransferred, ct);
        var statuses = GetStatuses();
        return new CaptureStatistics(total, statuses.Count(s => s.IsRunning), statuses.Count(s => s.IsConnected), readingsToday, statuses.Sum(s => s.ErrorCount), buffered);
    }

    // ---------------------------------------------------------------- hosted service

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Data capture supervisor started (reconcile every {Sec}s).", _options.ReconcileIntervalSeconds);
        await _runtimeStatus.BeginSessionAsync(_instanceId, stoppingToken);
        try { await _runtime.ReloadAsync(stoppingToken); } catch (Exception ex) { _logger.LogWarning(ex, "Could not load runtime settings; using defaults."); }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.ReconcileIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runtime.ReloadAsync(stoppingToken);
                await ReconcileAsync(stoppingToken);
                await CleanupIfDueAsync(stoppingToken);
                _serviceError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _serviceError = ex.Message;
                _logger.LogError(ex, "Capture supervisor cycle failed; will retry.");
            }

            try { await _runtimeStatus.PublishAsync(_instanceId, CollectStatuses(), _serviceError, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Could not publish runtime heartbeat."); }

            // Sleep until the interval elapses or someone requests an immediate reconcile.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(interval);
            try { await _wakeup.Reader.ReadAsync(cts.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping data capture supervisor and {Count} worker(s)...", _workers.Count);
        await base.StopAsync(cancellationToken);
        List<DeviceCaptureWorker> workers;
        lock (_workers) { workers = _workers.Values.Select(w => w.Worker).ToList(); _workers.Clear(); }
        await Task.WhenAll(workers.Select(w => w.DisposeAsync().AsTask()));
        await _runtimeStatus.EndSessionAsync(_instanceId, CollectStatuses(), CancellationToken.None);
        _logger.LogInformation("Data capture supervisor stopped.");
    }

    private List<DeviceCaptureStatus> CollectStatuses()
    {
        lock (_workers)
        {
            var active = _workers.Values.Select(w => w.Worker.Status).ToDictionary(s => s.DeviceId);
            lock (_lastStatus)
                foreach (var status in _lastStatus.Values)
                    active.TryAdd(status.DeviceId, status);
            return active.Values.ToList();
        }
    }

    // ---------------------------------------------------------------- reconcile

    private async Task ReconcileAsync(CancellationToken ct)
    {
        if (!await _reconcileLock.WaitAsync(0, ct)) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var devices = await db.Devices.AsNoTracking()
                .Include(d => d.Tags).ThenInclude(t => t.Address)
                .Include(d => d.TriggerConditions)
                .Where(d => d.IsEnabled && (d.IsCaptureEnabled || d.IsMonitorEnabled))
                .ToListAsync(ct);

            var wanted = devices.ToDictionary(d => d.Id, d => (Device: d, Fingerprint: Fingerprint(d, _runtime.UseMockPlc)));
            var toStop = new List<DeviceCaptureWorker>();
            var toStart = new List<Device>();

            lock (_workers)
            {
                foreach (var (id, entry) in _workers.ToList())
                {
                    if (!wanted.TryGetValue(id, out var w) || w.Fingerprint != entry.Fingerprint)
                    {
                        toStop.Add(entry.Worker);
                        _workers.Remove(id);
                    }
                }
                foreach (var (id, w) in wanted)
                    if (!_workers.ContainsKey(id)) toStart.Add(w.Device);
            }

            foreach (var worker in toStop)
            {
                _logger.LogInformation("{Device}: stopping worker (configuration changed or device disabled).", worker.DeviceName);
                await worker.DisposeAsync();
                if (!wanted.ContainsKey(worker.DeviceId))
                {
                    _cache.ClearDevice(worker.DeviceId);
                    var stopped = DeviceCaptureStatus.Stopped(worker.DeviceId, worker.DeviceName);
                    lock (_lastStatus) _lastStatus[worker.DeviceId] = stopped;
                    DeviceStatusChanged?.Invoke(this, stopped);
                }
            }

            foreach (var device in toStart)
            {
                var worker = new DeviceCaptureWorker(device, _driverFactory, _dbFactory, _externalQueries, _cache, _options,
                    _loggerFactory.CreateLogger($"WTT.Capture.{device.Name}"),
                    _alarmService,
                    v => TagValueChanged?.Invoke(this, v),
                    s => { lock (_lastStatus) _lastStatus[s.DeviceId] = s; DeviceStatusChanged?.Invoke(this, s); },
                    () => _paused);
                lock (_workers) _workers[device.Id] = (worker, wanted[device.Id].Fingerprint);
                worker.Start();
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    /// <summary>Stable hash of everything a worker snapshots at start; any change restarts the worker.</summary>
    private static string Fingerprint(Device d, bool mock)
    {
        var sb = new StringBuilder(1024);
        sb.Append(d.Id).Append('|').Append(d.Name).Append('|').Append(d.UpdatedAtUtc?.Ticks).Append('|')
          .Append(d.Host).Append(':').Append(d.Port).Append('|').Append(d.PollIntervalMs).Append('|').Append(d.TimeoutMs).Append('|')
          .Append(d.Protocol).Append('/').Append(d.Model).Append('|').Append(d.Slot).Append('/').Append(d.Rack).Append('/').Append(d.CipPath).Append('/').Append(d.ConnectionSize).Append('|')
          .Append(d.UnitId).Append('/').Append(d.WordOrder).Append('/').Append(d.OneBasedAddressing).Append('|')
          .Append(d.CaptureMode).Append('/').Append(d.TriggerTagName).Append('|').Append(d.IsCaptureEnabled).Append('/').Append(d.IsMonitorEnabled).Append('|')
          .Append(mock).Append('|');
        foreach (var t in d.Tags.OrderBy(t => t.Id))
            sb.Append(t.Id).Append(',').Append(t.Name).Append(',').Append(t.Kind).Append(',').Append(t.DataType).Append(',').Append(t.PlcAddress).Append(',')
              .Append(t.RegisterType).Append(',').Append(t.ElementCount).Append(',').Append(t.IsSelected).Append(',').Append(t.ScaleFactor).Append(',').Append(t.ScaleOffset).Append(',')
              .Append(t.DecimalPlaces).Append(',').Append(t.Address?.RegisterType).Append(',').Append(t.Address?.DecimalPlaces).Append(',')
              .Append(t.Kind == Domain.Enums.TagKind.Custom ? t.SetValue : null).Append(',').Append(t.QueryColumnId).Append(';');
        foreach (var c in d.TriggerConditions.OrderBy(c => c.Order))
            sb.Append(c.Id).Append(',').Append(c.Order).Append(',').Append(c.Conjunction).Append(',').Append(c.TagName).Append(',').Append(c.Operator).Append(',').Append(c.Value).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ---------------------------------------------------------------- retention

    private async Task CleanupIfDueAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastCleanupUtc).TotalMinutes < 60) return;
        _lastCleanupUtc = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _runtime.TransferredRetentionDays));
        var deleted = await db.TagReadings.Where(r => r.IsTransferred && r.TimestampUtc < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) _logger.LogInformation("Retention: removed {Count} transferred readings older than {Cutoff:u}.", deleted, cutoff);

        if (_options.UntransferredMaxAgeDays > 0)
        {
            var hardCutoff = DateTime.UtcNow.AddDays(-_options.UntransferredMaxAgeDays);
            var purged = await db.TagReadings.Where(r => !r.IsTransferred && r.TimestampUtc < hardCutoff).ExecuteDeleteAsync(ct);
            if (purged > 0) _logger.LogWarning("Retention: PURGED {Count} readings that were never transferred (older than {Days} days). Check the central server connection!", purged, _options.UntransferredMaxAgeDays);
        }

        var batchCutoff = DateTime.UtcNow.AddDays(-30);
        await db.TransferBatches.Where(b => b.StartedAtUtc < batchCutoff).ExecuteDeleteAsync(ct);
        await db.AuditLogs.Where(a => a.TimestampUtc < DateTime.UtcNow.AddDays(-365)).ExecuteDeleteAsync(ct);
    }
}
