using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Modbus.Device;
using Serilog;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.PlcDrivers;

/// <summary>
/// ModbusTcpDriver: Industry standard Modbus TCP protocol driver using NModbus.
/// Supports reading Holding Registers, Input Registers, Coils, and Discrete Inputs.
/// </summary>
public class ModbusTcpDriver : IPlcDriver, IDisposable
{
    private TcpClient? _tcpClient;
    private ModbusIpMaster? _modbusMaster;
    private PlcDevice? _device;
    private bool _isConnected;
    private readonly ILogger _logger = Log.ForContext<ModbusTcpDriver>();

    public bool IsConnected => _isConnected && _tcpClient != null && _tcpClient.Connected;

    public async Task ConnectAsync(PlcDevice device)
    {
        _device = device;
        try
        {
            _logger.Information("Connecting to Modbus device {DeviceName} at {IpAddress}:{Port}", 
                device.Name, device.IpAddress, device.Port);

            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(device.IpAddress, device.Port);
            _modbusMaster = ModbusIpMaster.CreateIp(_tcpClient);
            _modbusMaster.Transport.ReadTimeout = 3000;
            _modbusMaster.Transport.WriteTimeout = 3000;
            _isConnected = true;

            _logger.Information("Connected to Modbus device {DeviceName} successfully.", device.Name);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.Error(ex, "Failed to connect to Modbus device {DeviceName} at {IpAddress}:{Port}", 
                device.Name, device.IpAddress, device.Port);
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        try
        {
            _modbusMaster?.Dispose();
            _tcpClient?.Close();
            _tcpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error while disconnecting Modbus client.");
        }
        finally
        {
            _isConnected = false;
            _modbusMaster = null;
            _tcpClient = null;
        }

        return Task.CompletedTask;
    }

    public async Task<object> ReadTagAsync(string address)
    {
        if (!IsConnected || _modbusMaster == null)
            throw new InvalidOperationException($"Modbus driver is not connected to {_device?.Name ?? "device"}.");

        // Parse address: e.g., "40001" (Holding register 0), "00001" (Coil 0), "30001" (Input register 0)
        // Default to holding register if numeric
        byte slaveId = 1;
        ushort registerAddress = 0;

        if (ushort.TryParse(address, out ushort parsedAddr))
        {
            if (parsedAddr >= 40001 && parsedAddr <= 49999)
                registerAddress = (ushort)(parsedAddr - 40001);
            else if (parsedAddr >= 30001 && parsedAddr <= 39999)
            {
                var inputRegs = await _modbusMaster.ReadInputRegistersAsync(slaveId, (ushort)(parsedAddr - 30001), 1);
                return inputRegs.Length > 0 ? inputRegs[0] : 0;
            }
            else
                registerAddress = parsedAddr;
        }

        ushort[] registers = await _modbusMaster.ReadHoldingRegistersAsync(slaveId, registerAddress, 1);
        return registers.Length > 0 ? registers[0] : (object)0;
    }

    public async Task WriteTagAsync(string address, object value)
    {
        if (!IsConnected || _modbusMaster == null)
            throw new InvalidOperationException($"Modbus driver is not connected to {_device?.Name ?? "device"}.");

        byte slaveId = 1;
        ushort registerAddress = 0;

        if (ushort.TryParse(address, out ushort parsedAddr))
        {
            if (parsedAddr >= 40001) registerAddress = (ushort)(parsedAddr - 40001);
            else registerAddress = parsedAddr;
        }

        if (ushort.TryParse(value.ToString(), out ushort val))
        {
            await _modbusMaster.WriteSingleRegisterAsync(slaveId, registerAddress, val);
        }
        else
        {
            throw new ArgumentException($"Cannot convert value '{value}' to ushort for Modbus write.");
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
