using System.Net.Sockets;
using System.Text;
using libplctag;
using Microsoft.Extensions.Logging;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;

namespace WTT.Infrastructure.PlcDrivers.AllenBradley;

/// <summary>
/// Allen-Bradley EtherNet/IP driver built on libplctag (ControlLogix, CompactLogix, Micro800,
/// PLC-5, SLC-500, MicroLogix). libplctag keeps one CIP session per gateway/path and packs
/// multiple tag requests into a single packet up to the connection size, which replaces the
/// legacy hand-rolled "CIPConnectionSize - 53" batching.
/// Tag handles are cached for the lifetime of the connection.
/// </summary>
public sealed class AllenBradleyDriver : IPlcDriver
{
    private readonly ILogger _logger;
    private readonly Dictionary<int, Tag> _tags = new();
    private bool _connected;

    public AllenBradleyDriver(Device device, ILogger logger)
    {
        Device = device;
        _logger = logger;
        var isLogix = device.Model is DeviceModel.ControlLogix or DeviceModel.CompactLogix;
        Capabilities = new PlcDriverCapabilities(CanBrowseTags: isLogix, CanWrite: true, SupportsBatchRead: true, RequiresAddressBlocks: false);
    }

    public Device Device { get; }
    public PlcDriverCapabilities Capabilities { get; }
    public bool IsConnected => _connected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (Device.Port != 44818) throw new PlcConfigurationException("Allen-Bradley EtherNet/IP requires standard port 44818; libplctag does not support a custom gateway port.");
        await DisconnectAsync();

        // libplctag opens the CIP session lazily on the first tag. Probe TCP reachability first so
        // the worker gets an immediate, unambiguous "unreachable" instead of N tag timeouts.
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Device.TimeoutMs);
            await client.ConnectAsync(Device.Host, Device.Port <= 0 ? 44818 : Device.Port, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlcConnectionException(Device.Name, $"No response from {Device.Host}:{Device.Port} within {Device.TimeoutMs} ms.");
        }
        catch (SocketException ex)
        {
            throw new PlcConnectionException(Device.Name, $"Cannot reach {Device.Host}:{Device.Port}: {ex.Message}", ex);
        }

        _connected = true;
        _logger.LogDebug("EtherNet/IP reachable for {Device} at {Host} (type {Type}, path '{Path}').", Device.Name, Device.Host, MapPlcType(Device.Model), Path);
    }

    public Task DisconnectAsync()
    {
        foreach (var t in _tags.Values)
        {
            try { t.Dispose(); } catch { /* ignore */ }
        }
        _tags.Clear();
        _connected = false;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<DeviceTag> tags, CancellationToken cancellationToken)
    {
        if (!_connected) throw new PlcConnectionException(Device.Name, "Not connected.");
        var results = new TagReadResult[tags.Count];
        var timeouts = 0;
        var attempted = 0;

        // Issue all reads concurrently; libplctag packs them into as few CIP packets as the connection size allows.
        var pending = new List<(int Index, DeviceTag Tag, Tag Handle, AllenBradleyAddress.Parsed Parsed, Task Task)>();
        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            try
            {
                var (handle, parsed) = GetOrCreate(tag);
                attempted++;
                pending.Add((i, tag, handle, parsed, handle.ReadAsync(cancellationToken)));
            }
            catch (FormatException ex)
            {
                results[i] = TagReadResult.Bad(tag, ex.Message, TagQuality.ConfigError);
            }
            catch (LibPlcTagException ex)
            {
                results[i] = TagReadResult.Bad(tag, $"libplctag: {StatusOf(ex)}", IsConfigError(StatusOf(ex)) ? TagQuality.ConfigError : TagQuality.Bad);
                if (StatusOf(ex) == Status.ErrorTimeout) timeouts++;
            }
        }

        foreach (var (index, tag, handle, parsed, task) in pending)
        {
            try
            {
                await task;
                results[index] = TagReadResult.Good(tag, Decode(handle, tag, parsed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (LibPlcTagException ex)
            {
                if (StatusOf(ex) == Status.ErrorTimeout) timeouts++;
                var quality = IsConfigError(StatusOf(ex)) ? TagQuality.ConfigError : TagQuality.Bad;
                results[index] = TagReadResult.Bad(tag, $"libplctag: {StatusOf(ex)}", quality);
                if (quality == TagQuality.ConfigError) DropHandle(tag.Id);
            }
            catch (Exception ex)
            {
                results[index] = TagReadResult.Bad(tag, ex.Message);
            }
        }

        if (attempted > 0 && timeouts == attempted)
            throw new PlcConnectionException(Device.Name, $"All {attempted} tag reads timed out — link to {Device.Host} lost.");

        return results;
    }

    public async Task WriteAsync(TagWriteRequest request, CancellationToken cancellationToken)
    {
        if (!_connected) throw new PlcConnectionException(Device.Name, "Not connected.");
        var tag = request.Tag;
        try
        {
            var (handle, parsed) = GetOrCreate(tag);
            if (parsed.BitIndex.HasValue)
            {
                await handle.ReadAsync(cancellationToken); // read-modify-write for a single bit
                handle.SetBit(parsed.BitIndex.Value, (bool)TagValueFormatter.ParseForWrite(TagDataType.Bool, request.Value));
            }
            else
            {
                var typed = TagValueFormatter.ParseForWrite(tag.DataType, request.Value);
                Encode(handle, tag, typed);
            }
            await handle.WriteAsync(cancellationToken);
            _logger.LogInformation("{Device}: wrote '{Value}' to {Tag} ({Address}).", Device.Name, request.Value, tag.Name, tag.PlcAddress);
        }
        catch (FormatException ex) { throw new PlcWriteException($"'{request.Value}' is not a valid {tag.DataType}.", ex); }
        catch (LibPlcTagException ex) when (StatusOf(ex) == Status.ErrorTimeout) { throw new PlcConnectionException(Device.Name, $"Write to {tag.Name} timed out.", ex); }
        catch (LibPlcTagException ex) { throw new PlcWriteException($"Write to {tag.PlcAddress} failed: {StatusOf(ex)}", ex); }
    }

    public async Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(CancellationToken cancellationToken)
    {
        if (!Capabilities.CanBrowseTags) throw new NotSupportedException("Tag browsing is only available on ControlLogix / CompactLogix.");
        var result = new List<DiscoveredTag>();
        var programs = new List<string>();

        foreach (var entry in await ReadTagListAsync("@tags", cancellationToken))
        {
            if (entry.Name.StartsWith("Program:", StringComparison.OrdinalIgnoreCase) && (entry.Type & 0x1000) != 0)
            {
                programs.Add(entry.Name);
                continue;
            }
            AddDiscovered(result, entry, prefix: null);
        }

        foreach (var program in programs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in await ReadTagListAsync($"{program}.@tags", cancellationToken))
                    AddDiscovered(result, entry, prefix: program + ".");
            }
            catch (LibPlcTagException ex)
            {
                _logger.LogWarning("{Device}: could not list tags of {Program}: {Status}", Device.Name, program, StatusOf(ex));
            }
        }

        _logger.LogInformation("{Device}: discovered {Count} tags ({Programs} program scopes).", Device.Name, result.Count, programs.Count);
        return result.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    // ---------------------------------------------------------------- internals

    private string? Path
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Device.CipPath)) return Device.CipPath;
            return Device.Model is DeviceModel.ControlLogix or DeviceModel.CompactLogix ? $"1,{Device.Slot}" : null;
        }
    }

    internal static PlcType MapPlcType(DeviceModel model) => model switch
    {
        DeviceModel.ControlLogix or DeviceModel.CompactLogix => PlcType.ControlLogix,
        DeviceModel.Micro800 => PlcType.Micro800,
        DeviceModel.Plc5 => PlcType.Plc5,
        DeviceModel.Slc500 => PlcType.Slc500,
        DeviceModel.MicroLogix => PlcType.MicroLogix,
        _ => PlcType.ControlLogix,
    };

    private (Tag Handle, AllenBradleyAddress.Parsed Parsed) GetOrCreate(DeviceTag tag)
    {
        var parsed = AllenBradleyAddress.Parse(tag.PlcAddress);
        if (_tags.TryGetValue(tag.Id, out var existing) && existing.Name == parsed.TagName)
            return (existing, parsed);

        DropHandle(tag.Id);
        var handle = new Tag
        {
            Gateway = Device.Host,
            Path = Path,
            PlcType = MapPlcType(Device.Model),
            Protocol = Protocol.ab_eip,
            Name = parsed.TagName,
            ElementCount = Math.Max(1, Math.Max(parsed.ElementCount, tag.DataType == TagDataType.String ? 1 : tag.ElementCount)),
            Timeout = TimeSpan.FromMilliseconds(Math.Max(500, Device.TimeoutMs)),
            AllowPacking = true,
        };
        if (Device.Model is DeviceModel.ControlLogix or DeviceModel.CompactLogix && Device.ConnectionSize >= 4000)
            handle.UseConnectedMessaging = true; // large forward-open (4002 bytes)

        handle.Initialize();
        _tags[tag.Id] = handle;
        return (handle, parsed);
    }

    private void DropHandle(int tagId)
    {
        if (_tags.Remove(tagId, out var h))
        {
            try { h.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>libplctag.NET exceptions carry the status only in the message text.</summary>
    internal static Status StatusOf(LibPlcTagException ex)
    {
        var text = ex.Message?.Trim() ?? string.Empty;
        if (Enum.TryParse<Status>(text, ignoreCase: true, out var s)) return s;
        // Fallback: find any status name inside the message.
        foreach (var name in Enum.GetNames<Status>())
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return Enum.Parse<Status>(name);
        return Status.ErrorBadStatus;
    }

    private static bool IsConfigError(Status s) => s is Status.ErrorBadParam or Status.ErrorNotFound or Status.ErrorUnsupported
        or Status.ErrorBadData or Status.ErrorBadConfig or Status.ErrorOutOfBounds or Status.ErrorNotAllowed or Status.ErrorNoData;

    private static object Decode(Tag h, DeviceTag tag, AllenBradleyAddress.Parsed parsed)
    {
        if (parsed.BitIndex.HasValue)
            return h.GetBit(parsed.BitIndex.Value);

        // PCCC ASCII file range: every 16-bit word carries two characters (high byte first).
        if (parsed.FilePrefix is not null && parsed.FilePrefix.StartsWith('A') && !parsed.FilePrefix.StartsWith("ST", StringComparison.Ordinal))
        {
            var sb = new StringBuilder();
            var size = h.GetSize();
            for (var off = 0; off + 1 < size; off += 2)
            {
                var w = h.GetUInt16(off);
                var hi = (char)(w >> 8); var lo = (char)(w & 0xFF);
                if (hi != '\0') sb.Append(hi);
                if (lo != '\0') sb.Append(lo);
            }
            return sb.ToString();
        }

        if (tag.DataType == TagDataType.String)
            return h.GetString(0);

        var count = Math.Max(1, tag.ElementCount);
        var elemSize = ElementSize(h, tag, count);
        if (count == 1) return ReadOne(h, tag.DataType, 0);

        var values = new object[count];
        for (var i = 0; i < count; i++) values[i] = ReadOne(h, tag.DataType, i * elemSize);
        return values;
    }

    private static int ElementSize(Tag h, DeviceTag tag, int count)
    {
        var size = h.GetSize();
        return count > 0 && size >= count ? size / count : TagValueFormatter.DefaultByteSize(tag.DataType);
    }

    private static object ReadOne(Tag h, TagDataType type, int offset) => type switch
    {
        TagDataType.Bool => h.GetSize() == 1 ? h.GetUInt8(offset) != 0 : h.GetInt32(offset) != 0,
        TagDataType.Byte => h.GetUInt8(offset),
        TagDataType.Int16 => h.GetInt16(offset),
        TagDataType.UInt16 => h.GetUInt16(offset),
        TagDataType.Int32 => h.GetInt32(offset),
        TagDataType.UInt32 => h.GetUInt32(offset),
        TagDataType.Int64 => h.GetInt64(offset),
        TagDataType.UInt64 => h.GetUInt64(offset),
        TagDataType.Float32 => h.GetFloat32(offset),
        TagDataType.Float64 => h.GetFloat64(offset),
        _ => h.GetInt32(offset),
    };

    private static void Encode(Tag h, DeviceTag tag, object value)
    {
        switch (tag.DataType)
        {
            case TagDataType.Bool:
                if (h.GetSize() == 1) h.SetUInt8(0, (byte)((bool)value ? 1 : 0)); else h.SetInt32(0, (bool)value ? 1 : 0);
                break;
            case TagDataType.Byte: h.SetUInt8(0, (byte)value); break;
            case TagDataType.Int16: h.SetInt16(0, (short)value); break;
            case TagDataType.UInt16: h.SetUInt16(0, (ushort)value); break;
            case TagDataType.Int32: h.SetInt32(0, (int)value); break;
            case TagDataType.UInt32: h.SetUInt32(0, (uint)value); break;
            case TagDataType.Int64: h.SetInt64(0, (long)value); break;
            case TagDataType.UInt64: h.SetUInt64(0, (ulong)value); break;
            case TagDataType.Float32: h.SetFloat32(0, (float)value); break;
            case TagDataType.Float64: h.SetFloat64(0, (double)value); break;
            case TagDataType.String: h.SetString(0, (string)value); break;
            default: h.SetInt32(0, Convert.ToInt32(value)); break;
        }
    }

    // ---- tag list (@tags) parsing ----

    private sealed record TagListEntry(uint InstanceId, ushort Type, ushort ElementLength, uint[] Dims, string Name);

    private async Task<List<TagListEntry>> ReadTagListAsync(string name, CancellationToken ct)
    {
        using var listTag = new Tag
        {
            Gateway = Device.Host,
            Path = Path,
            PlcType = PlcType.ControlLogix,
            Protocol = Protocol.ab_eip,
            Name = name,
            Timeout = TimeSpan.FromMilliseconds(Math.Max(5000, Device.TimeoutMs * 3)),
        };
        await listTag.InitializeAsync(ct);
        await listTag.ReadAsync(ct);

        var entries = new List<TagListEntry>();
        var size = listTag.GetSize();
        var offset = 0;
        while (offset + 22 <= size)
        {
            var instanceId = listTag.GetUInt32(offset);
            var type = listTag.GetUInt16(offset + 4);
            var elemLen = listTag.GetUInt16(offset + 6);
            var dims = new[] { listTag.GetUInt32(offset + 8), listTag.GetUInt32(offset + 12), listTag.GetUInt32(offset + 16) };
            var nameLen = listTag.GetUInt16(offset + 20);
            offset += 22;
            if (offset + nameLen > size) break;
            var nameBytes = new byte[nameLen];
            for (var i = 0; i < nameLen; i++) nameBytes[i] = listTag.GetUInt8(offset + i);
            offset += nameLen;
            entries.Add(new TagListEntry(instanceId, type, elemLen, dims, Encoding.ASCII.GetString(nameBytes)));
        }
        return entries;
    }

    private static void AddDiscovered(List<DiscoveredTag> result, TagListEntry e, string? prefix)
    {
        if ((e.Type & 0x1000) != 0) return;                  // system tag
        if (e.Name.StartsWith("__", StringComparison.Ordinal)) return;
        var isStructure = (e.Type & 0x8000) != 0;
        var dimCount = (e.Type & 0x6000) >> 13;
        var elements = 1L;
        for (var i = 0; i < dimCount; i++) if (e.Dims[i] > 0) elements *= e.Dims[i];

        var fullName = prefix + e.Name;
        TagDataType? dataType;
        string typeName;
        if (isStructure)
        {
            // Logix STRING is a structure of 88 bytes (LEN + 82 chars + pad).
            if (e.ElementLength == 88) { dataType = TagDataType.String; typeName = "STRING"; isStructure = false; }
            else { dataType = null; typeName = $"UDT(0x{e.Type & 0x0FFF:X3})"; }
        }
        else
        {
            (dataType, typeName) = (e.Type & 0xFF) switch
            {
                0xC1 => (TagDataType.Bool, "BOOL"),
                0xC2 => (TagDataType.Byte, "SINT"),
                0xC3 => (TagDataType.Int16, "INT"),
                0xC4 => (TagDataType.Int32, "DINT"),
                0xC5 => (TagDataType.Int64, "LINT"),
                0xC6 => (TagDataType.Byte, "USINT"),
                0xC7 => (TagDataType.UInt16, "UINT"),
                0xC8 => (TagDataType.UInt32, "UDINT"),
                0xC9 => (TagDataType.UInt64, "ULINT"),
                0xCA => (TagDataType.Float32, "REAL"),
                0xCB => (TagDataType.Float64, "LREAL"),
                0xD3 => (TagDataType.UInt32, "DWORD"),
                _ => ((TagDataType?)null, $"0x{e.Type:X4}"),
            };
        }

        var byteCount = e.ElementLength;
        if (dimCount == 0 || elements <= 1)
        {
            result.Add(new DiscoveredTag(fullName, dataType, dataType == TagDataType.String ? 82 : 1, byteCount, isStructure, typeName));
            return;
        }

        // Arrays: expose each element (legacy behaviour) when reasonably small, else the whole array.
        if (elements <= 256 && dimCount == 1 && !isStructure)
        {
            for (var i = 0; i < elements; i++)
                result.Add(new DiscoveredTag($"{fullName}[{i}]", dataType, dataType == TagDataType.String ? 82 : 1, byteCount, false, typeName));
        }
        else
        {
            result.Add(new DiscoveredTag(fullName, dataType, (int)Math.Min(elements, int.MaxValue), byteCount * (int)Math.Min(elements, 65535), isStructure, $"{typeName}[{elements}]"));
        }
    }
}
