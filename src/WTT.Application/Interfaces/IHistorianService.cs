using WTT.Domain.Enums;

namespace WTT.Application.Interfaces;

/// <summary>
/// Ek single trend data point (Graph ke ek dot ki jaankari: samay, value, aur quality).
/// </summary>
public record HistorianDataPoint(
    DateTime TimestampUtc,
    double Value,
    TagQuality Quality
);

/// <summary>
/// Ek poore tag ki time-series line (Pen) ki complete details.
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
    /// <summary>Digital (Bool) tags step-chart se render hote hain, analog diagonal se nahi.</summary>
    public bool IsDigital => DataType == WTT.Domain.Enums.TagDataType.Bool;
}

/// <summary>
/// Industrial SCADA Historian Service Interface:
/// TagReadings table se live aur historical process data fetch karta hai.
/// </summary>
public interface IHistorianService
{
    /// <summary>
    /// Kisi device ke chune hue tags ka historical time-series data nikalna.
    /// </summary>
    /// <param name="deviceId">PLC Machine ki ID</param>
    /// <param name="tagNames">Jin-jin tags ka graph dekhna hai unke naam</param>
    /// <param name="fromUtc">Kab se data dekhna hai (start time)</param>
    /// <param name="toUtc">Kab tak ka data dekhna hai (end time)</param>
    /// <param name="maxPoints">Screen par max kitne points render karne hain</param>
    Task<IReadOnlyList<TagTrendSeries>> GetTrendsAsync(
        int deviceId,
        IEnumerable<string> tagNames,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints = 500,
        CancellationToken ct = default);
}
