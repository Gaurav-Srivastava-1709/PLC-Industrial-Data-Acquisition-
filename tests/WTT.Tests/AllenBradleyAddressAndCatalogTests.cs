using FluentAssertions;
using WTT.Application.Catalog;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Infrastructure.PlcDrivers.AllenBradley;
using Xunit;

namespace WTT.Tests;

public class AllenBradleyAddressTests
{
    [Fact]
    public void Parses_pccc_bit_address()
    {
        var p = AllenBradleyAddress.Parse("B3:0/4");
        p.TagName.Should().Be("B3:0");
        p.BitIndex.Should().Be(4);
        p.ElementCount.Should().Be(1);
        AllenBradleyAddress.InferDataType("B3:0/4").Should().Be(TagDataType.Bool);
    }

    [Fact]
    public void Parses_pccc_ascii_range()
    {
        var p = AllenBradleyAddress.Parse("A9:0-10");
        p.TagName.Should().Be("A9:0");
        p.ElementCount.Should().Be(11);
        AllenBradleyAddress.InferDataType("A9:0-10").Should().Be(TagDataType.String);
    }

    [Theory]
    [InlineData("N7:0", TagDataType.Int16)]
    [InlineData("F8:3", TagDataType.Float32)]
    [InlineData("ST10:2", TagDataType.String)]
    [InlineData("L9:1", TagDataType.Int32)]
    [InlineData("T4:0.ACC", TagDataType.Int16)]
    public void Infers_pccc_types(string address, TagDataType expected) =>
        AllenBradleyAddress.InferDataType(address).Should().Be(expected);

    [Fact]
    public void Logix_symbolic_names_pass_through()
    {
        var p = AllenBradleyAddress.Parse("Program:Main.Speed[3]");
        p.TagName.Should().Be("Program:Main.Speed[3]");
        p.BitIndex.Should().BeNull();
        AllenBradleyAddress.InferDataType("Program:Main.Speed[3]").Should().BeNull();
    }
}

public class PlcModelCatalogTests
{
    [Fact]
    public void Every_model_has_exactly_one_catalog_entry()
    {
        foreach (var model in Enum.GetValues<DeviceModel>())
            PlcModelCatalog.Models.Count(m => m.Model == model).Should().Be(1, $"{model} must be in the catalog once");
    }

    [Fact]
    public void Legacy_eight_models_are_present()
    {
        var expected = new[]
        {
            DeviceModel.ControlLogix, DeviceModel.CompactLogix, DeviceModel.Plc5, DeviceModel.Slc500,
            DeviceModel.S7_300, DeviceModel.S7_1200, DeviceModel.Mw100, DeviceModel.Gm10,
            DeviceModel.Adam6017, DeviceModel.Adam6018, DeviceModel.Walchem600,
        };
        PlcModelCatalog.Models.Select(m => m.Model).Should().Contain(expected);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(10, 18)]
    [InlineData(11, 100)]
    [InlineData(25, 208)]
    [InlineData(60, 518)]
    public void Gm10_channel_bank_layout_matches_legacy(int channel, int offset) =>
        PlcModelCatalog.Gm10ChannelRegisterOffset(channel).Should().Be(offset);
}

public class TagValueFormatterTests
{
    [Fact]
    public void Applies_scaling_and_rounding_with_invariant_culture()
    {
        var tag = new DeviceTag { ScaleFactor = 0.1, ScaleOffset = -5, DecimalPlaces = 2 };
        TagValueFormatter.Format(tag, (ushort)1234).Should().Be("118.4");
        TagValueFormatter.Format(new DeviceTag { DecimalPlaces = 2 }, 3.14159f).Should().Be("3.14");
        TagValueFormatter.Format(new DeviceTag(), true).Should().Be("1");
        TagValueFormatter.Format(new DeviceTag(), 42).Should().Be("42");
        TagValueFormatter.Format(new DeviceTag(), "abc\0\0").Should().Be("abc");
    }

    [Fact]
    public void Block_decimal_places_are_inherited_by_generated_tags()
    {
        var tag = new DeviceTag { Address = new DeviceAddress { DecimalPlaces = 1 } };
        TagValueFormatter.Format(tag, 12.349f).Should().Be("12.3");
    }

    [Fact]
    public void ParseForWrite_handles_all_types()
    {
        TagValueFormatter.ParseForWrite(TagDataType.Bool, "1").Should().Be(true);
        TagValueFormatter.ParseForWrite(TagDataType.Int16, "-7").Should().Be((short)-7);
        TagValueFormatter.ParseForWrite(TagDataType.Float32, "2.5").Should().Be(2.5f);
        var act = () => TagValueFormatter.ParseForWrite(TagDataType.Int32, "abc");
        act.Should().Throw<FormatException>();
    }
}
