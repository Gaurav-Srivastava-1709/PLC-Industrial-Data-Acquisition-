using WTT.Domain.Common;
using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

/// <summary>
/// A Modbus address block. For <see cref="ModbusAddressType.Sequential"/> blocks the
/// application auto-generates <see cref="PointCount"/> tags named "{TagPrefix}{index}".
/// For <see cref="ModbusAddressType.Discrete"/> blocks the user defines tags individually.
/// </summary>
public class DeviceAddress : Entity
{
    public int DeviceId { get; set; }
    public Device? Device { get; set; }

    public ModbusAddressType AddressType { get; set; } = ModbusAddressType.Discrete;
    public ModbusRegisterType RegisterType { get; set; } = ModbusRegisterType.HoldingRegisters;

    /// <summary>First register / coil address of the block (numbering per device setting).</summary>
    public int StartAddress { get; set; }

    /// <summary>Data type of each generated point (Sequential only).</summary>
    public TagDataType DataType { get; set; } = TagDataType.UInt16;

    /// <summary>Number of points to generate (Sequential only).</summary>
    public int PointCount { get; set; }

    /// <summary>Prefix for generated tag names (Sequential only).</summary>
    public string TagPrefix { get; set; } = string.Empty;

    /// <summary>Whether generated indexes start at 0 (ADAM style) or 1 (Yokogawa style).</summary>
    public bool ZeroBasedTagIndex { get; set; }

    /// <summary>Optional module offset added to every register address.</summary>
    public int ModuleOffset { get; set; }

    /// <summary>Optional rounding applied to generated point values (null = full precision).</summary>
    public int? DecimalPlaces { get; set; }

    public string? Description { get; set; }

    public ICollection<DeviceTag> Tags { get; set; } = new List<DeviceTag>();
}
