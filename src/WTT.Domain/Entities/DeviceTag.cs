using System;

namespace WTT.Domain.Entities;

/// <summary>
/// DeviceTag: PLC machine ke andar kisi specific sensor ya data memory location ko represent karta hai.
/// Jaise Boiler ka Temperature (TEMP_01), Pump ka Pressure (PRESS_01).
/// </summary>
public class DeviceTag
{
    public int Id { get; set; }

    // Foreign key to PlcDevice
    public int DeviceId { get; set; }
    public PlcDevice Device { get; set; } = null!;

    // Tag identify karne ke liye readable name (e.g., Boiler_Temperature)
    public string TagName { get; set; } = string.Empty;

    // PLC memory address (e.g. Modbus 40001, Siemens DB1.DBD0, Allen-Bradley N7:0)
    public string Address { get; set; } = string.Empty;

    // Data type: Int16, Int32, Float, Bool, String
    public string DataType { get; set; } = "Float";

    // Optional user description
    public string? Description { get; set; }

    // Kya ye tag polling ke liye active hai ya nahi
    public bool IsActive { get; set; } = true;

    // Record creation timestamp
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
