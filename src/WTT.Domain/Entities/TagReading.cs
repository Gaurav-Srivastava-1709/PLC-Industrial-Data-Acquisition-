using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>
/// One historised sample. This is the local store-and-forward buffer that the
/// transfer service ships to the central server (table "TagDetail").
/// </summary>
public class TagReading
{
    public long Id { get; set; }

    public int DeviceId { get; set; }

    /// <summary>Denormalised for fast transfer and to survive device renames.</summary>
    public string DeviceName { get; set; } = string.Empty;

    public string TagName { get; set; } = string.Empty;

    /// <summary>Invariant-culture string representation of the value.</summary>
    public string Value { get; set; } = string.Empty;

    public TagQuality Quality { get; set; }

    /// <summary>Local wall-clock time of the scan.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>UTC time of the scan.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>All rows produced by one poll share the same ScanId (enables row-wise pivoting downstream).</summary>
    public Guid ScanId { get; set; }

    public bool IsTransferred { get; set; }
    public DateTime? TransferredAtUtc { get; set; }
}
