namespace WTT.Application.DTOs;

/// <summary>
/// Alarm shelving state - Tag temporarily suppressed during maintenance.
/// ISA-18.2 compliant shelving information.
/// </summary>
public sealed class AlarmShelveState
{
    public int TagId { get; set; }
    public DateTime ShelvedAtUtc { get; set; }
    public DateTime UnshelveAtUtc { get; set; }
    public string ShelvedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
