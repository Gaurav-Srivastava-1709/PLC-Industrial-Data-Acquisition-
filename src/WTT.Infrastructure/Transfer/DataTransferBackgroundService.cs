using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Infrastructure.Persistence;
using WTT.Infrastructure.Services;

namespace WTT.Infrastructure.Transfer;

/// <summary>
/// Ships buffered readings to each device's central SQL Server using SqlBulkCopy.
/// Idempotent: the destination table carries (SourceNode, SourceReadingId) with a unique index that
/// ignores duplicates, so a retry after an ambiguous failure can never double-insert.
/// Per-server retry + circuit breaker (Polly v8) isolate a failing server from the others.
/// </summary>
public sealed class DataTransferBackgroundService : BackgroundService, IDataTransferController
{
    private static readonly string SourceNode = Environment.MachineName;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SqlConnectionStringFactory _connStrings;
    private readonly IRuntimeSettings _runtime;
    private readonly TransferOptions _options;
    private readonly ILogger<DataTransferBackgroundService> _logger;

    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ResiliencePipeline> _pipelines = new();
    private readonly ConcurrentDictionary<string, DateTime> _schemaVerifiedUtc = new();
    private volatile bool _paused;

    public DataTransferBackgroundService(
        IDbContextFactory<AppDbContext> dbFactory,
        SqlConnectionStringFactory connStrings,
        IRuntimeSettings runtime,
        IOptions<TransferOptions> options,
        ILogger<DataTransferBackgroundService> logger)
    {
        _dbFactory = dbFactory;
        _connStrings = connStrings;
        _runtime = runtime;
        _options = options.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------- IDataTransferController

    public bool IsPaused => _paused;
    public void Pause() { _paused = true; _logger.LogWarning("Data transfer paused by user."); }
    public void Resume() { _paused = false; _logger.LogInformation("Data transfer resumed."); }

    public event EventHandler<TransferBatch>? BatchCompleted;

    public async Task<int> TransferNowAsync(int? deviceId = null, CancellationToken ct = default) =>
        await RunCycleAsync(deviceId, manual: true, ct);

    public async Task<TransferStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var todayUtc = DateTime.Today.ToUniversalTime();
        var pending = await db.TagReadings.LongCountAsync(r => !r.IsTransferred, ct);
        var transferredToday = await db.TransferBatches.Where(b => b.Status == TransferStatus.Success && b.StartedAtUtc >= todayUtc).SumAsync(b => (long)b.RecordCount, ct);
        var failedToday = await db.TransferBatches.LongCountAsync(b => b.Status == TransferStatus.Failed && b.StartedAtUtc >= todayUtc, ct);
        var lastSuccess = await db.DeviceServers.MaxAsync(s => s.LastSuccessAtUtc, ct);
        return new TransferStatistics(pending, transferredToday, failedToday, lastSuccess, !_paused);
    }

    public async Task<IReadOnlyList<TransferBatch>> GetRecentBatchesAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TransferBatches.AsNoTracking().OrderByDescending(b => b.Id).Take(take).ToListAsync(ct);
    }

    // ---------------------------------------------------------------- hosted service

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Data transfer service started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_paused) await RunCycleAsync(null, manual: false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Must never terminate the host.
                _logger.LogError(ex, "Transfer cycle failed unexpectedly; will retry next interval.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _runtime.TransferIntervalSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("Data transfer service stopped.");
    }

    // ---------------------------------------------------------------- cycle

    private async Task<int> RunCycleAsync(int? onlyDeviceId, bool manual, CancellationToken ct)
    {
        if (!await _cycleLock.WaitAsync(manual ? 10_000 : 0, ct))
        {
            if (manual) throw new Domain.Exceptions.DataTransferException("A transfer is already running. Try again in a moment.");
            return 0;
        }

        try
        {
            List<DeviceServer> servers;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                var q = db.DeviceServers.AsNoTracking().Include(s => s.Device).Where(s => s.Device!.IsEnabled);
                q = onlyDeviceId.HasValue ? q.Where(s => s.DeviceId == onlyDeviceId.Value) : q.Where(s => s.IsEnabled);
                servers = await q.ToListAsync(ct);
            }

            var total = 0;
            foreach (var server in servers)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Keep draining this device while full batches come back (catch-up after an outage).
                    int moved;
                    do
                    {
                        moved = await TransferDeviceAsync(server, ct);
                        total += moved;
                    } while (moved >= _options.BatchSize && !ct.IsCancellationRequested);
                }
                catch (BrokenCircuitException)
                {
                    _logger.LogDebug("{Device}: circuit open for {Server}; skipping this cycle.", server.Device!.Name, server.ServerName);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Device}: transfer to {Server}/{Db} failed.", server.Device!.Name, server.ServerName, server.DatabaseName);
                    await RecordFailureAsync(server, 0, ex.Message, DateTime.UtcNow, 0, ct);
                    if (manual) throw;
                }
            }
            return total;
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    private async Task<int> TransferDeviceAsync(DeviceServer server, CancellationToken ct)
    {
        var device = server.Device!;
        List<TagReading> batch;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            batch = await db.TagReadings.AsNoTracking()
                .Where(r => r.DeviceId == device.Id && !r.IsTransferred)
                .OrderBy(r => r.Id)
                .Take(_options.BatchSize)
                .ToListAsync(ct);
        }
        if (batch.Count == 0) return 0;

        var startedUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var connectionString = _connStrings.ForServer(server, connectTimeoutSeconds: 15);
        var pipeline = _pipelines.GetOrAdd($"{server.ServerName}|{server.DatabaseName}", _ => BuildPipeline(server));

        try
        {
            await pipeline.ExecuteAsync(async token =>
            {
                await using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(token);
                await EnsureDestinationSchemaAsync(conn, server.TableName, token);

                await using var tx = conn.BeginTransaction();
                using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.CheckConstraints, tx)
                {
                    DestinationTableName = $"[dbo].[{server.TableName}]",
                    BatchSize = _options.BulkCopyBatchSize,
                    BulkCopyTimeout = _options.BulkCopyTimeoutSeconds,
                };
                bulk.ColumnMappings.Add("TagName", "TagName");
                bulk.ColumnMappings.Add("PLCName", "PLCName");
                bulk.ColumnMappings.Add("TagValue", "TagValue");
                bulk.ColumnMappings.Add("TimeStamp", "TimeStamp");
                bulk.ColumnMappings.Add("TimeStampUTC", "TimeStampUTC");
                bulk.ColumnMappings.Add("Quality", "Quality");
                bulk.ColumnMappings.Add("ScanId", "ScanId");
                bulk.ColumnMappings.Add("SourceNode", "SourceNode");
                bulk.ColumnMappings.Add("SourceReadingId", "SourceReadingId");

                await bulk.WriteToServerAsync(ToDataTable(batch), token);
                await tx.CommitAsync(token);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFailureAsync(server, batch.Count, ex.Message, startedUtc, sw.ElapsedMilliseconds, ct);
            throw;
        }

        // Mark the contiguous id range as transferred (see class remarks on why the range is safe).
        var minId = batch[0].Id; var maxId = batch[^1].Id;
        var now = DateTime.UtcNow;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            await db.TagReadings
                .Where(r => r.DeviceId == device.Id && !r.IsTransferred && r.Id >= minId && r.Id <= maxId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsTransferred, true).SetProperty(r => r.TransferredAtUtc, now), ct);

            await db.DeviceServers.Where(s => s.Id == server.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSuccessAtUtc, now).SetProperty(x => x.LastError, (string?)null), ct);

            var record = new TransferBatch
            {
                DeviceId = device.Id, DeviceName = device.Name, TargetServer = server.ServerName, TargetDatabase = server.DatabaseName,
                RecordCount = batch.Count, Status = TransferStatus.Success, StartedAtUtc = startedUtc, CompletedAtUtc = now, DurationMs = sw.ElapsedMilliseconds,
            };
            db.TransferBatches.Add(record);
            await db.SaveChangesAsync(ct);
            BatchCompleted?.Invoke(this, record);
        }

        _logger.LogInformation("{Device}: transferred {Count} readings to {Server}/{Db}.{Table} in {Ms} ms.",
            device.Name, batch.Count, server.ServerName, server.DatabaseName, server.TableName, sw.ElapsedMilliseconds);
        return batch.Count;
    }

    private ResiliencePipeline BuildPipeline(DeviceServer server) => new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = Math.Max(0, _options.RetryCount),
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(2),
            UseJitter = true,
            ShouldHandle = new PredicateBuilder().Handle<SqlException>(IsTransient).Handle<TimeoutException>().Handle<InvalidOperationException>(e => e.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)),
            OnRetry = args =>
            {
                _logger.LogWarning("{Server}: transfer attempt {Attempt} failed ({Message}); retrying in {Delay}s.", server.ServerName, args.AttemptNumber + 1, args.Outcome.Exception?.Message, args.RetryDelay.TotalSeconds);
                return ValueTask.CompletedTask;
            },
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 1.0,
            MinimumThroughput = Math.Max(2, _options.CircuitBreakerFailures),
            SamplingDuration = TimeSpan.FromMinutes(5),
            BreakDuration = TimeSpan.FromSeconds(Math.Max(5, _options.CircuitBreakerDurationSeconds)),
            ShouldHandle = new PredicateBuilder().Handle<SqlException>().Handle<TimeoutException>(),
            OnOpened = args => { _logger.LogError("{Server}: circuit OPEN for {Duration}s after repeated failures: {Message}", server.ServerName, args.BreakDuration.TotalSeconds, args.Outcome.Exception?.Message); return ValueTask.CompletedTask; },
            OnClosed = _ => { _logger.LogInformation("{Server}: circuit closed; transfers resumed.", server.ServerName); return ValueTask.CompletedTask; },
        })
        .Build();

    private static bool IsTransient(SqlException ex) => ex.Number is -2 or 53 or 121 or 233 or 1205 or 4060 or 10053 or 10054 or 10060 or 40197 or 40501 or 40613 or 49918 or 49919 or 49920 or 11001 || ex.Class >= 20;

    /// <summary>
    /// Creates the legacy-compatible TagDetail table when missing, and upgrades an existing legacy table
    /// (adds idempotency/quality columns, widens PLCName/TagName). Verified once per hour per server/table.
    /// </summary>
    private async Task EnsureDestinationSchemaAsync(SqlConnection conn, string table, CancellationToken ct)
    {
        var key = $"{conn.DataSource}|{conn.Database}|{table}";
        if (_schemaVerifiedUtc.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalHours < 1) return;

        var sql = $"""
            IF OBJECT_ID(N'[dbo].[{table}]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[{table}] (
                    [Pk_TagDetail]    BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY CLUSTERED,
                    [TagName]         VARCHAR(200)  NOT NULL,
                    [PLCName]         VARCHAR(100)  NULL,
                    [TagValue]        VARCHAR(500)  NULL,
                    [TimeStamp]       DATETIME      NOT NULL,
                    [TimeStampUTC]    DATETIME      NULL,
                    [Quality]         VARCHAR(20)   NULL,
                    [ScanId]          UNIQUEIDENTIFIER NULL,
                    [SourceNode]      VARCHAR(100)  NULL,
                    [SourceReadingId] BIGINT        NULL
                );
            END;

            IF COL_LENGTH(N'dbo.{table}', 'Quality') IS NULL         ALTER TABLE [dbo].[{table}] ADD [Quality] VARCHAR(20) NULL;
            IF COL_LENGTH(N'dbo.{table}', 'ScanId') IS NULL          ALTER TABLE [dbo].[{table}] ADD [ScanId] UNIQUEIDENTIFIER NULL;
            IF COL_LENGTH(N'dbo.{table}', 'SourceNode') IS NULL      ALTER TABLE [dbo].[{table}] ADD [SourceNode] VARCHAR(100) NULL;
            IF COL_LENGTH(N'dbo.{table}', 'SourceReadingId') IS NULL ALTER TABLE [dbo].[{table}] ADD [SourceReadingId] BIGINT NULL;
            IF COL_LENGTH(N'dbo.{table}', 'TimeStampUTC') IS NULL    ALTER TABLE [dbo].[{table}] ADD [TimeStampUTC] DATETIME NULL;

            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = 'PLCName' AND max_length < 100)
                ALTER TABLE [dbo].[{table}] ALTER COLUMN [PLCName] VARCHAR(100) NULL;
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = 'TagName' AND max_length < 200)
                ALTER TABLE [dbo].[{table}] ALTER COLUMN [TagName] VARCHAR(200) NOT NULL;
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = 'TagValue' AND max_length < 500)
                ALTER TABLE [dbo].[{table}] ALTER COLUMN [TagValue] VARCHAR(500) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = 'UX_{table}_Source')
                CREATE UNIQUE NONCLUSTERED INDEX [UX_{table}_Source] ON [dbo].[{table}] ([SourceNode], [SourceReadingId])
                    WHERE [SourceNode] IS NOT NULL AND [SourceReadingId] IS NOT NULL
                    WITH (IGNORE_DUP_KEY = ON);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = 'IX_{table}_PLC_Time')
                CREATE NONCLUSTERED INDEX [IX_{table}_PLC_Time] ON [dbo].[{table}] ([PLCName], [TimeStamp]) INCLUDE ([TagName], [TagValue]);
            """;

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
        await cmd.ExecuteNonQueryAsync(ct);
        _schemaVerifiedUtc[key] = DateTime.UtcNow;
        _logger.LogDebug("Destination schema verified for {Key}.", key);
    }

    private static DataTable ToDataTable(List<TagReading> rows)
    {
        var t = new DataTable();
        t.Columns.Add("TagName", typeof(string));
        t.Columns.Add("PLCName", typeof(string));
        t.Columns.Add("TagValue", typeof(string));
        t.Columns.Add("TimeStamp", typeof(DateTime));
        t.Columns.Add("TimeStampUTC", typeof(DateTime));
        t.Columns.Add("Quality", typeof(string));
        t.Columns.Add("ScanId", typeof(Guid));
        t.Columns.Add("SourceNode", typeof(string));
        t.Columns.Add("SourceReadingId", typeof(long));
        foreach (var r in rows)
            t.Rows.Add(r.TagName, r.DeviceName, r.Value, r.Timestamp, r.TimestampUtc, r.Quality.ToString(), r.ScanId, SourceNode, r.Id);
        return t;
    }

    private async Task RecordFailureAsync(DeviceServer server, int count, string error, DateTime startedUtc, long ms, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            var msg = error.Length > 4000 ? error[..4000] : error;
            await db.DeviceServers.Where(s => s.Id == server.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastError, msg), CancellationToken.None);
            var record = new TransferBatch
            {
                DeviceId = server.DeviceId, DeviceName = server.Device?.Name ?? string.Empty, TargetServer = server.ServerName, TargetDatabase = server.DatabaseName,
                RecordCount = count, Status = TransferStatus.Failed, ErrorMessage = msg, StartedAtUtc = startedUtc, CompletedAtUtc = DateTime.UtcNow, DurationMs = ms,
            };
            db.TransferBatches.Add(record);
            await db.SaveChangesAsync(CancellationToken.None);
            BatchCompleted?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record transfer failure for {Server}.", server.ServerName);
        }
    }
}
