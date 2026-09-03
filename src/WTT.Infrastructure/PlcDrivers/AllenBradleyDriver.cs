using System;
using System.Threading.Tasks;
using libplctag;
using libplctag.DataTypes;
using Serilog;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.PlcDrivers;

/// <summary>
/// AllenBradleyDriver: Uses libplctag for ControlLogix, CompactLogix, PLC-5, and SLC-500.
/// </summary>
public class AllenBradleyDriver : IPlcDriver, IDisposable
{
    private PlcDevice? _device;
    private bool _isConnected;
    private readonly ILogger _logger = Log.ForContext<AllenBradleyDriver>();

    public bool IsConnected => _isConnected;

    public Task ConnectAsync(PlcDevice device)
    {
        _device = device;
        _isConnected = true; // libplctag manages connection on-demand per tag
        _logger.Information("Configured Allen Bradley driver for {DeviceName} at {IpAddress}", device.Name, device.IpAddress);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        return Task.CompletedTask;
    }

    public async Task<object> ReadTagAsync(string address)
    {
        if (!_isConnected || _device == null)
            throw new InvalidOperationException("Allen Bradley driver is not connected.");

        // libplctag tag format: protocol=ab_eip&gateway=192.168.1.10&path=1,0&cpu=LGX&name=MyTagName
        try
        {
            var tag = new Tag<DintPlcMapper, int>()
            {
                Name = address,
                Gateway = _device.IpAddress,
                Path = "1,0",
                PlcType = PlcType.ControlLogix,
                Protocol = Protocol.ab_eip,
                Timeout = TimeSpan.FromSeconds(3)
            };

            await tag.ReadAsync();
            return tag.Value;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error reading AB tag {Address} from {DeviceName}", address, _device.Name);
            throw;
        }
    }

    public async Task WriteTagAsync(string address, object value)
    {
        if (!_isConnected || _device == null)
            throw new InvalidOperationException("Allen Bradley driver is not connected.");

        if (int.TryParse(value.ToString(), out int intVal))
        {
            var tag = new Tag<DintPlcMapper, int>()
            {
                Name = address,
                Gateway = _device.IpAddress,
                Path = "1,0",
                PlcType = PlcType.ControlLogix,
                Protocol = Protocol.ab_eip,
                Timeout = TimeSpan.FromSeconds(3),
                Value = intVal
            };

            await tag.WriteAsync();
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
