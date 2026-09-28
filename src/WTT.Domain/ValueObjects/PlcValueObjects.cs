using WTT.Domain.Entities;
using WTT.Domain.Enums;

namespace WTT.Domain.ValueObjects;

/// <summary>Result of reading one tag from a controller.</summary>
public sealed record TagReadResult(
    int TagId,
    string TagName,
    object? RawValue,
    TagQuality Quality,
    string? Error = null)
{
    public bool IsGood => Quality == TagQuality.Good;

    public static TagReadResult Good(DeviceTag tag, object? value) =>
        new(tag.Id, tag.Name, value, TagQuality.Good);

    public static TagReadResult Bad(DeviceTag tag, string error, TagQuality quality = TagQuality.Bad) =>
        new(tag.Id, tag.Name, null, quality, error);
}

/// <summary>Request to write a value to one tag.</summary>
public sealed record TagWriteRequest(DeviceTag Tag, string Value);
public sealed record PlcWriteResult(string RequestedValue, string? PreviousValue, string ReadbackValue, DateTime CompletedAtUtc);

/// <summary>Tag discovered by browsing a controller's symbol table.</summary>
public sealed record DiscoveredTag(
    string Name,
    TagDataType? DataType,
    int ElementCount,
    int ByteCount,
    bool IsStructure,
    string? TypeName = null);

/// <summary>What a driver can do; used by the UI to enable/disable features.</summary>
public sealed record PlcDriverCapabilities(
    bool CanBrowseTags,
    bool CanWrite,
    bool SupportsBatchRead,
    bool RequiresAddressBlocks);

/// <summary>Live health of one device's capture worker.</summary>
public sealed record DeviceCaptureStatus(
    int DeviceId,
    string DeviceName,
    bool IsRunning,
    bool IsConnected,
    bool IsCapturing,
    bool IsMonitoring,
    bool? LastTriggerState,
    DateTime? LastScanUtc,
    DateTime? LastSuccessfulReadUtc,
    long ScanCount,
    long ReadingsWritten,
    long ErrorCount,
    string? LastError,
    int ReconnectAttempts)
{
    public static DeviceCaptureStatus Stopped(int deviceId, string deviceName) =>
        new(deviceId, deviceName, false, false, false, false, null, null, null, 0, 0, 0, null, 0);
}

public sealed record CaptureRuntimeSnapshot(
    bool StatusAvailable,
    bool IsServiceRunning,
    Guid? ServiceInstanceId,
    DateTime? ServiceStartedAtUtc,
    DateTime? LastHeartbeatUtc,
    string? ServiceError,
    IReadOnlyList<DeviceCaptureStatus> Devices);

/// <summary>A live tag value published by the capture pipeline.</summary>
public sealed record LiveTagValue(
    int DeviceId,
    int TagId,
    string TagName,
    string? Value,
    TagQuality Quality,
    DateTime TimestampUtc);

/// <summary>Message that AlarmService will publish and UI will subscribe to</summary>
public sealed record AlarmTriggeredMessage(
    long AlarmEventId,
    string DeviceName,
    string TagName,
    AlarmType AlarmType,
    AlarmPriority Priority,
    double TriggerValue,
    double ThresholdValue,
    string? EngineeringUnit,
    DateTime TriggeredAtUtc);

/// <summary>Message jab alarm clear ho jaaye</summary>
public sealed record AlarmClearedMessage(
    long AlarmEventId,
    string DeviceName,
    string TagName,
    DateTime ClearedAtUtc);

