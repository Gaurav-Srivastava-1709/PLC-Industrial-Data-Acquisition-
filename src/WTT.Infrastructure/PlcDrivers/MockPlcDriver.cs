using System.Collections.Concurrent;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;

namespace WTT.Infrastructure.PlcDrivers;

/// <summary>
/// Deterministic PLC simulator. Every tag produces a realistic, smoothly varying signal derived
/// from its name so the UI, trigger logic and transfer pipeline can be exercised without hardware.
/// Written values override the simulation until the next write.
/// </summary>
public sealed class MockPlcDriver : IPlcDriver
{
    private static readonly ConcurrentDictionary<string, object> WrittenValues = new();
    private static readonly DateTime Epoch = DateTime.UtcNow;

    public MockPlcDriver(Device device) => Device = device;

    public Device Device { get; }

    public PlcDriverCapabilities Capabilities { get; } = new(CanBrowseTags: true, CanWrite: true, SupportsBatchRead: true, RequiresAddressBlocks: false);

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<DeviceTag> tags, CancellationToken cancellationToken)
    {
        var t = (DateTime.UtcNow - Epoch).TotalSeconds;
        var list = new List<TagReadResult>(tags.Count);
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag.PlcAddress))
            {
                list.Add(TagReadResult.Bad(tag, "Empty address", TagQuality.ConfigError));
                continue;
            }
            var key = Key(tag);
            if (WrittenValues.TryGetValue(key, out var written))
            {
                list.Add(TagReadResult.Good(tag, written));
                continue;
            }
            list.Add(TagReadResult.Good(tag, Simulate(tag, t)));
        }
        return Task.FromResult<IReadOnlyList<TagReadResult>>(list);
    }

    public Task WriteAsync(TagWriteRequest request, CancellationToken cancellationToken)
    {
        var typed = TagValueFormatter.ParseForWrite(request.Tag.DataType, request.Value); // throws FormatException on bad input
        WrittenValues[Key(request.Tag)] = typed;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiscoveredTag> sample =
        [
            new("Sim.Temperature", TagDataType.Float32, 1, 4, false, "REAL"),
            new("Sim.Pressure", TagDataType.Float32, 1, 4, false, "REAL"),
            new("Sim.Speed", TagDataType.Int32, 1, 4, false, "DINT"),
            new("Sim.Counter", TagDataType.Int32, 1, 4, false, "DINT"),
            new("Sim.Level", TagDataType.Int16, 1, 2, false, "INT"),
            new("Sim.Running", TagDataType.Bool, 1, 1, false, "BOOL"),
            new("Sim.Trigger", TagDataType.Bool, 1, 1, false, "BOOL"),
            new("Sim.BatchId", TagDataType.String, 82, 88, false, "STRING"),
            new("Sim.Array[0]", TagDataType.Float32, 1, 4, false, "REAL"),
            new("Sim.Array[1]", TagDataType.Float32, 1, 4, false, "REAL"),
        ];
        return Task.FromResult(sample);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private string Key(DeviceTag tag) => $"{Device.Id}:{tag.PlcAddress}";

    private static object Simulate(DeviceTag tag, double t)
    {
        var seed = StableHash(tag.PlcAddress);
        var phase = (seed % 628) / 100.0;
        var period = 20 + seed % 40;           // 20..59 s
        var wave = Math.Sin(2 * Math.PI * t / period + phase);
        var name = tag.PlcAddress.ToLowerInvariant();

        switch (tag.DataType)
        {
            case TagDataType.Bool:
                // "trigger"-like tags toggle every 30 s; others follow the wave.
                return name.Contains("trig") ? ((int)(t / 30) % 2) == 0 : wave > 0;
            case TagDataType.String:
                return name.Contains("batch") ? $"BATCH-{Epoch:yyyyMMdd}-{(int)(t / 300):D3}" : $"SIM-{seed % 1000:D3}";
            case TagDataType.Float32:
            case TagDataType.Float64:
                {
                    double baseValue = name.Contains("temp") ? 65 : name.Contains("press") ? 4.2 : name.Contains("level") ? 50 : 100;
                    double amplitude = baseValue * 0.15;
                    var noise = ((seed * 7919 + (long)(t * 10)) % 100) / 1000.0 * amplitude;
                    var v = baseValue + amplitude * wave + noise;
                    return tag.DataType == TagDataType.Float32 ? (object)(float)Math.Round(v, 3) : Math.Round(v, 4);
                }
            case TagDataType.Int16:
            case TagDataType.UInt16:
                return tag.DataType == TagDataType.Int16 ? (object)(short)(500 + 300 * wave) : (ushort)(500 + 300 * wave);
            case TagDataType.Int32:
            case TagDataType.UInt32:
                {
                    if (name.Contains("count")) return tag.DataType == TagDataType.Int32 ? (object)(int)(t / 2) : (uint)(t / 2);
                    var v = 1500 + 400 * wave;
                    return tag.DataType == TagDataType.Int32 ? (object)(int)v : (uint)v;
                }
            case TagDataType.Int64: return (long)(t * 1000);
            case TagDataType.UInt64: return (ulong)(t * 1000);
            case TagDataType.Byte: return (byte)(128 + 100 * wave);
            default: return (int)(100 * wave);
        }
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in s) h = h * 31 + c;
            return Math.Abs(h);
        }
    }
}
