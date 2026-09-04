using System.Collections.Concurrent;
using WTT.Domain.ValueObjects;

namespace WTT.Infrastructure.Capture;

/// <summary>In-memory latest value per tag, shared by capture workers and the UI.</summary>
public sealed class LiveValueCache
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, LiveTagValue>> _byDevice = new();

    public void Set(LiveTagValue value)
    {
        var device = _byDevice.GetOrAdd(value.DeviceId, _ => new ConcurrentDictionary<int, LiveTagValue>());
        device[value.TagId] = value;
    }

    public LiveTagValue? Get(int deviceId, int tagId) =>
        _byDevice.TryGetValue(deviceId, out var d) && d.TryGetValue(tagId, out var v) ? v : null;

    public IReadOnlyList<LiveTagValue> GetAll(int deviceId) =>
        _byDevice.TryGetValue(deviceId, out var d) ? d.Values.OrderBy(v => v.TagName).ToList() : Array.Empty<LiveTagValue>();

    public void ClearDevice(int deviceId) => _byDevice.TryRemove(deviceId, out _);
}
