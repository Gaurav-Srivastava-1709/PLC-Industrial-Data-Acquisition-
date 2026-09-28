using WTT.Domain.Entities;
using WTT.Domain.Enums;

namespace WTT.Application.Catalog;

/// <summary>
/// Pre-defined default tag collection for each DeviceModel.
/// Industry pattern: Template Seeding — auto-create sensible starter tags
/// based on the protocol when a new device is added.
///
/// RULE: Address format = raw register number (1-based or 0-based per model).
///       Do not use "HR:40001" format — the driver handles OneBasedAddressing itself.
///       Example: ADAM ZeroBasedIndex=true → address "0","1","2"...
///                MW100 OneBasedIndex → address "1","3","5"... (2 regs per Float32)
/// </summary>
public static class TagTemplateLibrary
{
    /// <summary>
    /// Returns the default DeviceTag list for the given model.
    /// DeviceId is left as 0 — the caller sets it directly before saving.
    /// </summary>
    public static IReadOnlyList<DeviceTag> GetTemplateFor(DeviceModel model) => model switch
    {
        // ── Allen-Bradley (starter tags also exist for browse-capable models
        //    in case browsing fails or the device is added offline) ──────────────
        DeviceModel.ControlLogix or DeviceModel.CompactLogix => LogixStarterTags(),
        DeviceModel.Plc5 or DeviceModel.Slc500               => Plc5StarterTags(),
        DeviceModel.MicroLogix or DeviceModel.Micro800        => MicroLogixStarterTags(),

        // ── Siemens S7 — browsing is not supported (S7comm has no symbol table)
        //    SiemensS7Driver.cs:35 CanBrowseTags=false
        //    SiemensS7Driver.cs:157 throw NotSupportedException ──────────────
        DeviceModel.S7_300 or DeviceModel.S7_400
            or DeviceModel.S7_1200 or DeviceModel.S7_1500    => SiemensS7StarterTags(),

        // ── Yokogawa (Modbus TCP) ──────────────────────────────────────────
        DeviceModel.Mw100    => Mw100Tags(),
        DeviceModel.Gm10     => Gm10Tags(),

        // ── Advantech ADAM (Modbus TCP) ────────────────────────────────────
        DeviceModel.Adam6017 => Adam6017Tags(),
        DeviceModel.Adam6018 => Adam6018Tags(),

        // ── Walchem (Modbus TCP) ───────────────────────────────────────────
        DeviceModel.Walchem600 => WalchemW600Tags(),

        // ── Generic fallback ───────────────────────────────────────────────
        _ => GenericModbusTags(),
    };

    // ──────────────────────────────────────────────────────────────────────────
    // Allen-Bradley Logix — symbolic tag names (browse fallback starters)
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> LogixStarterTags() =>
    [
        MakePlcTag("Speed",   "Speed",   TagDataType.Float32),
        MakePlcTag("Torque",  "Torque",  TagDataType.Float32),
        MakePlcTag("Running", "Running", TagDataType.Bool),
        MakePlcTag("Fault",   "Fault",   TagDataType.Bool),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // Allen-Bradley PLC-5 / SLC-500 — file address format e.g. N7:0
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> Plc5StarterTags() =>
    [
        MakePlcTag("N7_0",  "N7:0",  TagDataType.Int16),
        MakePlcTag("N7_1",  "N7:1",  TagDataType.Int16),
        MakePlcTag("F8_0",  "F8:0",  TagDataType.Float32),
        MakePlcTag("B3_0",  "B3:0/0", TagDataType.Bool),
    ];

    private static IReadOnlyList<DeviceTag> MicroLogixStarterTags() =>
    [
        MakePlcTag("N7_0", "N7:0", TagDataType.Int16),
        MakePlcTag("N7_1", "N7:1", TagDataType.Int16),
        MakePlcTag("B3_0", "B3:0/0", TagDataType.Bool),
        MakePlcTag("B3_1", "B3:0/1", TagDataType.Bool),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // Siemens S7 — DB address format. Browsing is unsupported, hence starter tags.
    // The user enters the real addresses from TIA Portal later.
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> SiemensS7StarterTags() =>
    [
        MakePlcTag("Speed",   "DB1.DBD0", TagDataType.Float32),
        MakePlcTag("Torque",  "DB1.DBD4", TagDataType.Float32),
        MakePlcTag("Running", "M0.0",     TagDataType.Bool),
        MakePlcTag("Fault",   "M0.1",     TagDataType.Bool),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // Yokogawa MW100 — 20 measurement channels
    // Float32 = 2 registers → +2 per channel, 1-based addressing
    // PlcModelCatalog:64 → (Float32, ZeroBasedIndex:false, 2 decimal, LowHigh)
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> Mw100Tags()
    {
        var tags = new List<DeviceTag>(20);
        for (var i = 0; i < 20; i++)
        {
            // 1-based: channel 1 → register 1, channel 2 → register 3 ...
            var reg = 1 + i * 2;
            tags.Add(MakeModbusTag(
                name: $"CH{i + 1:000}",
                address: reg.ToString(),
                dataType: TagDataType.Float32,
                registerType: ModbusRegisterType.HoldingRegisters,
                decimalPlaces: 2,
                unit: string.Empty));
        }
        return tags;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Yokogawa GM10 — 10 channels with bank layout
    // Uses the exact same logic as PlcModelCatalog.Gm10ChannelRegisterOffset().
    // DeviceAddressService.cs:125-127 does the same.
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> Gm10Tags()
    {
        var tags = new List<DeviceTag>(10);
        for (var i = 0; i < 10; i++)
        {
            // GM10 bank offset: offset for channel i+1
            var offset = PlcModelCatalog.Gm10ChannelRegisterOffset(i + 1);
            var reg = 1 + offset; // 1-based start address
            tags.Add(MakeModbusTag(
                name: $"CH{i + 1:000}",
                address: reg.ToString(),
                dataType: TagDataType.Float32,
                registerType: ModbusRegisterType.HoldingRegisters,
                decimalPlaces: 1,
                unit: string.Empty));
        }
        return tags;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Advantech ADAM-6017 — 8 Analog Input channels (0–20 mA)
    // PlcModelCatalog:66 → (UInt16, ZeroBasedIndex:true)
    // UInt16 = 1 register → address 0,1,2,3,4,5,6,7 (zero-based raw)
    // Driver OneBasedAddressing: false for ADAM (zero-based per catalog)
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> Adam6017Tags()
    {
        var tags = new List<DeviceTag>(8);
        for (var i = 0; i < 8; i++)
        {
            tags.Add(MakeModbusTag(
                name: $"AI_CH{i:000}",      // CH000..CH007 (zero-based per catalog)
                address: i.ToString(),        // raw register 0,1,2...7
                dataType: TagDataType.UInt16,
                registerType: ModbusRegisterType.HoldingRegisters,
                decimalPlaces: null,
                unit: "mA"));
        }
        return tags;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Advantech ADAM-6018 — 8 Thermocouple Input channels
    // Same addressing as 6017
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> Adam6018Tags()
    {
        var tags = new List<DeviceTag>(8);
        for (var i = 0; i < 8; i++)
        {
            tags.Add(MakeModbusTag(
                name: $"TC_CH{i:000}",
                address: i.ToString(),
                dataType: TagDataType.UInt16,
                registerType: ModbusRegisterType.HoldingRegisters,
                decimalPlaces: null,
                unit: "\u00B0C"));
        }
        return tags;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Walchem W600 — Common process measurement variables
    // Float32 = 2 registers per tag, 1-based
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> WalchemW600Tags() =>
    [
        MakeModbusTag("pH",           "1",  TagDataType.Float32, ModbusRegisterType.HoldingRegisters, 2, "pH"),
        MakeModbusTag("Conductivity", "3",  TagDataType.Float32, ModbusRegisterType.HoldingRegisters, 2, "\u00B5S/cm"),
        MakeModbusTag("Temperature",  "5",  TagDataType.Float32, ModbusRegisterType.HoldingRegisters, 1, "\u00B0C"),
        MakeModbusTag("ORP",          "7",  TagDataType.Float32, ModbusRegisterType.HoldingRegisters, 0, "mV"),
        MakeModbusTag("Flow",         "9",  TagDataType.Float32, ModbusRegisterType.HoldingRegisters, 1, "L/min"),
        MakeModbusTag("Level",        "11", TagDataType.Float32, ModbusRegisterType.HoldingRegisters, 1, "%"),
    ];

    // ──────────────────────────────────────────────────────────────────────────
    // Generic Modbus — 4 starter Holding Registers
    // ──────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<DeviceTag> GenericModbusTags() =>
    [
        MakeModbusTag("HR_001", "1", TagDataType.UInt16, ModbusRegisterType.HoldingRegisters, null, string.Empty),
        MakeModbusTag("HR_002", "2", TagDataType.UInt16, ModbusRegisterType.HoldingRegisters, null, string.Empty),
        MakeModbusTag("HR_003", "3", TagDataType.UInt16, ModbusRegisterType.HoldingRegisters, null, string.Empty),
        MakeModbusTag("HR_004", "4", TagDataType.UInt16, ModbusRegisterType.HoldingRegisters, null, string.Empty),
    ];

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates an EtherNet/IP / S7 symbolic-address tag.</summary>
    private static DeviceTag MakePlcTag(string name, string plcAddress, TagDataType dataType) =>
        new()
        {
            Name = name,
            PlcAddress = plcAddress,
            Kind = TagKind.Plc,
            DataType = dataType,
            ElementCount = 1,
            // ByteCount is set by the caller (DefaultByteSize)
            IsSelected = true,
        };

    /// <summary>Modbus tag helper — register type and decimal places.</summary>
    private static DeviceTag MakeModbusTag(
        string name, string address, TagDataType dataType,
        ModbusRegisterType registerType, int? decimalPlaces, string unit) =>
        new()
        {
            Name = name,
            PlcAddress = address,
            Kind = TagKind.Plc,
            DataType = dataType,
            RegisterType = registerType,
            ElementCount = 1,
            DecimalPlaces = decimalPlaces,
            EngineeringUnit = string.IsNullOrEmpty(unit) ? null : unit,
            IsSelected = true,
        };
}
