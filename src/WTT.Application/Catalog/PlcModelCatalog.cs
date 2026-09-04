using WTT.Application.DTOs;
using WTT.Domain.Enums;

namespace WTT.Application.Catalog;

/// <summary>
/// Static Protocol → Manufacturer → Model catalog (replaces the legacy ATIDAQ.XML).
/// Adding a model here is enough for it to appear in the UI; the driver factory
/// maps models to driver implementations.
/// </summary>
public static class PlcModelCatalog
{
    private const string LogixHint = "Symbolic tag, e.g. Program:Main.Speed or Data[3]";
    private const string Plc5Hint = "File address, e.g. N7:0, F8:3, B3:0/4, ST10:2, A9:0-10";
    private const string S7Hint = "S7 address, e.g. DB1.DBW8, DB1.DBD12, M0.1, I0.0, Q1.3";
    private const string ModbusHint = "Register number (1-based). Optional prefix HR:/IR:/CO:/DI:";

    public static readonly IReadOnlyList<PlcModelInfo> Models = new List<PlcModelInfo>
    {
        // Allen-Bradley
        new(DeviceModel.ControlLogix, "ControlLogix", Manufacturer.AllenBradley, ProtocolType.EthernetIp, 44818, true,  false, true,  false, true,  false, false, LogixHint),
        new(DeviceModel.CompactLogix, "CompactLogix", Manufacturer.AllenBradley, ProtocolType.EthernetIp, 44818, true,  false, true,  false, true,  false, false, LogixHint),
        new(DeviceModel.Micro800,     "Micro800",     Manufacturer.AllenBradley, ProtocolType.EthernetIp, 44818, false, false, false, false, false, false, false, LogixHint),
        new(DeviceModel.Plc5,         "PLC-5",        Manufacturer.AllenBradley, ProtocolType.EthernetIp, 44818, false, false, true,  false, false, false, false, Plc5Hint),
        new(DeviceModel.Slc500,       "SLC 500",      Manufacturer.AllenBradley, ProtocolType.EthernetIp, 44818, false, false, true,  false, false, false, false, Plc5Hint),
        new(DeviceModel.MicroLogix,   "MicroLogix",   Manufacturer.AllenBradley, ProtocolType.EthernetIp, 44818, false, false, false, false, false, false, false, Plc5Hint),

        // Siemens
        new(DeviceModel.S7_300,  "S7-300",  Manufacturer.Siemens, ProtocolType.SiemensS7, 102, false, false, true, true, false, false, false, S7Hint),
        new(DeviceModel.S7_400,  "S7-400",  Manufacturer.Siemens, ProtocolType.SiemensS7, 102, false, false, true, true, false, false, false, S7Hint),
        new(DeviceModel.S7_1200, "S7-1200", Manufacturer.Siemens, ProtocolType.SiemensS7, 102, false, false, true, true, false, false, false, S7Hint),
        new(DeviceModel.S7_1500, "S7-1500", Manufacturer.Siemens, ProtocolType.SiemensS7, 102, false, false, true, true, false, false, false, S7Hint),

        // Yokogawa
        new(DeviceModel.Mw100, "MW100", Manufacturer.Yokogawa, ProtocolType.ModbusTcp, 502, false, true, false, false, false, true, true, ModbusHint),
        new(DeviceModel.Gm10,  "GM10",  Manufacturer.Yokogawa, ProtocolType.ModbusTcp, 502, false, true, false, false, false, true, true, ModbusHint),

        // Advantech ADAM
        new(DeviceModel.Adam6017, "ADAM-6017", Manufacturer.Advantech, ProtocolType.ModbusTcp, 502, false, true, false, false, false, true, true, ModbusHint),
        new(DeviceModel.Adam6018, "ADAM-6018", Manufacturer.Advantech, ProtocolType.ModbusTcp, 502, false, true, false, false, false, true, true, ModbusHint),

        // Walchem
        new(DeviceModel.Walchem600, "W600", Manufacturer.Walchem, ProtocolType.ModbusTcp, 502, false, true, false, false, false, true, true, ModbusHint),

        // Generic
        new(DeviceModel.GenericModbusTcp, "Generic Modbus TCP", Manufacturer.Generic, ProtocolType.ModbusTcp, 502, false, true, false, false, false, true, true, ModbusHint),
    };

    public static PlcModelInfo Get(DeviceModel model) =>
        Models.FirstOrDefault(m => m.Model == model)
        ?? throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown device model.");

    public static IReadOnlyList<Manufacturer> ManufacturersFor(ProtocolType protocol) =>
        Models.Where(m => m.Protocol == protocol).Select(m => m.Manufacturer).Distinct().ToList();

    public static IReadOnlyList<PlcModelInfo> ModelsFor(ProtocolType protocol, Manufacturer manufacturer) =>
        Models.Where(m => m.Protocol == protocol && m.Manufacturer == manufacturer).ToList();

    public static IReadOnlyList<ProtocolType> Protocols => Enum.GetValues<ProtocolType>();

    /// <summary>Model-specific defaults applied when the user generates tags for a Sequential Modbus block.</summary>
    public static (TagDataType DataType, bool ZeroBasedIndex, int? DecimalPlaces, WordOrder WordOrder) SequentialDefaults(DeviceModel model) => model switch
    {
        DeviceModel.Mw100 => (TagDataType.Float32, false, 2, WordOrder.LowHigh),
        DeviceModel.Gm10 => (TagDataType.Float32, false, 1, WordOrder.LowHigh),
        DeviceModel.Adam6017 or DeviceModel.Adam6018 => (TagDataType.UInt16, true, null, WordOrder.LowHigh),
        DeviceModel.Walchem600 => (TagDataType.Float32, false, null, WordOrder.LowHigh),
        _ => (TagDataType.UInt16, false, null, WordOrder.LowHigh),
    };

    /// <summary>
    /// Yokogawa GM10 exposes channels in banks of 10, each bank 100 registers apart, 2 registers per channel.
    /// Channel 1 → offset 0, channel 11 → offset 100, channel 25 → offset 208.
    /// </summary>
    public static int Gm10ChannelRegisterOffset(int channel)
    {
        if (channel < 1) throw new ArgumentOutOfRangeException(nameof(channel));
        var bank = (channel - 1) / 10;
        var inBank = (channel - 1) % 10;
        return bank * 100 + inBank * 2;
    }
}
