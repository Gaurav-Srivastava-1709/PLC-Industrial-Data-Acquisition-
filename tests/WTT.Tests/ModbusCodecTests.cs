using FluentAssertions;
using WTT.Domain.Enums;
using WTT.Infrastructure.PlcDrivers.Modbus;
using Xunit;

namespace WTT.Tests;

public class ModbusCodecTests
{
    [Fact]
    public void Float32_LowHigh_matches_legacy_yokogawa_decoding()
    {
        // Legacy MW100 code assembled "reg2.hi reg2.lo reg1.hi reg1.lo" (second register is the high word).
        // 123.456f big-endian = 0x42F6E979 → high word 0x42F6, low word 0xE979.
        ushort[] regs = [0xE979, 0x42F6];
        var v = (float)ModbusCodec.Decode(regs, TagDataType.Float32, WordOrder.LowHigh);
        v.Should().BeApproximately(123.456f, 0.001f);
    }

    [Fact]
    public void Float32_HighLow_is_natural_big_endian()
    {
        ushort[] regs = [0x42F6, 0xE979];
        ((float)ModbusCodec.Decode(regs, TagDataType.Float32, WordOrder.HighLow)).Should().BeApproximately(123.456f, 0.001f);
    }

    [Theory]
    [InlineData(TagDataType.Int16, (short)-5)]
    [InlineData(TagDataType.UInt16, (ushort)65530)]
    [InlineData(TagDataType.Int32, -123456)]
    [InlineData(TagDataType.UInt32, 4000000000u)]
    [InlineData(TagDataType.Float32, 3.14159f)]
    [InlineData(TagDataType.Int64, -9876543210L)]
    [InlineData(TagDataType.Float64, 2.718281828459045)]
    [InlineData(TagDataType.Bool, true)]
    public void Encode_then_decode_round_trips(TagDataType type, object value)
    {
        foreach (var order in new[] { WordOrder.LowHigh, WordOrder.HighLow })
        {
            var regs = ModbusCodec.Encode(value, type, order);
            regs.Length.Should().Be(ModbusCodec.RegisterCount(type));
            var decoded = ModbusCodec.Decode(regs, type, order);
            decoded.Should().Be(value);
        }
    }

    [Fact]
    public void String_round_trip()
    {
        var regs = ModbusCodec.Encode("HELLO", TagDataType.String, WordOrder.HighLow, elementCount: 8);
        regs.Length.Should().Be(4);
        ModbusCodec.Decode(regs, TagDataType.String, WordOrder.HighLow, 8).Should().Be("HELLO");
    }

    [Theory]
    [InlineData("100", ModbusRegisterType.HoldingRegisters, true, ModbusRegisterType.HoldingRegisters, 99)]
    [InlineData("100", ModbusRegisterType.InputRegisters, false, ModbusRegisterType.InputRegisters, 100)]
    [InlineData("HR:1", ModbusRegisterType.Coils, true, ModbusRegisterType.HoldingRegisters, 0)]
    [InlineData("IR:5", ModbusRegisterType.Coils, true, ModbusRegisterType.InputRegisters, 4)]
    [InlineData("CO:17", ModbusRegisterType.HoldingRegisters, true, ModbusRegisterType.Coils, 16)]
    [InlineData("DI:3", ModbusRegisterType.HoldingRegisters, false, ModbusRegisterType.DiscreteInputs, 3)]
    [InlineData("4x10", ModbusRegisterType.Coils, true, ModbusRegisterType.HoldingRegisters, 9)]
    public void Address_parser(string input, ModbusRegisterType fallback, bool oneBased, ModbusRegisterType expectedType, int expectedAddress)
    {
        var p = ModbusAddressParser.Parse(input, fallback, oneBased);
        p.RegisterType.Should().Be(expectedType);
        p.Address.Should().Be(expectedAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("XX:1")]
    [InlineData("0")] // 1-based → -1
    public void Address_parser_rejects_invalid(string input)
    {
        var act = () => ModbusAddressParser.Parse(input, ModbusRegisterType.HoldingRegisters, oneBased: true);
        act.Should().Throw<FormatException>();
    }
}
