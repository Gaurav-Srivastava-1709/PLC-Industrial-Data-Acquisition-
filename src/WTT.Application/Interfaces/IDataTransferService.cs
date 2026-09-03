using System;
using System.Threading;
using System.Threading.Tasks;

namespace WTT.Application.Interfaces;

/// <summary>
/// TransferStatistics: Data Transfer Status Page pe metrics dikhane ke liye use hoga.
/// Jaise total kitne records transfer hue, pending kitne hai.
/// </summary>
public class TransferStatistics
{
    public int PendingRecordsCount { get; set; }
    public long TotalTransferredCount { get; set; }
    public int TransferErrorsCount { get; set; }
    public DateTime? LastSuccessfulTransferAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
}

/// <summary>
/// TransferProgressEventArgs: UI ko live update dene ke liye.
/// Jaise progress bar 20% -> 40%
/// </summary>
public class TransferProgressEventArgs : EventArgs
{
    public int CurrentBatchTransferred { get; set; }
    public int TotalPending { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// IDataTransferService: Data Transfer Background Worker ka contract.
/// </summary>
public interface IDataTransferService
{
    bool IsRunning { get; }
    int TransferIntervalSeconds { get; set; }
    int BatchSize { get; set; }
    int RetentionDays { get; set; }

    event EventHandler<TransferProgressEventArgs>? TransferProgress;
    event EventHandler<bool>? ServiceStatusChanged;

    TransferStatistics GetStatistics();
    
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    
    /// <summary>Settings page se SQL Server connection test karne ke liye</summary>
    Task<bool> TestConnectionAsync(string connectionString);
    
    /// <summary>Manual transfer trigger karne ke liye (Transfer Now button)</summary>
    Task TransferNowAsync();
}
