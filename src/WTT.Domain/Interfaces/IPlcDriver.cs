using WTT.Domain.Entities;
using WTT.Domain.ValueObjects;

namespace WTT.Domain.Interfaces;

/// <summary>
/// Protocol driver for one device instance. Implementations must be safe to call
/// sequentially from a single worker; they are not required to be thread-safe.
/// </summary>
public interface IPlcDriver : IAsyncDisposable
{
    Device Device { get; }

    PlcDriverCapabilities Capabilities { get; }

    bool IsConnected { get; }

    /// <summary>Open the connection. Throws <see cref="Exceptions.PlcConnectionException"/> on failure.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();

    /// <summary>
    /// Read a set of tags. Never throws for individual tag failures: each failure is
    /// reported through <see cref="TagReadResult.Quality"/>. Throws
    /// <see cref="Exceptions.PlcConnectionException"/> only when the transport is lost.
    /// </summary>
    Task<IReadOnlyList<TagReadResult>> ReadAsync(IReadOnlyList<DeviceTag> tags, CancellationToken cancellationToken);

    /// <summary>Write a value. Throws <see cref="Exceptions.PlcWriteException"/> on failure.</summary>
    Task WriteAsync(TagWriteRequest request, CancellationToken cancellationToken);

    /// <summary>Enumerate controller tags. Throws <see cref="NotSupportedException"/> if <see cref="PlcDriverCapabilities.CanBrowseTags"/> is false.</summary>
    Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the correct <see cref="IPlcDriver"/> for a device.</summary>
public interface IPlcDriverFactory
{
    IPlcDriver Create(Device device);
}
