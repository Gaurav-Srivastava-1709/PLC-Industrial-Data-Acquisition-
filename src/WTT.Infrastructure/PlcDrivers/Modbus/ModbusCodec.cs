using System.Buffers.Binary;
using System.Text;
using WTT.Domain.Enums;

namespace WTT.Infrastructure.PlcDrivers.Modbus;

/// <summary>
/// Pure register ↔ value conversions. Modbus registers are 16-bit big-endian; multi-register
/// values depend on the device word order (<see cref="WordOrder"/>).
/// </summary>
public static class ModbusCodec
{
    /// <summary>Number of registers needed for one value of the type (strings: ElementCount chars).</summary>
    public static int RegisterCount(TagDataType type, int elementCount = 1) => type switch
    {
        TagDataType.Bool or TagDataType.Byte or TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Float64 => 4,
        TagDataType.String => Math.Max(1, (Math.Max(1, elementCount) + 1) / 2),
        _ => 1,
    };

    public static object Decode(ReadOnlySpan<ushort> regs, TagDataType type, WordOrder order, int elementCount = 1)
    {
        switch (type)
        {
            case TagDataType.Bool: return regs[0] != 0;
            case TagDataType.Byte: return (byte)(regs[0] & 0xFF);
            case TagDataType.Int16: return unchecked((short)regs[0]);
            case TagDataType.UInt16: return regs[0];
            case TagDataType.Int32: return BinaryPrimitives.ReadInt32BigEndian(ToBytes(regs, 2, order));
            case TagDataType.UInt32: return BinaryPrimitives.ReadUInt32BigEndian(ToBytes(regs, 2, order));
            case TagDataType.Float32: return BinaryPrimitives.ReadSingleBigEndian(ToBytes(regs, 2, order));
            case TagDataType.Int64: return BinaryPrimitives.ReadInt64BigEndian(ToBytes(regs, 4, order));
            case TagDataType.UInt64: return BinaryPrimitives.ReadUInt64BigEndian(ToBytes(regs, 4, order));
            case TagDataType.Float64: return BinaryPrimitives.ReadDoubleBigEndian(ToBytes(regs, 4, order));
            case TagDataType.String:
                {
                    var bytes = new byte[regs.Length * 2];
                    for (var i = 0; i < regs.Length; i++)
                    {
                        bytes[i * 2] = (byte)(regs[i] >> 8);
                        bytes[i * 2 + 1] = (byte)(regs[i] & 0xFF);
                    }
                    var s = Encoding.ASCII.GetString(bytes);
                    var nul = s.IndexOf('\0');
                    if (nul >= 0) s = s[..nul];
                    return elementCount > 0 && s.Length > elementCount ? s[..elementCount] : s;
                }
            default:
                return regs[0];
        }
    }

    public static ushort[] Encode(object value, TagDataType type, WordOrder order, int elementCount = 1)
    {
        switch (type)
        {
            case TagDataType.Bool: return [(ushort)(Convert.ToBoolean(value) ? 1 : 0)];
            case TagDataType.Byte: return [Convert.ToByte(value)];
            case TagDataType.Int16: return [unchecked((ushort)Convert.ToInt16(value))];
            case TagDataType.UInt16: return [Convert.ToUInt16(value)];
            case TagDataType.Int32: { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, Convert.ToInt32(value)); return FromBytes(b, order); }
            case TagDataType.UInt32: { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, Convert.ToUInt32(value)); return FromBytes(b, order); }
            case TagDataType.Float32: { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, Convert.ToSingle(value)); return FromBytes(b, order); }
            case TagDataType.Int64: { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, Convert.ToInt64(value)); return FromBytes(b, order); }
            case TagDataType.UInt64: { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, Convert.ToUInt64(value)); return FromBytes(b, order); }
            case TagDataType.Float64: { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, Convert.ToDouble(value)); return FromBytes(b, order); }
            case TagDataType.String:
                {
                    var s = value?.ToString() ?? string.Empty;
                    var regCount = RegisterCount(TagDataType.String, elementCount);
                    var bytes = new byte[regCount * 2];
                    Encoding.ASCII.GetBytes(s, 0, Math.Min(s.Length, bytes.Length), bytes, 0);
                    var regs = new ushort[regCount];
                    for (var i = 0; i < regCount; i++) regs[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
                    return regs;
                }
            default:
                return [Convert.ToUInt16(value)];
        }
    }

    /// <summary>Arrange registers into a big-endian byte stream according to word order.</summary>
    private static byte[] ToBytes(ReadOnlySpan<ushort> regs, int count, WordOrder order)
    {
        if (regs.Length < count) throw new ArgumentException($"Expected {count} registers but got {regs.Length}.");
        var bytes = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            // HighLow: first register is the most significant word → natural order.
            // LowHigh: first register is the least significant word → reverse register order.
            var src = order == WordOrder.HighLow ? regs[i] : regs[count - 1 - i];
            bytes[i * 2] = (byte)(src >> 8);
            bytes[i * 2 + 1] = (byte)(src & 0xFF);
        }
        return bytes;
    }

    private static ushort[] FromBytes(ReadOnlySpan<byte> bigEndian, WordOrder order)
    {
        var count = bigEndian.Length / 2;
        var regs = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            var word = (ushort)((bigEndian[i * 2] << 8) | bigEndian[i * 2 + 1]);
            if (order == WordOrder.HighLow) regs[i] = word;
            else regs[count - 1 - i] = word;
        }
        return regs;
    }
}

/// <summary>Parses tag addresses like "100", "HR:100", "IR:5", "CO:1", "DI:3", "4x100" into register type + address.</summary>
public static class ModbusAddressParser
{
    public sealed record Parsed(ModbusRegisterType RegisterType, int Address);

    /// <param name="address">User supplied address.</param>
    /// <param name="fallback">Register type when the address has no prefix.</param>
    /// <param name="oneBased">If true, 1 is subtracted to obtain the protocol (0-based) address.</param>
    public static Parsed Parse(string address, ModbusRegisterType fallback, bool oneBased)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("Modbus address is empty.");

        var a = address.Trim().ToUpperInvariant();
        var type = fallback;
        string number = a;

        var colon = a.IndexOf(':');
        if (colon > 0)
        {
            var prefix = a[..colon];
            number = a[(colon + 1)..];
            type = prefix switch
            {
                "HR" or "H" or "4X" or "4" => ModbusRegisterType.HoldingRegisters,
                "IR" or "I" or "3X" or "3" => ModbusRegisterType.InputRegisters,
                "CO" or "C" or "0X" or "0" => ModbusRegisterType.Coils,
                "DI" or "D" or "1X" or "1" => ModbusRegisterType.DiscreteInputs,
                _ => throw new FormatException($"Unknown Modbus prefix '{prefix}'. Use HR:, IR:, CO: or DI:."),
            };
        }
        else if (a.Length > 2 && a[1] == 'X' && char.IsDigit(a[0]))
        {
            type = a[0] switch
            {
                '4' => ModbusRegisterType.HoldingRegisters,
                '3' => ModbusRegisterType.InputRegisters,
                '0' => ModbusRegisterType.Coils,
                '1' => ModbusRegisterType.DiscreteInputs,
                _ => throw new FormatException($"Unknown Modbus prefix '{a[..2]}'."),
            };
            number = a[2..];
        }

        if (!int.TryParse(number, out var n) || n < 0)
            throw new FormatException($"'{address}' is not a valid Modbus address.");

        var wire = oneBased ? n - 1 : n;
        if (wire < 0 || wire > 65535)
            throw new FormatException($"Modbus address '{address}' is out of range (1..65536 when 1-based).");

        return new Parsed(type, wire);
    }
}
