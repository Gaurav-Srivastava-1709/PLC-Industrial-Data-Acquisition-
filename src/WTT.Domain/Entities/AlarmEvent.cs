using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>
/// ISA-18.2 compliant alarm event log entry
/// </summary>
public class AlarmEvent
{
    public long Id { get; set; }
    
    // Which tag triggered the alarm
    public int DeviceTagId { get; set; }
    public DeviceTag? DeviceTag { get; set; }
    
    // Denormalized for fast queries (survive tag renames)
    public string DeviceName { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    
    // What happened
    public AlarmType AlarmType { get; set; }        // High or Low
    public AlarmState State { get; set; }            // Active, Acknowledged, Cleared, Shelved
    public AlarmPriority Priority { get; set; }
    
    // Values at the time of alarm
    public double TriggerValue { get; set; }         // Actual value jo thi
    public double ThresholdValue { get; set; }       // Limit jo cross hui
    public string? EngineeringUnit { get; set; }
    
    // Timestamps (ISA-18.2 requires full audit)
    public DateTime TriggeredAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime? ClearedAtUtc { get; set; }
    
    // Who acknowledged
    public string? AcknowledgedBy { get; set; }
    
    // Description for operator
    public string? Description { get; set; }
    
    // Duration (auto-calculated)
    public TimeSpan? Duration => ClearedAtUtc.HasValue 
        ? ClearedAtUtc.Value - TriggeredAtUtc 
        : DateTime.UtcNow - TriggeredAtUtc;
}
