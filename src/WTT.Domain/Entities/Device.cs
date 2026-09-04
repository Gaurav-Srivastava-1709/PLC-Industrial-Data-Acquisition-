using WTT.Domain.Common;
using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>
/// A PLC / data-acquisition device. One capture worker is spawned per enabled device.
/// </summary>
public class Device : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ProtocolType Protocol { get; set; }
    public Manufacturer Manufacturer { get; set; }
    public DeviceModel Model { get; set; }

    /// <summary>IPv4 address or host name.</summary>
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }

    /// <summary>Poll period in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>Read/connect timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 3000;

    // ---- Protocol-specific settings ----

    /// <summary>Allen-Bradley processor slot / Siemens CPU slot.</summary>
    public int Slot { get; set; }

    /// <summary>Siemens rack number.</summary>
    public int Rack { get; set; }

    /// <summary>Allen-Bradley CIP connection size (511 or 4002). Governs batch read packing.</summary>
    public int ConnectionSize { get; set; } = 511;

    /// <summary>Optional explicit CIP routing path, e.g. "1,0". If empty it is derived from Slot.</summary>
    public string? CipPath { get; set; }

    /// <summary>Modbus unit / slave id.</summary>
    public byte UnitId { get; set; } = 1;

    /// <summary>Modbus word order for 32/64-bit values.</summary>
    public WordOrder WordOrder { get; set; } = WordOrder.LowHigh;

    /// <summary>Modbus register numbering: when true addresses entered by users are 1-based (40001 style semantics).</summary>
    public bool OneBasedAddressing { get; set; } = true;

    // ---- Capture configuration ----

    public CaptureMode CaptureMode { get; set; } = CaptureMode.Manual;

    /// <summary>Tag name used when <see cref="CaptureMode"/> is <see cref="CaptureMode.PlcTrigger"/>.</summary>
    public string? TriggerTagName { get; set; }

    /// <summary>Historise scans to the local buffer.</summary>
    public bool IsCaptureEnabled { get; set; }

    /// <summary>Keep current values refreshed for live monitoring even when not capturing.</summary>
    public bool IsMonitorEnabled { get; set; }

    /// <summary>
    /// Latch used to refresh external (SQL) context tags once per trigger cycle.
    /// Reset to false whenever the trigger condition becomes false or configuration is saved.
    /// </summary>
    public bool ExternalContextLoaded { get; set; }

    /// <summary>Master switch. Disabled devices are ignored by all background services.</summary>
    public bool IsEnabled { get; set; } = true;

    // ---- Navigation ----
    public ICollection<DeviceAddress> Addresses { get; set; } = new List<DeviceAddress>();
    public ICollection<DeviceTag> Tags { get; set; } = new List<DeviceTag>();
    public ICollection<DeviceQuery> Queries { get; set; } = new List<DeviceQuery>();
    public ICollection<DeviceTriggerCondition> TriggerConditions { get; set; } = new List<DeviceTriggerCondition>();
    public DeviceServer? Server { get; set; }
}
