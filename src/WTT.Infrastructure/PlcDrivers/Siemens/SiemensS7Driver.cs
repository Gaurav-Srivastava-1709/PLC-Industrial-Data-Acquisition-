using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using S7.Net;
using S7.Net.Types;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;

namespace WTT.Infrastructure.PlcDrivers.Siemens;

/// <summary>
/// Siemens S7-300/400/1200/1500 driver over S7comm (S7.Net). Reads are batched into
/// multi-variable requests sized for the CPU's PDU. Values are decoded according to the
/// tag's <see cref="TagDataType"/> so a REAL is delivered as a float, not a raw DWORD.
/// </summary>
public sealed class SiemensS7Driver : IPlcDriver
{
    private const int MaxItemsPerRequest = 19;
    private const int MaxPayloadPerRequest = 200; // bytes (PDU 240 minus headers)

    private readonly ILogger _logger;
    private Plc? _plc;

    public SiemensS7Driver(Device device, ILogger logger)
    {
        Device = device;
        _logger = logger;
    }

    public Device Device { get; }

    public PlcDriverCapabilities Capabilities { get; } = new(CanBrowseTags: false, CanWrite: true, SupportsBatchRead: true, RequiresAddressBlocks: false);

    public bool IsConnected => _plc?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        var plc = new Plc(MapCpu(Device.Model), Device.Host, Device.Port <= 0 ? 102 : Device.Port, (short)Device.Rack, (short)Device.Slot)
        {
            ReadTimeout = Device.TimeoutMs,
            WriteTimeout = Device.TimeoutMs,
        };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Device.TimeoutMs);
            await plc.OpenAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            plc.Close();
            throw new PlcConnectionException(Device.Name, $"Connection to {Device.Host}:{Device.Port} (rack {Device.Rack}, slot {Device.Slot}) timed out.");
        }
        catch (Exception ex) when (ex is PlcException or SocketException or IOException)
        {
            plc.Close();
            throw new PlcConnectionException(Device.Name, $"Cannot connect to S7 at {Device.Host} (rack {Device.Rack}, slot {Device.Slot}): {ex.Message}", ex);
        }
        _plc = plc;
        _logger.LogDebug("S7 connected to {Device} ({Cpu}) at {Host}.", Device.Name, plc.CPU, Device.Host);
    }

    public Task DisconnectAsync()
    {
        try { _plc?.Close(); } catch { /* ignore */ }
        _plc = null;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<DeviceTag> tags, CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new PlcConnectionException(Device.Name, "Not connected.");
        var results = new Dictionary<int, TagReadResult>(tags.Count);
        var items = new List<(DeviceTag Tag, DataItem Item, int Size)>();

        foreach (var tag in tags)
        {
            try
            {
                var item = BuildDataItem(tag);
                items.Add((tag, item, EstimateSize(item)));
            }
            catch (Exception ex) when (ex is FormatException or InvalidAddressException or ArgumentException)
            {
                results[tag.Id] = TagReadResult.Bad(tag, $"Invalid S7 address '{tag.PlcAddress}': {ex.Message}", TagQuality.ConfigError);
            }
        }

        foreach (var chunk in Chunk(items))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = chunk.Select(c => c.Item).ToList();
            try
            {
                await _plc!.ReadMultipleVarsAsync(list, cancellationToken);
                foreach (var (tag, item, _) in chunk)
                    results[tag.Id] = item.Value is null
                        ? TagReadResult.Bad(tag, "No value returned")
                        : TagReadResult.Good(tag, Normalize(item.Value, tag));
            }
            catch (PlcException ex) when (ex.ErrorCode is ErrorCode.ConnectionError or ErrorCode.WriteData or ErrorCode.ReadData)
            {
                throw new PlcConnectionException(Device.Name, $"S7 link error: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                throw new PlcConnectionException(Device.Name, $"S7 link lost: {ex.Message}", ex);
            }
            catch (PlcException ex)
            {
                // Address error for this chunk: fall back to per-item reads so one bad tag does not poison the others.
                _logger.LogDebug(ex, "{Device}: multi-var read failed ({Code}); retrying items individually.", Device.Name, ex.ErrorCode);
                foreach (var (tag, item, _) in chunk)
                {
                    try
                    {
                        await _plc!.ReadMultipleVarsAsync([item], cancellationToken);
                        results[tag.Id] = TagReadResult.Good(tag, Normalize(item.Value!, tag));
                    }
                    catch (PlcException inner) when (inner.ErrorCode is ErrorCode.ConnectionError)
                    {
                        throw new PlcConnectionException(Device.Name, inner.Message, inner);
                    }
                    catch (Exception inner)
                    {
                        results[tag.Id] = TagReadResult.Bad(tag, inner.Message);
                    }
                }
            }
        }

        return tags.Select(t => results.TryGetValue(t.Id, out var r) ? r : TagReadResult.Bad(t, "Not read")).ToList();
    }

    public async Task WriteAsync(TagWriteRequest request, CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new PlcConnectionException(Device.Name, "Not connected.");
        var tag = request.Tag;
        try
        {
            var item = BuildDataItem(tag);
            item.Value = TagValueFormatter.ParseForWrite(tag.DataType, request.Value);
            if (tag.DataType == TagDataType.String) item.Count = Math.Max(tag.ElementCount, ((string)item.Value).Length);
            await _plc!.WriteAsync([item]);
            _logger.LogInformation("{Device}: wrote '{Value}' to {Tag} ({Address}).", Device.Name, request.Value, tag.Name, tag.PlcAddress);
        }
        catch (FormatException ex) { throw new PlcWriteException($"'{request.Value}' is not a valid {tag.DataType}.", ex); }
        catch (PlcException ex) when (ex.ErrorCode == ErrorCode.ConnectionError) { throw new PlcConnectionException(Device.Name, ex.Message, ex); }
        catch (Exception ex) when (ex is PlcException or InvalidAddressException or ArgumentException) { throw new PlcWriteException($"S7 write to {tag.PlcAddress} failed: {ex.Message}", ex); }
        catch (Exception ex) when (ex is SocketException or IOException) { throw new PlcConnectionException(Device.Name, ex.Message, ex); }
    }

    public Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("S7 controllers do not expose a symbol table over S7comm. Enter DB addresses manually.");

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    // ---- helpers ----

    internal static CpuType MapCpu(DeviceModel model) => model switch
    {
        DeviceModel.S7_300 => CpuType.S7300,
        DeviceModel.S7_400 => CpuType.S7400,
        DeviceModel.S7_1200 => CpuType.S71200,
        DeviceModel.S7_1500 => CpuType.S71500,
        _ => CpuType.S71200,
    };

    /// <summary>Build a DataItem from the address and force the VarType from the tag's data type.</summary>
    internal static DataItem BuildDataItem(DeviceTag tag)
    {
        var item = DataItem.FromAddress(tag.PlcAddress.Trim());
        item.VarType = tag.DataType switch
        {
            TagDataType.Bool => VarType.Bit,
            TagDataType.Byte => VarType.Byte,
            TagDataType.Int16 => VarType.Int,
            TagDataType.UInt16 => VarType.Word,
            TagDataType.Int32 => VarType.DInt,
            TagDataType.UInt32 => VarType.DWord,
            TagDataType.Float32 => VarType.Real,
            TagDataType.Float64 => VarType.LReal,
            TagDataType.String => VarType.S7String,
            TagDataType.Int64 or TagDataType.UInt64 => VarType.LReal, // S7 has no 64-bit int in S7.Net; treat as LReal bits
            _ => item.VarType,
        };
        item.Count = tag.DataType == TagDataType.String ? Math.Max(1, tag.ElementCount) : Math.Max(1, tag.ElementCount);
        return item;
    }

    private static int EstimateSize(DataItem item) => item.VarType switch
    {
        VarType.Bit or VarType.Byte => 1 * item.Count,
        VarType.Word or VarType.Int or VarType.Timer or VarType.Counter => 2 * item.Count,
        VarType.DWord or VarType.DInt or VarType.Real => 4 * item.Count,
        VarType.LReal or VarType.DateTime => 8 * item.Count,
        VarType.String or VarType.S7String => item.Count + 2,
        _ => 4,
    } + 4;

    private static IEnumerable<List<(DeviceTag Tag, DataItem Item, int Size)>> Chunk(List<(DeviceTag Tag, DataItem Item, int Size)> items)
    {
        var chunk = new List<(DeviceTag, DataItem, int)>();
        var bytes = 0;
        foreach (var it in items)
        {
            if (chunk.Count > 0 && (chunk.Count >= MaxItemsPerRequest || bytes + it.Size > MaxPayloadPerRequest))
            {
                yield return chunk;
                chunk = [];
                bytes = 0;
            }
            chunk.Add(it);
            bytes += it.Size;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    private static object Normalize(object value, DeviceTag tag)
    {
        // S7.Net already returns CLR types matching VarType. Only strings may carry padding.
        return value is string s ? s.TrimEnd('\0', ' ') : value;
    }
}
