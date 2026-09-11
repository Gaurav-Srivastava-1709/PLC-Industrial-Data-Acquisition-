using WTT.Application.DTOs;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;

namespace WTT.Application.Interfaces;

public interface IDeviceService
{
    Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default);
    Task<Device?> GetByIdAsync(int id, bool includeChildren = false, CancellationToken ct = default);
    Task<Device> CreateAsync(Device device, CancellationToken ct = default);
    Task<Device> UpdateAsync(Device device, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Toggle capture / monitor flags (Operator permission).</summary>
    Task SetRunStateAsync(int id, bool? captureEnabled, bool? monitorEnabled, CancellationToken ct = default);

    /// <summary>Persist capture mode + PLC trigger tag.</summary>
    Task SetCaptureModeAsync(int id, CaptureMode mode, string? triggerTagName, CancellationToken ct = default);
}

public interface IDeviceAddressService
{
    Task<IReadOnlyList<DeviceAddress>> GetForDeviceAsync(int deviceId, CancellationToken ct = default);
    Task<DeviceAddress> SaveAsync(DeviceAddress address, CancellationToken ct = default);
    Task DeleteAsync(int addressId, CancellationToken ct = default);

    /// <summary>Regenerate tags for a Sequential block using the model's naming/decoding rules.</summary>
    Task<int> GenerateTagsAsync(int addressId, CancellationToken ct = default);
}

public interface ITagService
{
    Task<IReadOnlyList<DeviceTag>> GetForDeviceAsync(int deviceId, TagKind? kind = null, CancellationToken ct = default);
    Task<DeviceTag?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<DeviceTag> SaveAsync(DeviceTag tag, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task SetSelectedAsync(IEnumerable<int> tagIds, bool selected, CancellationToken ct = default);

    /// <summary>Set the constant value of a Custom tag (stamped into every scan).</summary>
    Task SetCustomValueAsync(int tagId, string value, CancellationToken ct = default);

    /// <summary>Merge browsed controller tags into the device's PLC tag list. Returns number of tags added.</summary>
    Task<int> ImportDiscoveredAsync(int deviceId, IEnumerable<DiscoveredTag> discovered, bool selectNew, CancellationToken ct = default);
}

public interface ITriggerService
{
    Task<IReadOnlyList<DeviceTriggerCondition>> GetForDeviceAsync(int deviceId, CancellationToken ct = default);
    Task<DeviceTriggerCondition> AddAsync(DeviceTriggerCondition condition, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task ReorderAsync(int deviceId, IReadOnlyList<int> orderedIds, CancellationToken ct = default);
}

public interface IDeviceServerService
{
    Task<IReadOnlyList<DeviceServer>> GetAllAsync(CancellationToken ct = default);
    Task<DeviceServer?> GetForDeviceAsync(int deviceId, CancellationToken ct = default);

    /// <param name="plainPassword">Clear-text password; encrypted before storage. Null keeps the existing secret.</param>
    Task<DeviceServer> SaveAsync(DeviceServer server, string? plainPassword, CancellationToken ct = default);
    Task DeleteAsync(int deviceId, CancellationToken ct = default);
    Task SetEnabledAsync(int deviceId, bool enabled, CancellationToken ct = default);
    Task<ConnectionTestResult> TestConnectionAsync(DeviceServer server, string? plainPassword, CancellationToken ct = default);
}

public interface IExternalQueryService
{
    Task<IReadOnlyList<DeviceQuery>> GetForDeviceAsync(int deviceId, CancellationToken ct = default);
    Task<DeviceQuery> SaveAsync(DeviceQuery query, string? plainPassword, CancellationToken ct = default);
    Task DeleteAsync(int queryId, CancellationToken ct = default);

    /// <summary>Execute the query, store discovered columns and mark it verified.</summary>
    Task<ExternalQueryVerifyResult> VerifyAsync(int queryId, CancellationToken ct = default);

    /// <summary>Run every verified query of the device and copy the first-row values into External tags' SetValue.</summary>
    Task<bool> RefreshExternalTagValuesAsync(int deviceId, CancellationToken ct = default);
}

/// <summary>Validates that external SQL is one read-only SELECT statement.</summary>
public interface IExternalSqlPolicy
{
    void DemandSelectOnly(string sql);
}

/// <summary>Ad-hoc PLC operations from the UI (test / browse / write) using a temporary connection.</summary>
public interface IPlcOperationsService
{
    Task<ConnectionTestResult> TestConnectionAsync(Device device, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(Device device, CancellationToken ct = default);
    Task<PlcWriteResult> WriteTagAsync(int tagId, string value, CancellationToken ct = default);
    Task<TagReadResult> ReadTagOnceAsync(int tagId, CancellationToken ct = default);
}

/// <summary>Runtime (DB-stored) settings editable from the UI.</summary>
public interface IAppSettingsService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default) where T : IParsable<T>;
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}

public static class AppSettingKeys
{
    public const string Theme = "ui.theme";
    public const string UseMockPlc = "simulation.useMockPlc";
    public const string SessionTimeoutMinutes = "security.sessionTimeoutMinutes";
    public const string TransferredRetentionDays = "capture.transferredRetentionDays";
    public const string TransferIntervalSeconds = "transfer.intervalSeconds";
}
