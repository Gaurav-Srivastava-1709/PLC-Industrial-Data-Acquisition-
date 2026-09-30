using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Infrastructure;
using WTT.Infrastructure.Persistence;
using Xunit;

namespace WTT.Tests;

/// <summary>
/// Boots the real composition root (AddInfrastructure) against a temporary SQL Server database with the
/// simulator enabled and drives the capture pipeline the way the UI would.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CapturePipelineEndToEndTests : IAsyncLifetime
{
    private readonly SqlServerTestDatabase _database = new();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = _database.ConnectionString,
            ["Simulation:UseMockPlc"] = "true",
            ["Capture:ReconcileIntervalSeconds"] = "1",
            ["Capture:CurrentValueFlushSeconds"] = "1",
            ["Security:BcryptWorkFactor"] = "10",
        }).Build();

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => { c.Sources.Clear(); c.AddConfiguration(config); })
            .ConfigureLogging(l => { l.ClearProviders(); l.SetMinimumLevel(LogLevel.Warning); })
            .ConfigureServices(s => { s.AddInfrastructure(config); s.AddWttPipelineWorkers(); })
            .Build();

        await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await _host.Services.GetRequiredService<IInitialAdminEnrollmentService>()
            .EnrollAsync(new InitialAdminRequest("admin", "Test Administrator", "Admin#Secure2026"));
        await _host.StartAsync();

        var auth = _host.Services.GetRequiredService<IAuthService>();
        (await auth.LoginAsync(new LoginRequest("admin", "Admin#Secure2026"))).Succeeded.Should().BeTrue();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _database.DeleteAsync();
    }

    [Fact]
    public async Task Manual_mode_captures_plc_and_custom_tags_with_shared_scan_id()
    {
        var devices = _host.Services.GetRequiredService<IDeviceService>();
        var tags = _host.Services.GetRequiredService<ITagService>();
        var capture = _host.Services.GetRequiredService<IDataCaptureController>();

        var device = await devices.CreateAsync(new Device
        {
            Name = "SimLine1", Protocol = ProtocolType.EthernetIp, Manufacturer = Manufacturer.AllenBradley, Model = DeviceModel.ControlLogix,
            Host = "127.0.0.1", Port = 44818, PollIntervalMs = 200, TimeoutMs = 1000, Slot = 0,
        });
        await tags.SaveAsync(new DeviceTag { DeviceId = device.Id, Name = "Temperature", PlcAddress = "Sim.Temperature", DataType = TagDataType.Float32, DecimalPlaces = 2, EngineeringUnit = "°C" });
        await tags.SaveAsync(new DeviceTag { DeviceId = device.Id, Name = "Running", PlcAddress = "Sim.Running", DataType = TagDataType.Bool });
        await tags.SaveAsync(new DeviceTag { DeviceId = device.Id, Name = "LineId", Kind = TagKind.Custom, DataType = TagDataType.String, SetValue = "LINE-01" });

        await devices.SetRunStateAsync(device.Id, captureEnabled: true, monitorEnabled: true);
        capture.RequestReconcile();

        await WaitUntilAsync(async () => await CountReadingsAsync(device.Id) >= 9, TimeSpan.FromSeconds(15));
        await WaitUntilAsync(async () => (await tags.GetForDeviceAsync(device.Id)).Where(t => t.Kind == TagKind.Plc).All(t => t.CurrentValueAtUtc.HasValue), TimeSpan.FromSeconds(15));

        await using var db = await _host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var readings = await db.TagReadings.Where(r => r.DeviceId == device.Id).OrderBy(r => r.Id).ToListAsync();

        readings.Select(r => r.TagName).Distinct().Should().BeEquivalentTo(["Temperature", "Running", "LineId"]);
        readings.Where(r => r.TagName == "LineId").Should().OnlyContain(r => r.Value == "LINE-01", "custom tags are stamped into every scan");
        readings.Should().OnlyContain(r => r.Quality == TagQuality.Good && !r.IsTransferred && r.DeviceName == "SimLine1");
        (await tags.GetForDeviceAsync(device.Id)).Where(t => t.Kind == TagKind.Plc).Should().OnlyContain(t => t.CurrentQuality == TagQuality.Good && t.CurrentValueAtUtc.HasValue);

        // every scan produces exactly one row per tag sharing one ScanId
        readings.GroupBy(r => r.ScanId).Should().OnlyContain(g => g.Count() == 3);

        // temperature is formatted with 2 decimals, invariant culture
        var temp = readings.First(r => r.TagName == "Temperature").Value;
        double.Parse(temp, System.Globalization.CultureInfo.InvariantCulture).Should().BeInRange(40, 90);
        temp.Should().MatchRegex(@"^\d+(\.\d{1,2})?$");

        var status = capture.GetStatus(device.Id);
        status.Should().NotBeNull();
        status!.IsConnected.Should().BeTrue();
        status.LastTriggerState.Should().BeTrue();
        capture.GetLiveValues(device.Id).Should().HaveCount(3);
    }

    [Fact]
    public async Task Plc_trigger_mode_only_captures_while_trigger_tag_is_true()
    {
        var devices = _host.Services.GetRequiredService<IDeviceService>();
        var tags = _host.Services.GetRequiredService<ITagService>();
        var plcOps = _host.Services.GetRequiredService<IPlcOperationsService>();
        var capture = _host.Services.GetRequiredService<IDataCaptureController>();

        var device = await devices.CreateAsync(new Device { Name = "SimTrig", Protocol = ProtocolType.SiemensS7, Manufacturer = Manufacturer.Siemens, Model = DeviceModel.S7_1200, Host = "127.0.0.1", Port = 102, PollIntervalMs = 200 });
        var speed = await tags.SaveAsync(new DeviceTag { DeviceId = device.Id, Name = "Speed", PlcAddress = "DB1.DBD0", DataType = TagDataType.Float32 });
        var trig = await tags.SaveAsync(new DeviceTag { DeviceId = device.Id, Name = "CaptureOn", PlcAddress = "DB1.DBX4.0", DataType = TagDataType.Bool, IsSelected = false, IsWriteEnabled = true, WriteMinimum = 0, WriteMaximum = 1, MinimumWriteIntervalMs = 250 });

        // Force the simulated trigger to FALSE via a write, then arm PLC-trigger mode.
        await plcOps.WriteTagAsync(trig.Id, "0");
        await devices.SetCaptureModeAsync(device.Id, CaptureMode.PlcTrigger, "CaptureOn");
        await devices.SetRunStateAsync(device.Id, captureEnabled: true, monitorEnabled: false);
        capture.RequestReconcile();

        await WaitUntilAsync(async () => (capture.GetStatus(device.Id)?.ScanCount ?? 0) >= 3, TimeSpan.FromSeconds(15));
        (await CountReadingsAsync(device.Id)).Should().Be(0, "trigger is FALSE → nothing historised");
        capture.GetStatus(device.Id)!.LastTriggerState.Should().BeFalse();

        await plcOps.WriteTagAsync(trig.Id, "1");
        await WaitUntilAsync(async () => await CountReadingsAsync(device.Id) >= 2, TimeSpan.FromSeconds(15));
        capture.GetStatus(device.Id)!.LastTriggerState.Should().BeTrue();

        var before = await CountReadingsAsync(device.Id);
        await plcOps.WriteTagAsync(trig.Id, "0");
        await WaitUntilAsync(async () => capture.GetStatus(device.Id)?.LastTriggerState == false, TimeSpan.FromSeconds(15));
        await Task.Delay(800);
        (await CountReadingsAsync(device.Id) - before).Should().BeLessThan(3, "capture stops once trigger drops");
        _ = speed;
    }

    [Fact]
    public async Task Logical_mode_evaluates_expression_and_tag_service_enforces_rules()
    {
        var devices = _host.Services.GetRequiredService<IDeviceService>();
        var tags = _host.Services.GetRequiredService<ITagService>();
        var triggers = _host.Services.GetRequiredService<ITriggerService>();
        var plcOps = _host.Services.GetRequiredService<IPlcOperationsService>();
        var capture = _host.Services.GetRequiredService<IDataCaptureController>();

        var device = await devices.CreateAsync(new Device { Name = "SimLogic", Protocol = ProtocolType.ModbusTcp, Manufacturer = Manufacturer.Generic, Model = DeviceModel.GenericModbusTcp, Host = "127.0.0.1", Port = 502, PollIntervalMs = 200 });
        var level = await tags.SaveAsync(new DeviceTag { DeviceId = device.Id, Name = "Level", PlcAddress = "HR:100", DataType = TagDataType.Int16, IsWriteEnabled = true, WriteMinimum = 0, WriteMaximum = 2000, MinimumWriteIntervalMs = 250 });

        // Logical mode cannot be chosen without conditions.
        var noCond = () => devices.SetCaptureModeAsync(device.Id, CaptureMode.Logical, null);
        await noCond.Should().ThrowAsync<Domain.Exceptions.BusinessRuleException>();

        await triggers.AddAsync(new DeviceTriggerCondition { DeviceId = device.Id, TagName = "Level", Operator = TriggerOperator.GreaterThan, Value = "1000" });
        await devices.SetCaptureModeAsync(device.Id, CaptureMode.Logical, null);

        // Tag used by a condition cannot be deleted.
        var del = () => tags.DeleteAsync(level.Id);
        await del.Should().ThrowAsync<Domain.Exceptions.BusinessRuleException>().WithMessage("*trigger condition*");

        await plcOps.WriteTagAsync(level.Id, "500");
        await devices.SetRunStateAsync(device.Id, captureEnabled: true, monitorEnabled: false);
        capture.RequestReconcile();
        await WaitUntilAsync(async () => (capture.GetStatus(device.Id)?.ScanCount ?? 0) >= 3, TimeSpan.FromSeconds(15));
        capture.GetStatus(device.Id)!.LastTriggerState.Should().BeFalse("500 > 1000 is false");

        await plcOps.WriteTagAsync(level.Id, "1500");
        await WaitUntilAsync(async () => await CountReadingsAsync(device.Id) >= 1, TimeSpan.FromSeconds(15));
        capture.GetStatus(device.Id)!.LastTriggerState.Should().BeTrue();
    }

    [Fact]
    public async Task Sequential_modbus_block_generates_legacy_named_tags()
    {
        var devices = _host.Services.GetRequiredService<IDeviceService>();
        var addresses = _host.Services.GetRequiredService<IDeviceAddressService>();
        var tags = _host.Services.GetRequiredService<ITagService>();

        var mw100 = await devices.CreateAsync(new Device { Name = "MW100-A", Protocol = ProtocolType.ModbusTcp, Manufacturer = Manufacturer.Yokogawa, Model = DeviceModel.Mw100, Host = "10.0.0.5", Port = 502 });
        await addresses.SaveAsync(new DeviceAddress { DeviceId = mw100.Id, AddressType = ModbusAddressType.Sequential, RegisterType = ModbusRegisterType.InputRegisters, StartAddress = 301, DataType = TagDataType.Float32, PointCount = 5, TagPrefix = "CH", DecimalPlaces = 2 });
        var generated = await tags.GetForDeviceAsync(mw100.Id);
        generated.Select(t => t.Name).Should().Equal("CH001", "CH002", "CH003", "CH004", "CH005");
        generated.Select(t => t.PlcAddress).Should().Equal("301", "303", "305", "307", "309");
        generated.Should().OnlyContain(t => t.RegisterType == ModbusRegisterType.InputRegisters && t.DataType == TagDataType.Float32 && t.DecimalPlaces == 2);

        var gm10 = await devices.CreateAsync(new Device { Name = "GM10-A", Protocol = ProtocolType.ModbusTcp, Manufacturer = Manufacturer.Yokogawa, Model = DeviceModel.Gm10, Host = "10.0.0.6", Port = 502 });
        await addresses.SaveAsync(new DeviceAddress { DeviceId = gm10.Id, AddressType = ModbusAddressType.Sequential, RegisterType = ModbusRegisterType.InputRegisters, StartAddress = 1, DataType = TagDataType.Float32, PointCount = 12, TagPrefix = "CH" });
        var gm = await tags.GetForDeviceAsync(gm10.Id);
        gm.First(t => t.Name == "CH011").PlcAddress.Should().Be("101", "channel 11 lives in the second 100-register bank");

        var adam = await devices.CreateAsync(new Device { Name = "ADAM-A", Protocol = ProtocolType.ModbusTcp, Manufacturer = Manufacturer.Advantech, Model = DeviceModel.Adam6017, Host = "10.0.0.7", Port = 502 });
        await addresses.SaveAsync(new DeviceAddress { DeviceId = adam.Id, AddressType = ModbusAddressType.Sequential, RegisterType = ModbusRegisterType.HoldingRegisters, StartAddress = 1, DataType = TagDataType.UInt16, PointCount = 8, TagPrefix = "AI", ZeroBasedTagIndex = true });
        (await tags.GetForDeviceAsync(adam.Id)).Select(t => t.Name).First().Should().Be("AI000", "ADAM tags are 0-based like the legacy app");
    }

    [Fact]
    public async Task Unknown_protocol_is_rejected_not_silently_mocked()
    {
        var runtime = _host.Services.GetRequiredService<IRuntimeSettings>();
        var settings = _host.Services.GetRequiredService<IAppSettingsService>();
        await settings.SetAsync(AppSettingKeys.UseMockPlc, "false");
        await runtime.ReloadAsync();
        try
        {
            var factory = _host.Services.GetRequiredService<Domain.Interfaces.IPlcDriverFactory>();
            var bogus = new Device { Name = "Bogus", Protocol = ProtocolType.EthernetIp, Manufacturer = Manufacturer.Siemens, Model = DeviceModel.S7_300 };
            var act = () => factory.Create(bogus);
            act.Should().Throw<Domain.Exceptions.PlcConfigurationException>();
        }
        finally
        {
            await settings.SetAsync(AppSettingKeys.UseMockPlc, "true");
            await runtime.ReloadAsync();
        }
    }

    private async Task<int> CountReadingsAsync(int deviceId)
    {
        await using var db = await _host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.TagReadings.CountAsync(r => r.DeviceId == deviceId);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(150);
        }
        (await condition()).Should().BeTrue($"condition not met within {timeout}");
    }
}
