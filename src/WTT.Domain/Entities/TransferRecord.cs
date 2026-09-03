using System;

namespace WTT.Domain.Entities;

/// <summary>
/// TransferRecord: Har data transfer batch ka audit log.
/// Jaise courier company me har delivery ka receipt hota hai — kab, kitne parcels, success ya fail.
/// Isse Transfer page pe history grid me dikhayenge.
/// </summary>
public class TransferRecord
{
    public long Id { get; set; }

    /// <summary>Is batch me kitne TagReading records transfer hue</summary>
    public int BatchSize { get; set; }

    /// <summary>Transfer kab hua (UTC timestamp)</summary>
    public DateTime TransferredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Transfer ka result — Success, Failed, PartialSuccess</summary>
    public TransferStatus Status { get; set; } = TransferStatus.Pending;

    /// <summary>Agar error aayi toh uska message yaha save hoga</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Transfer me kitna time laga (milliseconds me)</summary>
    public long DurationMs { get; set; }

    /// <summary>Kis SQL Server pe transfer hua (audit ke liye)</summary>
    public string? TargetServer { get; set; }
}
