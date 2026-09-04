using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Domain.Interfaces;
using WTT.Infrastructure.PlcDrivers.AllenBradley;
using WTT.Infrastructure.PlcDrivers.Modbus;
using WTT.Infrastructure.PlcDrivers.Siemens;

namespace WTT.Infrastructure.PlcDrivers;

/// <summary>
/// Maps a device's protocol/model to a driver. Unknown combinations throw — a misconfigured
/// device must never silently produce simulated data.
/// </summary>
public sealed class PlcDriverFactory : IPlcDriverFactory
{
    private readonly IRuntimeSettings _runtime;
    private readonly ILoggerFactory _loggerFactory;

    public PlcDriverFactory(IRuntimeSettings runtime, ILoggerFactory loggerFactory)
    {
        _runtime = runtime;
        _loggerFactory = loggerFactory;
    }

    public IPlcDriver Create(Device device)
    {
        if (_runtime.UseMockPlc)
            return new MockPlcDriver(device);

        return device.Protocol switch
        {
            ProtocolType.ModbusTcp => new ModbusTcpDriver(device, _loggerFactory.CreateLogger<ModbusTcpDriver>()),
            ProtocolType.SiemensS7 => new SiemensS7Driver(device, _loggerFactory.CreateLogger<SiemensS7Driver>()),
            ProtocolType.EthernetIp when device.Manufacturer == Manufacturer.AllenBradley
                => new AllenBradleyDriver(device, _loggerFactory.CreateLogger<AllenBradleyDriver>()),
            _ => throw new PlcConfigurationException(
                $"No driver available for device '{device.Name}' ({device.Protocol} / {device.Manufacturer} / {device.Model}). Check the device configuration."),
        };
    }
}
