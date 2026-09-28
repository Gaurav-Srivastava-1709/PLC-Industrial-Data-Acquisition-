// =============================================================================
// FILE      : src/WTT.Infrastructure/Capture/LiveValueCache.cs
// LAYER     : Infrastructure - in-process live-value store shared by capture + UI
// PURPOSE   : Holds the newest value/quality/timestamp per (device, tag) so live
//             screens need not hit the database on every poll. A cache, not truth.
// DI        : Singleton - one instance per process, shared by the capture workers
//             and the WPF views hosted in that same process.
// THREADING : Lock-free. Nested ConcurrentDictionary<deviceId, ConcurrentDictionary<
//             tagId, LiveTagValue>> so reads never block and a write to one device
//             never disturbs another. Values are immutable records, so Set/overwrite
//             is the only mutation and needs no extra synchronisation.
// DEPENDS ON: WTT.Domain.ValueObjects.LiveTagValue (immutable)
// USED BY   : DeviceCaptureWorker (Set), DataCaptureBackgroundService (GetAll/ClearDevice), live views, plc operations screens.
// KEY RULES :
//   * IN-PROCESS ONLY. WTT.Service and WTT.UI are separate OS processes with
//     SEPARATE, non-shared instances. Anything the UI must see has to be persisted.
//   * Use DeviceRuntimeState / DeviceTags.CurrentValue in the DB for cross-process
//     state (connection status, last scan, last good read). Reading this cache from
//     WTT.UI always looks like "no data".
//   * Never treat a value here as durable: it is lost on restart and is only as
//     fresh as the last CurrentValueFlushSeconds window (5 s).
//   * Entries are never trimmed by age. ClearDevice(deviceId) is called by the
//     supervisor when a worker stops so a removed device's values do not linger.
//   * GetAll() materialises a sorted list per call - fine for one device's tags, not
//     for a tight loop across every device.
// =============================================================================
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
