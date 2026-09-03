using System;
using System.Threading.Tasks;
using S7.Net;
using Serilog;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.PlcDrivers;

/// <summary>
/// SiemensS7Driver: S7.Net driver for Siemens S7-300, S7-400, S7-1200, S7-1500 PLCs.
/// Supports DB, Merker, Input, Output data blocks reading/writing.
/// </summary>
public class SiemensS7Driver : IPlcDriver, IDisposable
{
    private Plc? _plc;
    private PlcDevice? _device;
    private bool _isConnected;
    private readonly ILogger _logger = Log.ForContext<SiemensS7Driver>();

    public bool IsConnected => _isConnected && _plc != null && _plc.IsConnected;

    public async Task ConnectAsync(PlcDevice device)
    {
        _device = device;
        try
        {
            _logger.Information("Connecting to Siemens S7 device {DeviceName} at {IpAddress}", 
                device.Name, device.IpAddress);

            // Default S7-1200 (Rack 0, Slot 1) - can be parameterized
            _plc = new Plc(CpuType.S71200, device.IpAddress, 0, 1);
            _plc.ReadTimeout = 3000;
            _plc.WriteTimeout = 3000;

            await _plc.OpenAsync();
            _isConnected = _plc.IsConnected;

            if (_isConnected)
            {
                _logger.Information("Connected to Siemens S7 device {DeviceName} successfully.", device.Name);
            }
            else
            {
                throw new InvalidOperationException($"Could not establish connection to S7 device at {device.IpAddress}.");
            }
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.Error(ex, "Failed to connect to Siemens S7 device {DeviceName} at {IpAddress}", 
                device.Name, device.IpAddress);
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        try
        {
            _plc?.Close();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error while closing Siemens S7 connection.");
        }
        finally
        {
            _isConnected = false;
            _plc = null;
        }

        return Task.CompletedTask;
    }

    public async Task<object> ReadTagAsync(string address)
    {
        if (!IsConnected || _plc == null)
            throw new InvalidOperationException($"Siemens driver is not connected to {_device?.Name ?? "device"}.");

        // S7.Net handles standard address formats like "DB1.DBD0", "DB1.DBW2", "M0.0", "MW10"
        var result = await _plc.ReadAsync(address);
        return result ?? 0;
    }

    public async Task WriteTagAsync(string address, object value)
    {
        if (!IsConnected || _plc == null)
            throw new InvalidOperationException($"Siemens driver is not connected to {_device?.Name ?? "device"}.");

        await _plc.WriteAsync(address, value);
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
