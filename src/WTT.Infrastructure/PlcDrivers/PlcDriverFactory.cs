using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.PlcDrivers;

public class PlcDriverFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public PlcDriverFactory(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public IPlcDriver CreateDriver(PlcDevice device)
    {
        bool useMockPlc = false;
        bool.TryParse(_configuration["MockPLC:Enabled"], out useMockPlc);

        if (useMockPlc)
        {
            return _serviceProvider.GetRequiredService<MockPlcDriver>();
        }

        return device.Protocol.ToUpperInvariant() switch
        {
            "MODBUSTCP" => _serviceProvider.GetRequiredService<ModbusTcpDriver>(),
            "SIEMENSS7" => _serviceProvider.GetRequiredService<SiemensS7Driver>(),
            "ETHERNETIP" => _serviceProvider.GetRequiredService<AllenBradleyDriver>(),
            _ => _serviceProvider.GetRequiredService<MockPlcDriver>()
        };
    }
}
