using WTT.Domain.Enums;

namespace WTT.Application.Interfaces;

/// <summary>
/// A single trend data point (timestamp, value, and quality for one graph point).
/// </summary>
public record HistorianDataPoint(
    DateTime TimestampUtc,
    double Value,
    TagQuality Quality
);

/// <summary>
/// Complete details of one tag's time-series line (pen).
/// </summary>
public record TagTrendSeries(
    string TagName,
    string? EngineeringUnit,
    double? MinAlarm,
    double? MaxAlarm,
    IReadOnlyList<HistorianDataPoint> Points,
    WTT.Domain.Enums.TagDataType DataType = WTT.Domain.Enums.TagDataType.Float32
)
{
    /// <summary>Digital (Bool) tags render as step charts, analog tags as diagonal lines.</summary>
    public bool IsDigital => DataType == WTT.Domain.Enums.TagDataType.Bool;
}

/// <summary>
/// Industrial SCADA historian service interface:
/// Fetches live and historical process data from the TagReadings table.
/// </summary>
public interface IHistorianService
{
    /// <summary>
    /// Retrieves historical time-series data for a device's selected tags.
    /// </summary>
    /// <param name="deviceId">PLC machine ID</param>
    /// <param name="tagNames">Names of the tags to plot</param>
    /// <param name="fromUtc">Start time of the data range</param>
    /// <param name="toUtc">End time of the data range</param>
    /// <param name="maxPoints">Maximum points to render on screen</param>
    Task<IReadOnlyList<TagTrendSeries>> GetTrendsAsync(
        int deviceId,
        IEnumerable<string> tagNames,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints = 500,
        CancellationToken ct = default);
}
