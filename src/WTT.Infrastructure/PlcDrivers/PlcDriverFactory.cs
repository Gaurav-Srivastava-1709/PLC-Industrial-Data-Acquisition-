// =============================================================================
// FILE      : src/WTT.Infrastructure/PlcDrivers/PlcDriverFactory.cs
// LAYER     : Infrastructure - maps a device row to a concrete IPlcDriver
// PURPOSE   : The one place deciding which driver talks to a device, from its
//             protocol / manufacturer / model. Keeps the switch out of the capture
//             worker and makes driver selection testable.
// DI        : Singleton - stateless, resolved by workers as IPlcDriverFactory.
// THREADING : Stateless and safe from any thread. It only constructs; the returned
//             driver belongs to exactly one DeviceCaptureWorker and is used
//             sequentially by that worker alone.
// DEPENDS ON: IRuntimeSettings (UseMockPlc), ILoggerFactory, the concrete drivers
// USED BY   : DeviceCaptureWorker (IPlcDriverFactory.Create), PlcOperationsService (test/browse/write).
// KEY RULES :
//   * An UNKNOWN (ProtocolType, Manufacturer, DeviceModel) combination THROWS
//     PlcConfigurationException. It must never fall back to another driver or to
//     the mock: a misconfigured device silently producing simulated data is a data
//     integrity incident, not a convenience.
//   * Simulation:UseMockPlc short-circuits everything to MockPlcDriver - the ONLY
//     way simulated values reach production code. The flag is part of the worker
//     fingerprint, so toggling it restarts the workers.
//   * Adding a protocol means: implement IPlcDriver, add a case here, and add the
//     model to MapCpu / MapPlcType in that driver. A silent no-match is a bug.
//   * Do not cache or share driver instances - the returned object owns a socket and
//     a connection lifetime, so it must be per-worker and disposable.
// =============================================================================
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
