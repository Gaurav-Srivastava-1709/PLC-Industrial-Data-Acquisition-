using WTT.Domain.Common;
using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>
/// A single data point belonging to a device.
/// </summary>
public class DeviceTag : AuditableEntity
{
    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>Owning Modbus block (Modbus devices only).</summary>
    public int? AddressId { get; set; }
    public DeviceAddress? Address { get; set; }

    /// <summary>Unique (per device) tag name. This is the key used in the historian.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional human friendly display name.</summary>
    public string? Alias { get; set; }

    public TagKind Kind { get; set; } = TagKind.Plc;
    public TagDataType DataType { get; set; } = TagDataType.Float32;

    /// <summary>
    /// Protocol address:
    ///  - Logix: symbolic tag name, e.g. "Program:Main.Speed" or "Data[3]"
    ///  - PLC-5 / SLC-500: file address, e.g. "N7:0", "F8:3", "B3:0/4", "ST10:2"
    ///  - Siemens S7: "DB1.DBW8", "DB1.DBD12", "M0.1", "I0.0", "Q1.3"
    ///  - Modbus: register/coil number, optionally prefixed "HR:", "IR:", "CO:", "DI:"
    /// For Custom/External tags this is empty.
    /// </summary>
    public string PlcAddress { get; set; } = string.Empty;

    /// <summary>Modbus register type override; when null the owning <see cref="Address"/> block type is used.</summary>
    public ModbusRegisterType? RegisterType { get; set; }

    /// <summary>Array length / string capacity (1 for scalars).</summary>
    public int ElementCount { get; set; } = 1;

    /// <summary>Size in bytes of one element (used for CIP packet packing / PLC5 element size).</summary>
    public int ByteCount { get; set; }

    /// <summary>Whether this tag is part of the polled set.</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>Value for Custom tags (set by user) and External tags (set by query refresh).</summary>
    public string? SetValue { get; set; }

    /// <summary>Optional linear scaling: engineering = raw * ScaleFactor + ScaleOffset.</summary>
    public double? ScaleFactor { get; set; }
    public double? ScaleOffset { get; set; }

    /// <summary>Optional rounding for numeric values (null = full precision).</summary>
    public int? DecimalPlaces { get; set; }

    public string? EngineeringUnit { get; set; }

    // ---- Live state (refreshed by capture worker) ----
    public string? CurrentValue { get; set; }
    public TagQuality CurrentQuality { get; set; } = TagQuality.Uncertain;
    public DateTime? CurrentValueAtUtc { get; set; }

    // ---- External tag binding ----
    public int? QueryId { get; set; }
    public DeviceQuery? Query { get; set; }
    public int? QueryColumnId { get; set; }
    public DeviceQueryColumn? QueryColumn { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias!;
}
