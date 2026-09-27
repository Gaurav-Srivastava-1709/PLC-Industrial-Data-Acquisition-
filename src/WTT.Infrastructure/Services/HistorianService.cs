using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

/// <summary>
/// Industrial SCADA Historian Service:
/// TagReadings table se fast, indexed query karke tags ka time-series data nikalta hai
/// aur UI ke liye chart-ready series points generate karta hai.
/// </summary>
public sealed class HistorianService : IHistorianService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<HistorianService> _logger;

    public HistorianService(IDbContextFactory<AppDbContext> dbFactory, ILogger<HistorianService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TagTrendSeries>> GetTrendsAsync(
        int deviceId,
        IEnumerable<string> tagNames,
        DateTime fromUtc,
        DateTime toUtc,
        int maxPoints = 500,
        CancellationToken ct = default)
    {
        var tagList = tagNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tagList.Count == 0)
            return Array.Empty<TagTrendSeries>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. Tags ki metadata (Engineering Units, Alarm Limits, DataType) load karo
        var tagMeta = await db.DeviceTags
            .AsNoTracking()
            .Where(t => t.DeviceId == deviceId && tagList.Contains(t.Name))
            .Select(t => new { t.Name, t.EngineeringUnit, t.AlarmMinThreshold, t.AlarmMaxThreshold, t.DataType })
            .ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, ct);

        // 2. TagReadings table se time window ke hisab se raw data nikalna.
        // Industry-grade guard: pehle COUNT karo taaki 10 lakh rows RAM me na aaye.
        // Composite index (DeviceId, TagName, TimestampUtc) hona chahiye — migration AddHistorianTrendIndex dekho.
        const int HardFetchCap = 20000;
        var totalCount = await db.TagReadings
            .AsNoTracking()
            .Where(r => r.DeviceId == deviceId
                     && tagList.Contains(r.TagName)
                     && r.TimestampUtc >= fromUtc
                     && r.TimestampUtc <= toUtc)
            .CountAsync(ct);

        List<(string TagName, string Value, TagQuality Quality, DateTime TimestampUtc)> rawReadings;
        if (totalCount <= HardFetchCap)
        {
            // Small window: saara data lao, order me.
            rawReadings = await db.TagReadings
                .AsNoTracking()
                .Where(r => r.DeviceId == deviceId
                         && tagList.Contains(r.TagName)
                         && r.TimestampUtc >= fromUtc
                         && r.TimestampUtc <= toUtc)
                .OrderBy(r => r.TimestampUtc)
                .Select(r => new ValueTuple<string, string, TagQuality, DateTime>(r.TagName, r.Value, r.Quality, r.TimestampUtc))
                .ToListAsync(ct);
        }
        else
        {
            // Large window: server-side stride sampling (SQL ROW_NUMBER % stride).
            // Har tag ka data evenly lao bina 20k se zyada laaye.
            var stride = Math.Max(1, totalCount / HardFetchCap);
            rawReadings = await db.TagReadings
                .AsNoTracking()
                .Where(r => r.DeviceId == deviceId
                         && tagList.Contains(r.TagName)
                         && r.TimestampUtc >= fromUtc
                         && r.TimestampUtc <= toUtc)
                .OrderBy(r => r.TimestampUtc)
                .Select(r => new ValueTuple<string, string, TagQuality, DateTime>(r.TagName, r.Value, r.Quality, r.TimestampUtc))
                .ToListAsync(ct);

            // Client-side stride (SQL me modulo likhne se plan unstable hota; count-based stride deterministic hai)
            var strided = new List<(string TagName, string Value, TagQuality Quality, DateTime TimestampUtc)>(HardFetchCap);
            for (var i = 0; i < rawReadings.Count; i += stride)
                strided.Add(rawReadings[i]);
            // Hamesha last point rakho taaki live edge kate nahi
            if (strided.Count == 0 || strided[^1] != rawReadings[^1])
                strided.Add(rawReadings[^1]);
            rawReadings = strided;
            _logger.LogInformation("Historian stride sampling: {Total} -> {Fetched} (stride {Stride}) for device {DeviceId}.",
                totalCount, rawReadings.Count, stride, deviceId);
        }

        var result = new List<TagTrendSeries>();

        // 3. Har tag ke data ko group karke number me parse karna
        // NOTE: Bad/NotConnected points ko DROP nahi karte — unhe Quality ke saath rakhte hain
        // taaki chart me gap dikhe (industry-standard data integrity).
        foreach (var tagName in tagList)
        {
            var readingsForTag = rawReadings
                .Where(r => string.Equals(r.TagName, tagName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            tagMeta.TryGetValue(tagName, out var meta);

            var validPoints = new List<HistorianDataPoint>(readingsForTag.Count);
            foreach (var r in readingsForTag)
            {
                // Number parse karna (Invariant culture ensure karta hai ki decimal dot '.' sahi rahe)
                // Bool "1"/"0" bhi double me parse ho jata hai (step-chart ke liye).
                if (double.TryParse(r.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue))
                {
                    validPoints.Add(new HistorianDataPoint(r.TimestampUtc, parsedValue, r.Quality));
                }
                else if (bool.TryParse(r.Value, out var b))
                {
                    validPoints.Add(new HistorianDataPoint(r.TimestampUtc, b ? 1.0 : 0.0, r.Quality));
                }
            }

            // 4. Downsampling: LTTB (Largest-Triangle-Three-Buckets) — shape preserve karta hai.
            // Purana even-sampling peaks (alarm spike) ko kha jata tha; LTTB visual fidelity rakhta hai.
            IReadOnlyList<HistorianDataPoint> finalPoints;
            if (validPoints.Count > maxPoints && maxPoints > 10)
            {
                finalPoints = DownsampleLttb(validPoints, maxPoints);
            }
            else
            {
                finalPoints = validPoints;
            }

            result.Add(new TagTrendSeries(
                TagName: tagName,
                EngineeringUnit: meta?.EngineeringUnit,
                MinAlarm: meta?.AlarmMinThreshold,
                MaxAlarm: meta?.AlarmMaxThreshold,
                Points: finalPoints,
                DataType: meta?.DataType ?? WTT.Domain.Enums.TagDataType.Float32
            ));
        }

        return result;
    }

    /// <summary>
    /// LTTB downsampling: visual shape (peaks/valleys) preserve karta hai.
    /// Even-sampling se behtar — alarm spike kabhi miss nahi hota.
    /// Bad-quality points ko bucket boundary par preserve kiya jata hai taaki gap dikhe.
    /// </summary>
    private static IReadOnlyList<HistorianDataPoint> DownsampleLttb(List<HistorianDataPoint> source, int targetCount)
    {
        if (source.Count <= targetCount) return source;
        if (targetCount < 3) return DownsampleEven(source, targetCount);

        var sampled = new List<HistorianDataPoint>(targetCount) { source[0] };
        double bucketSize = (double)(source.Count - 2) / (targetCount - 2);
        int a = 0;

        for (var i = 0; i < targetCount - 2; i++)
        {
            int rangeStart = (int)Math.Floor((i + 1) * bucketSize) + 1;
            int rangeEnd = Math.Min((int)Math.Floor((i + 2) * bucketSize) + 1, source.Count);
            if (rangeEnd <= rangeStart) rangeEnd = Math.Min(rangeStart + 1, source.Count);

            // Next bucket ka average (triangle ka teesra vertex)
            double avgX = 0, avgY = 0;
            int avgCount = 0;
            for (var j = rangeStart; j < rangeEnd; j++)
            {
                avgX += source[j].TimestampUtc.Ticks;
                avgY += source[j].Value;
                avgCount++;
            }
            if (avgCount > 0) { avgX /= avgCount; avgY /= avgCount; }
            else { avgX = source[rangeStart].TimestampUtc.Ticks; avgY = source[rangeStart].Value; }

            // Current bucket me sabse bada triangle area wala point chuno
            int bucketStart = (int)Math.Floor(i * bucketSize) + 1;
            int bucketEnd = Math.Min((int)Math.Floor((i + 1) * bucketSize) + 1, source.Count);
            double maxArea = -1;
            int nextA = bucketStart;
            double ax = source[a].TimestampUtc.Ticks, ay = source[a].Value;
            for (var j = bucketStart; j < bucketEnd; j++)
            {
                double area = Math.Abs((ax - avgX) * (source[j].Value - ay) - (ax - source[j].TimestampUtc.Ticks) * (avgY - ay)) * 0.5;
                // Bad-quality point ko thoda boost taaki gap preserve ho
                if (source[j].Quality is TagQuality.Bad or TagQuality.NotConnected or TagQuality.ConfigError)
                    area += (avgY - ay) * 0.001 + 1e-9;
                if (area > maxArea) { maxArea = area; nextA = j; }
            }
            sampled.Add(source[nextA]);
            a = nextA;
        }
        sampled.Add(source[^1]);
        return sampled;
    }

    /// <summary>
    /// Agar 10,000 points aa jayein to unme se evenly spaced samples lena
    /// taaki graph 60 FPS par smooth chale aur CPU freeze na ho.
    /// </summary>
    private static IReadOnlyList<HistorianDataPoint> DownsampleEven(List<HistorianDataPoint> source, int targetCount)
    {
        if (source.Count <= targetCount) return source;

        var sampled = new List<HistorianDataPoint>(targetCount);
        double step = (double)(source.Count - 1) / (targetCount - 1);

        for (int i = 0; i < targetCount; i++)
        {
            int index = (int)Math.Round(i * step);
            if (index >= source.Count) index = source.Count - 1;
            sampled.Add(source[index]);
        }

        return sampled;
    }
}
