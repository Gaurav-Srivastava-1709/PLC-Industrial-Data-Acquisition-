namespace WTT.Domain.Enums;

/// <summary>Wire protocol used to talk to the device.</summary>
public enum ProtocolType
{
    ModbusTcp = 1,
    EthernetIp = 2,
    SiemensS7 = 3,
}

/// <summary>Device vendor. Drives the list of selectable models.</summary>
public enum Manufacturer
{
    AllenBradley = 1,
    Siemens = 2,
    Yokogawa = 3,
    Advantech = 4,
    Walchem = 5,
    Generic = 99,
}

/// <summary>Concrete device model. Determines driver behaviour, defaults and tag generation rules.</summary>
public enum DeviceModel
{
    // Allen-Bradley (EtherNet/IP via libplctag)
    ControlLogix = 1,
    CompactLogix = 2,
    Plc5 = 3,
    Slc500 = 4,
    MicroLogix = 5,
    Micro800 = 6,

    // Siemens (S7comm via S7.Net)
    S7_300 = 10,
    S7_400 = 11,
    S7_1200 = 12,
    S7_1500 = 13,

    // Yokogawa (Modbus TCP)
    Mw100 = 20,
    Gm10 = 21,

    // Advantech ADAM (Modbus TCP)
    Adam6017 = 30,
    Adam6018 = 31,

    // Walchem (Modbus TCP)
    Walchem600 = 40,

    // Any Modbus/TCP slave
    GenericModbusTcp = 90,
}

/// <summary>
/// Origin of a tag value.
/// Plc: read from the controller.
/// Custom: constant/manually set value stamped into every scan.
/// External: value pulled from an external SQL query column (batch/shift context).
/// </summary>
public enum TagKind
{
    Plc = 1,
    Custom = 2,
    External = 3,
}

public enum TagDataType
{
    Bool = 1,
    Byte = 2,
    Int16 = 3,
    UInt16 = 4,
    Int32 = 5,
    UInt32 = 6,
    Int64 = 7,
    UInt64 = 8,
    Float32 = 9,
    Float64 = 10,
    String = 11,
}

/// <summary>How the capture worker decides whether to historise a scan.</summary>
public enum CaptureMode
{
    /// <summary>Always capture on every poll.</summary>
    Manual = 1,
    /// <summary>Capture only while a boolean PLC tag is TRUE.</summary>
    PlcTrigger = 2,
    /// <summary>Capture only while a logical expression over tag values is TRUE.</summary>
    Logical = 3,
}

public enum ModbusRegisterType
{
    Coils = 1,
    DiscreteInputs = 2,
    InputRegisters = 3,
    HoldingRegisters = 4,
}

/// <summary>
/// Discrete: user defines each tag individually.
/// Sequential: a block of N consecutive points; tags are auto-generated from a prefix.
/// </summary>
public enum ModbusAddressType
{
    Discrete = 1,
    Sequential = 2,
}

/// <summary>Word order for multi-register (32/64-bit) Modbus values.</summary>
public enum WordOrder
{
    /// <summary>First register holds the LOW word (little-endian word order, "LSB").</summary>
    LowHigh = 1,
    /// <summary>First register holds the HIGH word (big-endian word order, "MSB").</summary>
    HighLow = 2,
}

public enum TriggerOperator
{
    Equal = 1,
    NotEqual = 2,
    GreaterThan = 3,
    LessThan = 4,
    GreaterOrEqual = 5,
    LessOrEqual = 6,
    OnChange = 7,
}

public enum LogicalOperator
{
    None = 0,
    And = 1,
    Or = 2,
}

public enum ExternalQueryType
{
    Contextual = 1,
    Expression = 2,
}

public enum UserRole
{
    Viewer = 0,
    Operator = 1,
    Admin = 2,
}

public enum TransferStatus
{
    Success = 1,
    Failed = 2,
    NothingToTransfer = 3,
}

public enum TagQuality
{
    Good = 0,
    Uncertain = 1,
    Bad = 2,
    NotConnected = 3,
    ConfigError = 4,
}

public enum AppTheme
{
    Light = 0,
    Dark = 1,
}
