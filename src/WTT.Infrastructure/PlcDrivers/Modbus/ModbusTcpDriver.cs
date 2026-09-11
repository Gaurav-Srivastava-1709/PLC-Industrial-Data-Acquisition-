using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NModbus;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;

namespace WTT.Infrastructure.PlcDrivers.Modbus;

/// <summary>
/// Modbus/TCP driver (NModbus). Used for Yokogawa MW100/GM10, Advantech ADAM, Walchem and generic slaves.
/// Tags are grouped per register type into contiguous blocks so a 60-channel device is read with
/// a handful of requests instead of 60.
/// </summary>
public sealed class ModbusTcpDriver : IPlcDriver
{
    private const int MaxRegistersPerRequest = 120;   // protocol max 125
    private const int MaxCoilsPerRequest = 1900;      // protocol max 2000
    private const int MaxGapRegisters = 8;            // merge blocks separated by ≤ this many unused registers

    private readonly ILogger _logger;
    private TcpClient? _client;
    private IModbusMaster? _master;

    public ModbusTcpDriver(Device device, ILogger logger)
    {
        Device = device;
        _logger = logger;
    }

    public Device Device { get; }

    public PlcDriverCapabilities Capabilities { get; } = new(CanBrowseTags: false, CanWrite: true, SupportsBatchRead: true, RequiresAddressBlocks: true);

    public bool IsConnected => _client?.Connected == true && _master is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = Device.TimeoutMs, SendTimeout = Device.TimeoutMs };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Device.TimeoutMs);
            await client.ConnectAsync(Device.Host, Device.Port, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new PlcConnectionException(Device.Name, $"Connection to {Device.Host}:{Device.Port} timed out after {Device.TimeoutMs} ms.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            client.Dispose();
            throw new PlcConnectionException(Device.Name, $"Cannot connect to {Device.Host}:{Device.Port}: {ex.Message}", ex);
        }

        var factory = new ModbusFactory();
        var master = factory.CreateMaster(client);
        master.Transport.ReadTimeout = Device.TimeoutMs;
        master.Transport.WriteTimeout = Device.TimeoutMs;
        master.Transport.Retries = 1;

        _client = client;
        _master = master;
        _logger.LogDebug("Modbus connected to {Device} at {Host}:{Port} (unit {Unit}).", Device.Name, Device.Host, Device.Port, Device.UnitId);
    }

    public Task DisconnectAsync()
    {
        try { _master?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _master = null;
        _client = null;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<DeviceTag> tags, CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new PlcConnectionException(Device.Name, "Not connected.");
        var master = _master!;
        var results = new Dictionary<int, TagReadResult>(tags.Count);

        // 1. Resolve addresses; configuration errors never abort the scan.
        var items = new List<Item>(tags.Count);
        foreach (var tag in tags)
        {
            try
            {
                var fallback = tag.RegisterType ?? tag.Address?.RegisterType ?? ModbusRegisterType.HoldingRegisters;
                var parsed = ModbusAddressParser.Parse(tag.PlcAddress, fallback, Device.OneBasedAddressing);
                var isBit = parsed.RegisterType is ModbusRegisterType.Coils or ModbusRegisterType.DiscreteInputs;
                var count = isBit ? 1 : ModbusCodec.RegisterCount(tag.DataType, tag.ElementCount);
                var limit = isBit ? MaxCoilsPerRequest : MaxRegistersPerRequest;
                if (count > limit || parsed.Address < 0 || parsed.Address + count > 65536)
                    throw new FormatException($"Tag requires {count} point(s) at {parsed.Address}; Modbus limit is {limit} and addresses must remain within 0..65535.");
                items.Add(new Item(tag, parsed.RegisterType, parsed.Address, count));
            }
            catch (FormatException ex)
            {
                results[tag.Id] = TagReadResult.Bad(tag, ex.Message, TagQuality.ConfigError);
            }
        }

        // 2. Group into contiguous blocks per register type and read.
        foreach (var group in items.GroupBy(i => i.RegisterType))
        {
            var isBit = group.Key is ModbusRegisterType.Coils or ModbusRegisterType.DiscreteInputs;
            var max = isBit ? MaxCoilsPerRequest : MaxRegistersPerRequest;

            foreach (var block in BuildBlocks(group.OrderBy(i => i.Address).ToList(), max))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = block[0].Address;
                var end = block.Max(i => i.Address + i.Count);
                var length = (ushort)(end - start);

                try
                {
                    if (isBit)
                    {
                        var bits = group.Key == ModbusRegisterType.Coils
                            ? await master.ReadCoilsAsync(Device.UnitId, (ushort)start, length)
                            : await master.ReadInputsAsync(Device.UnitId, (ushort)start, length);
                        foreach (var item in block)
                            results[item.Tag.Id] = TagReadResult.Good(item.Tag, bits[item.Address - start]);
                    }
                    else
                    {
                        var regs = group.Key == ModbusRegisterType.HoldingRegisters
                            ? await master.ReadHoldingRegistersAsync(Device.UnitId, (ushort)start, length)
                            : await master.ReadInputRegistersAsync(Device.UnitId, (ushort)start, length);
                        foreach (var item in block)
                        {
                            var slice = regs.AsSpan(item.Address - start, item.Count);
                            var value = ModbusCodec.Decode(slice, item.Tag.DataType, Device.WordOrder, item.Tag.ElementCount);
                            results[item.Tag.Id] = TagReadResult.Good(item.Tag, value);
                        }
                    }
                }
                catch (SlaveException ex)
                {
                    // Device answered with an exception code (illegal address etc.) — data problem, not a link problem.
                    var msg = $"Modbus exception {ex.SlaveExceptionCode} reading {group.Key} {start}..{end - 1}: {ex.Message}";
                    _logger.LogWarning("{Device}: {Message}", Device.Name, msg);
                    foreach (var item in block) results[item.Tag.Id] = TagReadResult.Bad(item.Tag, msg);
                }
                catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException || ex.InnerException is SocketException)
                {
                    throw new PlcConnectionException(Device.Name, $"Link lost while reading {group.Key} {start}: {ex.Message}", ex);
                }
            }
        }

        return tags.Select(t => results.TryGetValue(t.Id, out var r) ? r : TagReadResult.Bad(t, "Not read")).ToList();
    }

    public async Task WriteAsync(TagWriteRequest request, CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new PlcConnectionException(Device.Name, "Not connected.");
        var tag = request.Tag;
        var fallback = tag.RegisterType ?? tag.Address?.RegisterType ?? ModbusRegisterType.HoldingRegisters;
        var parsed = ModbusAddressParser.Parse(tag.PlcAddress, fallback, Device.OneBasedAddressing);

        try
        {
            switch (parsed.RegisterType)
            {
                case ModbusRegisterType.Coils:
                    await _master!.WriteSingleCoilAsync(Device.UnitId, (ushort)parsed.Address, (bool)TagValueFormatter.ParseForWrite(TagDataType.Bool, request.Value));
                    break;
                case ModbusRegisterType.HoldingRegisters:
                    {
                        var typed = TagValueFormatter.ParseForWrite(tag.DataType, request.Value);
                        var regs = ModbusCodec.Encode(typed, tag.DataType, Device.WordOrder, tag.ElementCount);
                        if (regs.Length > 123) throw new PlcWriteException("A Modbus write may contain at most 123 registers.");
                        if (regs.Length == 1) await _master!.WriteSingleRegisterAsync(Device.UnitId, (ushort)parsed.Address, regs[0]);
                        else await _master!.WriteMultipleRegistersAsync(Device.UnitId, (ushort)parsed.Address, regs);
                        break;
                    }
                default:
                    throw new PlcWriteException($"{parsed.RegisterType} are read-only.");
            }
            _logger.LogInformation("{Device}: wrote '{Value}' to {Tag} ({Type} {Addr}).", Device.Name, request.Value, tag.Name, parsed.RegisterType, parsed.Address);
        }
        catch (PlcWriteException) { throw; }
        catch (FormatException ex) { throw new PlcWriteException($"'{request.Value}' is not a valid {tag.DataType}.", ex); }
        catch (SlaveException ex) { throw new PlcWriteException($"Device rejected the write (exception code {ex.SlaveExceptionCode}).", ex); }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            throw new PlcConnectionException(Device.Name, $"Link lost while writing {tag.Name}: {ex.Message}", ex);
        }
    }

    public Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Modbus devices do not expose a tag list. Define address blocks instead.");

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private static List<List<Item>> BuildBlocks(List<Item> sorted, int maxPoints)
    {
        var blocks = new List<List<Item>>();
        List<Item>? current = null;
        var currentEnd = 0;
        foreach (var item in sorted)
        {
            if (current is null || item.Address - currentEnd > MaxGapRegisters || item.Address + item.Count - current[0].Address > maxPoints)
            {
                current = [item];
                blocks.Add(current);
                currentEnd = item.Address + item.Count;
                continue;
            }
            current.Add(item);
            currentEnd = Math.Max(currentEnd, item.Address + item.Count);
        }
        return blocks;
    }

    private sealed record Item(DeviceTag Tag, ModbusRegisterType RegisterType, int Address, int Count);
}
