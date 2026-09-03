using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Infrastructure.Data;

namespace WTT.Infrastructure.Services;

/// <summary>
/// DataTransferBackgroundService: Local SQLite DB se data SQL Server pe bulk transfer karta hai.
/// Resilience ke liye Polly (Retry + Circuit Breaker) aur performance ke liye SqlBulkCopy use karta hai.
/// </summary>
public class DataTransferBackgroundService : BackgroundService, IDataTransferService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataTransferBackgroundService> _logger;

    // State Variables
    private bool _isManualRunning = false;
    private DateTime? _lastSuccessfulTransferAt;
    private DateTime? _lastAttemptAt;
    private string _lastErrorMessage = string.Empty;
    private long _totalTransferredSession = 0;
    
    // Polly Policies
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;

    // Interfaces properties
    public bool IsRunning => _isManualRunning && ExecuteTask != null && !ExecuteTask.IsCompleted;
    public int TransferIntervalSeconds { get; set; } = 300; // 5 minutes default
    public int BatchSize { get; set; } = 1000;
    public int RetentionDays { get; set; } = 7; // Delete transferred records after 7 days

    public event EventHandler<TransferProgressEventArgs>? TransferProgress;
    public event EventHandler<bool>? ServiceStatusChanged;

    public DataTransferBackgroundService(
        IServiceProvider serviceProvider, 
        IConfiguration configuration,
        ILogger<DataTransferBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;

        LoadConfig();

        // 1. Retry Policy: 3 times retry, with exponential backoff (2s, 4s, 8s)
        _retryPolicy = Policy
            .Handle<SqlException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            (exception, timeSpan, retryCount, context) =>
            {
                _logger.LogWarning("SQL Transfer failed. Retry {RetryCount} after {Seconds}s. Error: {Message}", 
                    retryCount, timeSpan.TotalSeconds, exception.Message);
            });

        // 2. Circuit Breaker Policy: 5 consecutive failures break circuit for 60 seconds
        _circuitBreakerPolicy = Policy
            .Handle<SqlException>()
            .Or<TimeoutException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(60),
                onBreak: (ex, breakDelay) => _logger.LogError("Circuit Breaker OPENED for {Seconds}s due to: {Message}", breakDelay.TotalSeconds, ex.Message),
                onReset: () => _logger.LogInformation("Circuit Breaker RESET. Connection restored."),
                onHalfOpen: () => _logger.LogInformation("Circuit Breaker HALF-OPEN. Testing connection..."));
    }

    private void LoadConfig()
    {
        var interval = _configuration.GetValue<int?>("DataTransfer:IntervalSeconds");
        if (interval.HasValue) TransferIntervalSeconds = interval.Value;

        var batchSize = _configuration.GetValue<int?>("DataTransfer:BatchSize");
        if (batchSize.HasValue) BatchSize = batchSize.Value;

        var cleanup = _configuration.GetValue<int?>("DataTransfer:CleanupDays");
        if (cleanup.HasValue) RetentionDays = cleanup.Value;
    }

    public new Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isManualRunning = true;
        ServiceStatusChanged?.Invoke(this, true);
        _logger.LogInformation("Data Transfer Service manually started.");
        return Task.CompletedTask;
    }

    public new Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isManualRunning = false;
        ServiceStatusChanged?.Invoke(this, false);
        _logger.LogInformation("Data Transfer Service manually stopped.");
        return Task.CompletedTask;
    }

    public TransferStatistics GetStatistics()
    {
        int pending = 0;
        int errors = 0;
        
        try 
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pending = context.TagReadings.Count(r => !r.IsTransferred);
            errors = context.TransferRecords.Count(r => r.Status == TransferStatus.Failed);
        }
        catch { } // Ignore errors during stats fetch

        return new TransferStatistics
        {
            PendingRecordsCount = pending,
            TotalTransferredCount = _totalTransferredSession,
            LastSuccessfulTransferAt = _lastSuccessfulTransferAt,
            LastAttemptAt = _lastAttemptAt,
            LastErrorMessage = _lastErrorMessage,
            TransferErrorsCount = errors
        };
    }

    public async Task<bool> TestConnectionAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Test connection failed: {Message}", ex.Message);
            return false;
        }
    }

    public async Task TransferNowAsync()
    {
        _logger.LogInformation("Manual transfer triggered.");
        await ProcessDataTransferAsync(CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Data Transfer Background loop started. Interval: {Interval}s", TransferIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_isManualRunning)
            {
                await ProcessDataTransferAsync(stoppingToken);
                await CleanupOldTransferredRecordsAsync(stoppingToken);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(TransferIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private string GetSqlConnectionString()
    {
        // Construct connection string from AppSettings (CentralServer section)
        var serverName = _configuration["CentralServer:ServerName"];
        var dbName = _configuration["CentralServer:DatabaseName"];
        
        if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(dbName))
            return _configuration.GetConnectionString("CentralServer") ?? string.Empty;

        var useWindowsAuth = _configuration.GetValue<bool>("CentralServer:UseWindowsAuth");
        
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            InitialCatalog = dbName,
            Encrypt = false // For local/internal testing
        };

        if (useWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = _configuration["CentralServer:UserId"];
            builder.Password = _configuration["CentralServer:Password"];
        }

        return builder.ConnectionString;
    }

    private async Task ProcessDataTransferAsync(CancellationToken cancellationToken)
    {
        _lastAttemptAt = DateTime.UtcNow;
        var connectionString = GetSqlConnectionString();
        
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogWarning("Transfer skipped: Central SQL Server connection string is not configured.");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Find untransferred records
        var pendingRecords = await context.TagReadings
            .Include(r => r.DeviceTag)
            .ThenInclude(t => t.Device)
            .Where(r => !r.IsTransferred)
            .OrderBy(r => r.Timestamp)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pendingRecords.Count == 0)
            return;

        int totalPending = await context.TagReadings.CountAsync(r => !r.IsTransferred, cancellationToken);
        
        TransferProgress?.Invoke(this, new TransferProgressEventArgs 
        { 
            CurrentBatchTransferred = 0, 
            TotalPending = totalPending,
            Message = $"Starting transfer of {pendingRecords.Count} records..."
        });

        var transferRecord = new TransferRecord
        {
            BatchSize = pendingRecords.Count,
            TargetServer = new SqlConnectionStringBuilder(connectionString).DataSource,
            Status = TransferStatus.InProgress
        };
        
        context.TransferRecords.Add(transferRecord);
        await context.SaveChangesAsync(cancellationToken); // Save InProgress state

        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Execute with Circuit Breaker -> Retry -> Execution
            await _circuitBreakerPolicy.ExecuteAsync(async () =>
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    await PerformBulkTransferAsync(connectionString, pendingRecords, cancellationToken);
                });
            });

            // If we reach here, bulk transfer was successful
            sw.Stop();
            
            // Mark records as transferred in SQLite
            var ids = pendingRecords.Select(r => r.Id).ToList();
            await context.TagReadings
                .Where(r => ids.Contains(r.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsTransferred, true), cancellationToken);

            // Update Audit Record
            transferRecord.Status = TransferStatus.Success;
            transferRecord.DurationMs = sw.ElapsedMilliseconds;
            
            _lastSuccessfulTransferAt = DateTime.UtcNow;
            _totalTransferredSession += pendingRecords.Count;
            _lastErrorMessage = string.Empty;

            _logger.LogInformation("Successfully transferred {Count} records in {Ms}ms", pendingRecords.Count, sw.ElapsedMilliseconds);
            
            TransferProgress?.Invoke(this, new TransferProgressEventArgs 
            { 
                CurrentBatchTransferred = pendingRecords.Count, 
                TotalPending = totalPending - pendingRecords.Count,
                Message = $"Successfully transferred {pendingRecords.Count} records."
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _lastErrorMessage = ex.Message;
            
            transferRecord.Status = TransferStatus.Failed;
            transferRecord.ErrorMessage = ex.Message;
            transferRecord.DurationMs = sw.ElapsedMilliseconds;

            _logger.LogError(ex, "Bulk transfer failed after retries.");
            
            TransferProgress?.Invoke(this, new TransferProgressEventArgs 
            { 
                CurrentBatchTransferred = 0, 
                TotalPending = totalPending,
                Message = $"Transfer failed: {ex.Message}"
            });
        }
        finally
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task PerformBulkTransferAsync(string connectionString, List<TagReading> records, CancellationToken cancellationToken)
    {
        // Step 1: Map SQLite entities to SQL Server DataTable (Matches Old Schema)
        using var dt = new DataTable("TagDetail");
        dt.Columns.Add("Pk_TagDetail", typeof(int)); // Identity column, will be ignored during insert
        dt.Columns.Add("TagName", typeof(string));
        dt.Columns.Add("PLCName", typeof(string));
        dt.Columns.Add("TagValue", typeof(string));
        dt.Columns.Add("TimeStamp", typeof(DateTime));
        dt.Columns.Add("TimeStampUTC", typeof(DateTime));

        foreach (var r in records)
        {
            dt.Rows.Add(
                null, 
                r.DeviceTag.TagName, 
                r.DeviceTag.Device.Name, 
                r.Value, 
                r.Timestamp.ToLocalTime(), 
                r.Timestamp
            );
        }

        // Step 2: Ensure Destination Table Exists (Migration Strategy from Old Project)
        await EnsureDestinationTableExistsAsync(connectionString, cancellationToken);

        // Step 3: SqlBulkCopy
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        using var transaction = conn.BeginTransaction();
        
        using var bulkCopy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = "TagDetail",
            BatchSize = BatchSize,
            BulkCopyTimeout = 60
        };

        // Column Mappings
        bulkCopy.ColumnMappings.Add("TagName", "TagName");
        bulkCopy.ColumnMappings.Add("PLCName", "PLCName");
        bulkCopy.ColumnMappings.Add("TagValue", "TagValue");
        bulkCopy.ColumnMappings.Add("TimeStamp", "TimeStamp");
        bulkCopy.ColumnMappings.Add("TimeStampUTC", "TimeStampUTC");

        try
        {
            await bulkCopy.WriteToServerAsync(dt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task EnsureDestinationTableExistsAsync(string connectionString, CancellationToken cancellationToken)
    {
        string commandStr = @"
            IF NOT EXISTS (SELECT name FROM sysobjects WHERE name = 'TagDetail')
            BEGIN
                CREATE TABLE [dbo].[TagDetail](
                    [Pk_TagDetail] [int] IDENTITY(1,1) NOT NULL,
                    [TagName] [varchar](100) NOT NULL,
                    [PLCName] [varchar](50) NULL,
                    [TagValue] [varchar](500) NOT NULL,
                    [TimeStamp] [datetime] NOT NULL,
                    [TimeStampUTC] [datetime] NOT NULL,
                    CONSTRAINT [PK_TagDetail] PRIMARY KEY CLUSTERED ([Pk_TagDetail] ASC)
                ) ON [PRIMARY]
            END";

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        using var cmd = new SqlCommand(commandStr, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CleanupOldTransferredRecordsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var thresholdDate = DateTime.UtcNow.AddDays(-RetentionDays);
            
            // Only delete records that HAVE been transferred AND are older than retention days
            int deleted = await context.TagReadings
                .Where(r => r.IsTransferred && r.Timestamp < thresholdDate)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation("Cleaned up {Count} transferred records older than {Days} days.", deleted, RetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old transferred records.");
        }
    }
}
