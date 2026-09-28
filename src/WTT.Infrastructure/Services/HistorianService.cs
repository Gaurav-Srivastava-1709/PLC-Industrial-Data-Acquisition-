using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

/// <summary>
/// Industrial SCADA Historian Service:
/// Runs fast, indexed queries against the TagReadings table to retrieve tag time-series data
/// and generates chart-ready series points for the UI.
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
        if (fromUtc > toUtc)
            throw new ArgumentOutOfRangeException(nameof(fromUtc), "Trend start time must not be after end time.");
        if (maxPoints < 10)
            throw new ArgumentOutOfRangeException(nameof(maxPoints), "maxPoints must be at least 10.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. Load tag metadata (Engineering Units, Alarm Limits, DataType)
        Dictionary<string, (string? Unit, double? Min, double? Max, TagDataType DataType)> tagMeta;
        try
        {
            tagMeta = await db.DeviceTags
                .AsNoTracking()
                .Where(t => t.DeviceId == deviceId && tagList.Contains(t.Name))
                .Select(t => new { t.Name, t.EngineeringUnit, t.AlarmMinThreshold, t.AlarmMaxThreshold, t.DataType })
                .ToDictionaryAsync(
                    t => t.Name,
                    t => ((string?)t.EngineeringUnit, t.AlarmMinThreshold, t.AlarmMaxThreshold, t.DataType),
                    StringComparer.OrdinalIgnoreCase, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historian tag metadata load failed for device {DeviceId}.", deviceId);
            throw new HistorianException("Could not load trend tag information. Check database connectivity.", ex);
        }

        // 2. Fetch readings with keyset pagination (bounded memory).
        // Never ToList the whole window at once — page by page, so even days of
        // data cannot exhaust RAM. Each page is a fast indexed range-scan over
        // (DeviceId, TagName, TimestampUtc). Requires migration AddHistorianTrendIndex.
        const int PageSize = 5000;
        const int MaxParsedTotal = 100_000;
        var perTag = tagList.ToDictionary(t => t, _ => new List<HistorianDataPoint>(), StringComparer.OrdinalIgnoreCase);
        try
        {
            var totalCount = await db.TagReadings
                .AsNoTracking()
                .Where(r => r.DeviceId == deviceId
                         && tagList.Contains(r.TagName)
                         && r.TimestampUtc >= fromUtc
                         && r.TimestampUtc <= toUtc)
                .CountAsync(ct);
            // Global stride keeps total parsed rows under cap, evenly across the window.
            var stride = Math.Max(1, totalCount / MaxParsedTotal);
            if (stride > 1)
                _logger.LogInformation("Historian stride {Stride}: {Total} rows in window for device {DeviceId}.",
                    stride, totalCount, deviceId);

            long rowIndex = 0;
            var cursorTs = fromUtc;
            long cursorId = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await db.TagReadings
                    .AsNoTracking()
                    .Where(r => r.DeviceId == deviceId
                             && tagList.Contains(r.TagName)
                             && r.TimestampUtc >= fromUtc
                             && r.TimestampUtc <= toUtc
                             && (r.TimestampUtc > cursorTs || (r.TimestampUtc == cursorTs && r.Id > cursorId)))
                    .OrderBy(r => r.TimestampUtc)
                    .ThenBy(r => r.Id)
                    .Select(r => new { r.Id, r.TagName, r.Value, r.Quality, r.TimestampUtc })
                    .Take(PageSize)
                    .ToListAsync(ct);
                if (page.Count == 0) break;

                foreach (var r in page)
                {
                    cursorTs = r.TimestampUtc;
                    cursorId = r.Id;
                    if ((rowIndex++ % stride) != 0) continue;
                    if (!perTag.TryGetValue(r.TagName, out var list)) continue;
                    // Parse numbers (Invariant culture ensures the decimal dot '.' is handled correctly)
                    // Bool "1"/"0" also parses to double (for step charts).
                    if (double.TryParse(r.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue))
                        list.Add(new HistorianDataPoint(r.TimestampUtc, parsedValue, r.Quality));
                    else if (bool.TryParse(r.Value, out var b))
                        list.Add(new HistorianDataPoint(r.TimestampUtc, b ? 1.0 : 0.0, r.Quality));
                    // Non-numeric strings (LineName/Batch) are skipped — they have no line.
                }

                if (page.Count < PageSize) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (HistorianException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historian readings fetch failed for device {DeviceId} window {From:u}-{To:u}.",
                deviceId, fromUtc, toUtc);
            throw new HistorianException("Could not load trend history. Check database connectivity and retry.", ex);
        }

        var result = new List<TagTrendSeries>();

        // 3. Per tag: split good-segments by quality gaps, then normalized LTTB per segment.
        // Bad/NotConnected points are NOT dropped — one marker per gap-run is kept so the
        // chart breaks the line and shows the red X (industry-standard data integrity).
        foreach (var tagName in tagList)
        {
            tagMeta.TryGetValue(tagName, out var meta);
            var parsed = perTag[tagName];

            var finalPoints = DownsampleWithGaps(parsed, maxPoints);

            result.Add(new TagTrendSeries(
                TagName: tagName,
                EngineeringUnit: meta.Unit,
                MinAlarm: meta.Min,
                MaxAlarm: meta.Max,
                Points: finalPoints,
                DataType: meta.DataType == 0 ? TagDataType.Float32 : meta.DataType
            ));
        }

        return result;
    }

    private static bool IsGapPoint(HistorianDataPoint p) =>
        p.Quality is TagQuality.Bad or TagQuality.NotConnected or TagQuality.ConfigError;

    /// <summary>
    /// Gap-aware downsampling: good runs get normalized-LTTB with proportional budget,
    /// each gap-run contributes exactly one marker (its first point).
    /// </summary>
    private static IReadOnlyList<HistorianDataPoint> DownsampleWithGaps(List<HistorianDataPoint> source, int targetCount)
    {
        if (source.Count == 0) return source;
        if (source.Count <= targetCount) return source;

        // Split into good runs + one marker per gap-run (time order preserved).
        var goodRuns = new List<List<HistorianDataPoint>>();
        var gapMarkers = new List<HistorianDataPoint>();
        List<HistorianDataPoint>? current = null;
        var lastGapTs = DateTime.MinValue;
        foreach (var p in source)
        {
            if (IsGapPoint(p))
            {
                if (current is { Count: > 0 }) { goodRuns.Add(current); current = null; }
                if (p.TimestampUtc != lastGapTs)
                {
                    gapMarkers.Add(p);
                    lastGapTs = p.TimestampUtc;
                }
            }
            else
            {
                current ??= new List<HistorianDataPoint>();
                current.Add(p);
            }
        }
        if (current is { Count: > 0 }) goodRuns.Add(current);
        if (goodRuns.Count == 0) return gapMarkers.Take(targetCount).ToList();

        // Proportional point budget per good run (rounding slack is at most a few points).
        var totalGood = goodRuns.Sum(r => r.Count);
        var goodBudget = Math.Max(0, targetCount - gapMarkers.Count);
        var sampled = new List<HistorianDataPoint>(targetCount);
        foreach (var run in goodRuns)
        {
            var budget = Math.Max(2, (int)((long)run.Count * goodBudget / Math.Max(1, totalGood)));
            sampled.AddRange(DownsampleLttbNormalized(run, budget));
        }

        // Merge markers back in time order; trim good points first if over budget.
        sampled.AddRange(gapMarkers);
        sampled.Sort(static (a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        if (sampled.Count > targetCount)
        {
            var keepGaps = Math.Min(gapMarkers.Count, targetCount / 4);
            var goods = sampled.Where(static p => !IsGapPoint(p)).ToList();
            var trimmed = DownsampleLttbNormalized(goods, Math.Max(2, targetCount - keepGaps));
            sampled = trimmed.Concat(gapMarkers.Take(keepGaps)).ToList();
            sampled.Sort(static (a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        }
        return sampled;
    }

    /// <summary>
    /// Normalized LTTB downsampling: preserves visual shape (peaks/valleys).
    /// X (time) and Y (value) are both scaled to 0-1 first — otherwise raw Ticks
    /// (~1e17) drown out the value axis and peaks get picked almost at random.
    /// Input must be a gap-free run (use <see cref="DownsampleWithGaps"/> for mixed data).
    /// </summary>
    private static IReadOnlyList<HistorianDataPoint> DownsampleLttbNormalized(List<HistorianDataPoint> source, int targetCount)
    {
        if (source.Count <= targetCount) return source;
        if (targetCount < 3) return DownsampleEven(source, targetCount);

        double xMin = source[0].TimestampUtc.Ticks;
        double xMax = source[^1].TimestampUtc.Ticks;
        double xRange = Math.Max(1.0, xMax - xMin);
        double yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var p in source)
        {
            if (p.Value < yMin) yMin = p.Value;
            if (p.Value > yMax) yMax = p.Value;
        }
        double yRange = Math.Max(1e-9, yMax - yMin);
        static double Nx(HistorianDataPoint p, double xMin, double xRange) => (p.TimestampUtc.Ticks - xMin) / xRange;
        static double Ny(HistorianDataPoint p, double yMin, double yRange) => (p.Value - yMin) / yRange;

        var sampled = new List<HistorianDataPoint>(targetCount) { source[0] };
        double bucketSize = (double)(source.Count - 2) / (targetCount - 2);
        int a = 0;

        for (var i = 0; i < targetCount - 2; i++)
        {
            int rangeStart = (int)Math.Floor((i + 1) * bucketSize) + 1;
            int rangeEnd = Math.Min((int)Math.Floor((i + 2) * bucketSize) + 1, source.Count);
            if (rangeEnd <= rangeStart) rangeEnd = Math.Min(rangeStart + 1, source.Count);

            // Average of the next bucket (third vertex of the triangle), normalized space.
            double avgX = 0, avgY = 0;
            int avgCount = 0;
            for (var j = rangeStart; j < rangeEnd; j++)
            {
                avgX += Nx(source[j], xMin, xRange);
                avgY += Ny(source[j], yMin, yRange);
                avgCount++;
            }
            if (avgCount > 0) { avgX /= avgCount; avgY /= avgCount; }
            else { avgX = Nx(source[rangeStart], xMin, xRange); avgY = Ny(source[rangeStart], yMin, yRange); }

            // Select the point with the largest triangle area in the current bucket.
            int bucketStart = (int)Math.Floor(i * bucketSize) + 1;
            int bucketEnd = Math.Min((int)Math.Floor((i + 1) * bucketSize) + 1, source.Count);
            double maxArea = -1;
            int nextA = bucketStart;
            double ax = Nx(source[a], xMin, xRange), ay = Ny(source[a], yMin, yRange);
            for (var j = bucketStart; j < bucketEnd; j++)
            {
                double area = Math.Abs((ax - avgX) * (Ny(source[j], yMin, yRange) - ay)
                    - (ax - Nx(source[j], xMin, xRange)) * (avgY - ay)) * 0.5;
                if (area > maxArea) { maxArea = area; nextA = j; }
            }
            sampled.Add(source[nextA]);
            a = nextA;
        }
        sampled.Add(source[^1]);
        return sampled;
    }

    /// <summary>
    /// Takes evenly spaced samples when up to 10,000 points arrive
    /// so the graph renders smoothly at 60 FPS without freezing the CPU.
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
