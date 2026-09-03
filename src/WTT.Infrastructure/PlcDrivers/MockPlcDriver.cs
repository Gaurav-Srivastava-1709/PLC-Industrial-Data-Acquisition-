using System;
using System.Threading.Tasks;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.PlcDrivers;

public class MockPlcDriver : IPlcDriver
{
    private bool _isConnected;
    private readonly Random _random = new();

    public bool IsConnected => _isConnected;

    public Task ConnectAsync(PlcDevice device)
    {
        // Simulate connection delay
        _isConnected = true;
        return Task.Delay(500);
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        return Task.CompletedTask;
    }

    public Task<object> ReadTagAsync(string address)
    {
        if (!_isConnected)
            throw new InvalidOperationException("Not connected to mock PLC.");

        // Generate random fake data based on address hint
        object value;
        if (address.Contains("TEMP", StringComparison.OrdinalIgnoreCase))
        {
            value = Math.Round(_random.NextDouble() * 100, 2); // 0-100 degrees
        }
        else if (address.Contains("PRESSURE", StringComparison.OrdinalIgnoreCase))
        {
            value = _random.Next(10, 50); // 10-50 PSI
        }
        else if (address.Contains("SPEED", StringComparison.OrdinalIgnoreCase))
        {
            value = _random.Next(1000, 1500); // 1000-1500 RPM
        }
        else
        {
            value = _random.Next(0, 100);
        }

        return Task.FromResult(value);
    }

    public Task WriteTagAsync(string address, object value)
    {
        if (!_isConnected)
            throw new InvalidOperationException("Not connected to mock PLC.");

        // Simulate write success
        return Task.Delay(100);
    }
}
