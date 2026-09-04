using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Triggers;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Capture;

/// <summary>
/// Polls one device. Owns the driver connection, evaluates the capture mode (Manual / PLC trigger /
/// Logical expression), refreshes external context on the trigger's rising edge, historises scans
/// and keeps live values current. Reconnects with exponential backoff; never dies on transient errors.
/// </summary>
public sealed class DeviceCaptureWorker : IAsyncDisposable
{
    private readonly Device _device;
    private readonly IPlcDriverFactory _driverFactory;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IExternalQueryService _externalQueries;
    private readonly LiveValueCache _cache;
    private readonly CaptureOptions _options;
    private readonly ILogger _logger;
    private readonly Action<LiveTagValue> _publishValue;
    private readonly Action<DeviceCaptureStatus> _publishStatus;
    private readonly Func<bool> _isPaused;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<DeviceTag> _plcTags;
    private readonly List<DeviceTag> _customTags;
    private readonly List<DeviceTag> _externalTags;
    private readonly List<DeviceTriggerCondition> _conditions;
    private readonly Dictionary<string, DeviceTag> _tagsByName;
    private readonly Dictionary<string, string?> _previousValues = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string? Value, TagQuality Quality, DateTime AtUtc)> _dirtyCurrentValues = new();

    private IPlcDriver? _driver;
    private Task? _loop;
    private DateTime _lastCurrentValueFlush = DateTime.MinValue;

    // status fields
    private bool _connected;
    private bool? _lastTrigger;
    private bool _externalLoaded;
    private DateTime? _lastScanUtc;
    private DateTime? _lastGoodReadUtc;
    private long _scanCount;
    private long _readingsWritten;
    private long _errorCount;
    private string? _lastError;
    private int _reconnectAttempts;

    public DeviceCaptureWorker(
        Device device,
        IPlcDriverFactory driverFactory,
        IDbContextFactory<AppDbContext> dbFactory,
        IExternalQueryService externalQueries,
        LiveValueCache cache,
        CaptureOptions options,
        ILogger logger,
        Action<LiveTagValue> publishValue,
        Action<DeviceCaptureStatus> publishStatus,
        Func<bool> isPaused)
    {
        _device = device;
        _driverFactory = driverFactory;
        _dbFactory = dbFactory;
        _externalQueries = externalQueries;
        _cache = cache;
        _options = options;
        _logger = logger;
        _publishValue = publishValue;
        _publishStatus = publishStatus;
        _isPaused = isPaused;
        _externalLoaded = device.ExternalContextLoaded;

        _plcTags = device.Tags.Where(t => t.Kind == TagKind.Plc && t.IsSelected).OrderBy(t => t.Name).ToList();
        _customTags = device.Tags.Where(t => t.Kind == TagKind.Custom && t.IsSelected).ToList();
        _externalTags = device.Tags.Where(t => t.Kind == TagKind.External && t.IsSelected).ToList();
        _conditions = device.TriggerConditions.OrderBy(c => c.Order).ToList();
        _tagsByName = device.Tags.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var t in device.Tags)
            _previousValues[t.Name] = t.CurrentValue;
    }

    public int DeviceId => _device.Id;
    public string DeviceName => _device.Name;

    public DeviceCaptureStatus Status => new(
        _device.Id, _device.Name, _loop is { IsCompleted: false }, _connected,
        _device.IsCaptureEnabled, _device.IsMonitorEnabled, _lastTrigger,
        _lastScanUtc, _lastGoodReadUtc, _scanCount, _readingsWritten, _errorCount, _lastError, _reconnectAttempts);

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { } catch (Exception ex) { _logger.LogError(ex, "{Device}: worker loop faulted during stop.", _device.Name); }
        }
        await FlushCurrentValuesAsync(force: true, CancellationToken.None);
        if (_driver is not null) await _driver.DisposeAsync();
        _driver = null;
        _connected = false;
        _publishStatus(Status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    // ------------------------------------------------------------------ main loop

    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("{Device}: capture worker started (mode={Mode}, poll={Poll} ms, plcTags={Plc}, custom={Custom}, external={Ext}).",
            _device.Name, _device.CaptureMode, _device.PollIntervalMs, _plcTags.Count, _customTags.Count, _externalTags.Count);
        _publishStatus(Status);

        var period = TimeSpan.FromMilliseconds(Math.Max(_options.MinPollIntervalMs, _device.PollIntervalMs));
        using var timer = new PeriodicTimer(period);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_isPaused())
                {
                    await Task.Delay(500, ct);
                    continue;
                }

                if (!_connected)
                {
                    if (!await TryConnectAsync(ct)) continue;
                }

                await ScanOnceAsync(ct);
                await FlushCurrentValuesAsync(force: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (PlcConnectionException ex)
            {
                await HandleDisconnectAsync(ex.Message, ct);
                continue; // reconnect immediately (with backoff inside TryConnect)
            }
            catch (Exception ex)
            {
                _errorCount++;
                _lastError = ex.Message;
                _logger.LogError(ex, "{Device}: unexpected error during scan.", _device.Name);
                _publishStatus(Status);
            }

            try { await timer.WaitForNextTickAsync(ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("{Device}: capture worker stopped.", _device.Name);
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            _driver ??= _driverFactory.Create(_device);
            await _driver.ConnectAsync(ct);
            _connected = true;
            _reconnectAttempts = 0;
            _lastError = null;
            _logger.LogInformation("{Device}: connected to {Host}:{Port}.", _device.Name, _device.Host, _device.Port);
            _publishStatus(Status);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PlcConfigurationException ex)
        {
            // Not recoverable by retrying: surface loudly and idle.
            _lastError = ex.Message;
            _errorCount++;
            MarkAllTags(TagQuality.ConfigError);
            _publishStatus(Status);
            _logger.LogError("{Device}: {Message}", _device.Name, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return false;
        }
        catch (Exception ex)
        {
            _reconnectAttempts++;
            _errorCount++;
            _lastError = ex.Message;
            MarkAllTags(TagQuality.NotConnected);
            var delay = Backoff(_reconnectAttempts);
            if (_reconnectAttempts is 1 or 5 or 20 || _reconnectAttempts % 100 == 0)
                _logger.LogWarning("{Device}: connect attempt {Attempt} failed: {Message}. Next retry in {Delay}s.", _device.Name, _reconnectAttempts, ex.Message, delay.TotalSeconds);
            _publishStatus(Status);
            await Task.Delay(delay, ct);
            return false;
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(_options.ReconnectMaxDelaySeconds, _options.ReconnectInitialDelaySeconds * Math.Pow(2, Math.Min(attempt - 1, 10)));
        var jitter = Random.Shared.NextDouble() * 0.2 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private async Task HandleDisconnectAsync(string reason, CancellationToken ct)
    {
        _connected = false;
        _errorCount++;
        _lastError = reason;
        _logger.LogWarning("{Device}: connection lost — {Reason}", _device.Name, reason);
        MarkAllTags(TagQuality.NotConnected);
        if (_driver is not null)
        {
            try { await _driver.DisconnectAsync(); } catch { /* ignore */ }
        }
        _publishStatus(Status);
        await Task.Delay(Backoff(Math.Max(1, _reconnectAttempts)), ct);
    }

    // ------------------------------------------------------------------ one scan

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var scanUtc = DateTime.UtcNow;
        var scanId = Guid.NewGuid();

        // Read set = selected PLC tags ∪ trigger tag ∪ PLC tags referenced by logical conditions.
        var readSet = new Dictionary<int, DeviceTag>(_plcTags.Count + 4);
        foreach (var t in _plcTags) readSet[t.Id] = t;
        if (_device.CaptureMode == CaptureMode.PlcTrigger && _device.TriggerTagName is not null && _tagsByName.TryGetValue(_device.TriggerTagName, out var trig) && trig.Kind == TagKind.Plc)
            readSet[trig.Id] = trig;
        if (_device.CaptureMode == CaptureMode.Logical)
            foreach (var c in _conditions)
                if (_tagsByName.TryGetValue(c.TagName, out var ct1) && ct1.Kind == TagKind.Plc) readSet[ct1.Id] = ct1;

        var toRead = readSet.Values.ToList();
        IReadOnlyList<TagReadResult> results = toRead.Count == 0
            ? Array.Empty<TagReadResult>()
            : await _driver!.ReadAsync(toRead, ct);

        _scanCount++;
        _lastScanUtc = scanUtc;

        // Current values (this scan) keyed by tag name, formatted.
        var current = new Dictionary<string, (string? Value, TagQuality Quality)>(StringComparer.Ordinal);
        var goodCount = 0;
        foreach (var r in results)
        {
            var tag = readSet[r.TagId];
            string? formatted = r.IsGood ? TagValueFormatter.Format(tag, r.RawValue) : null;
            if (r.IsGood) goodCount++;
            else if (r.Quality == TagQuality.ConfigError && _lastError != r.Error) _logger.LogWarning("{Device}/{Tag}: {Error}", _device.Name, tag.Name, r.Error);
            current[tag.Name] = (formatted, r.Quality);
            PublishLive(tag, formatted, r.Quality, scanUtc);
        }
        if (goodCount > 0) _lastGoodReadUtc = scanUtc;

        // Non-PLC tags contribute their configured/refreshed values (published to the live cache only when they change).
        foreach (var t in _customTags.Concat(_externalTags))
        {
            var q = t.SetValue is null ? TagQuality.Uncertain : TagQuality.Good;
            current[t.Name] = (t.SetValue, q);
            var cached = _cache.Get(_device.Id, t.Id);
            if (cached is null || cached.Value != t.SetValue || cached.Quality != q)
                PublishLive(t, t.SetValue, q, scanUtc);
        }

        // ---- trigger evaluation ----
        var triggered = EvaluateTrigger(current);

        if (triggered && !_externalLoaded)
        {
            await RefreshExternalContextAsync(ct);
            foreach (var t in _externalTags) current[t.Name] = (t.SetValue, TagQuality.Good);
        }
        else if (!triggered && _externalLoaded)
        {
            await SetExternalLatchAsync(false, ct);
        }

        if (_lastTrigger != triggered)
            _logger.LogInformation("{Device}: trigger → {State} ({Mode}).", _device.Name, triggered ? "ON" : "OFF", _device.CaptureMode);
        _lastTrigger = triggered;

        // ---- historise ----
        if (_device.IsCaptureEnabled && triggered)
        {
            var written = await WriteReadingsAsync(scanUtc, scanId, current, ct);
            _readingsWritten += written;
        }

        // Track previous values for OnChange.
        foreach (var kv in current)
            if (kv.Value.Quality == TagQuality.Good) _previousValues[kv.Key] = kv.Value.Value;

        if (sw.ElapsedMilliseconds > _device.PollIntervalMs)
            _logger.LogDebug("{Device}: scan took {Ms} ms which exceeds the {Poll} ms poll interval.", _device.Name, sw.ElapsedMilliseconds, _device.PollIntervalMs);

        if (_scanCount % 20 == 1) _publishStatus(Status);
    }

    private bool EvaluateTrigger(Dictionary<string, (string? Value, TagQuality Quality)> current)
    {
        switch (_device.CaptureMode)
        {
            case CaptureMode.Manual:
                return true;

            case CaptureMode.PlcTrigger:
                if (_device.TriggerTagName is null || !current.TryGetValue(_device.TriggerTagName, out var tv)) return false;
                return tv.Quality == TagQuality.Good && TriggerEvaluator.IsTrue(tv.Value);

            case CaptureMode.Logical:
                {
                    if (_conditions.Count == 0) return false;
                    var snapshot = new Dictionary<string, TriggerEvaluator.TagSnapshot>(StringComparer.Ordinal);
                    foreach (var c in _conditions)
                    {
                        if (!current.TryGetValue(c.TagName, out var v) || v.Quality != TagQuality.Good) continue;
                        _previousValues.TryGetValue(c.TagName, out var prev);
                        snapshot[c.TagName] = new TriggerEvaluator.TagSnapshot(v.Value, prev);
                    }
                    return TriggerEvaluator.Evaluate(_conditions, snapshot);
                }
            default:
                return false;
        }
    }

    private async Task RefreshExternalContextAsync(CancellationToken ct)
    {
        if (_externalTags.Count > 0)
        {
            var ok = await _externalQueries.RefreshExternalTagValuesAsync(_device.Id, ct);
            if (!ok) _logger.LogWarning("{Device}: one or more external queries failed; external tags keep their previous values.", _device.Name);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var ids = _externalTags.Select(t => t.Id).ToList();
            var fresh = await db.DeviceTags.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.SetValue }).ToListAsync(ct);
            foreach (var f in fresh)
            {
                var tag = _externalTags.First(t => t.Id == f.Id);
                tag.SetValue = f.SetValue;
                PublishLive(tag, f.SetValue, TagQuality.Good, DateTime.UtcNow);
            }
        }
        await SetExternalLatchAsync(true, ct);
    }

    private async Task SetExternalLatchAsync(bool loaded, CancellationToken ct)
    {
        _externalLoaded = loaded;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Devices.Where(d => d.Id == _device.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExternalContextLoaded, loaded), ct);
    }

    private async Task<int> WriteReadingsAsync(DateTime scanUtc, Guid scanId, Dictionary<string, (string? Value, TagQuality Quality)> current, CancellationToken ct)
    {
        var local = scanUtc.ToLocalTime();
        var rows = new List<TagReading>(_plcTags.Count + _customTags.Count + _externalTags.Count);

        foreach (var t in _plcTags)
        {
            if (!current.TryGetValue(t.Name, out var v) || v.Quality != TagQuality.Good || v.Value is null) continue;
            rows.Add(NewReading(t, v.Value, v.Quality, local, scanUtc, scanId));
        }

        // Legacy behaviour: context values are stamped into every scan alongside PLC values.
        if (rows.Count > 0)
        {
            foreach (var t in _customTags.Concat(_externalTags))
                rows.Add(NewReading(t, t.SetValue ?? string.Empty, TagQuality.Good, local, scanUtc, scanId));
        }

        if (rows.Count == 0) return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.TagReadings.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private TagReading NewReading(DeviceTag t, string value, TagQuality q, DateTime local, DateTime utc, Guid scanId) => new()
    {
        DeviceId = _device.Id,
        DeviceName = _device.Name,
        TagName = t.Name,
        Value = value.Length > 500 ? value[..500] : value,
        Quality = q,
        Timestamp = local,
        TimestampUtc = utc,
        ScanId = scanId,
        IsTransferred = false,
    };

    // ------------------------------------------------------------------ live values

    private void PublishLive(DeviceTag tag, string? value, TagQuality quality, DateTime atUtc)
    {
        var live = new LiveTagValue(_device.Id, tag.Id, tag.Name, value, quality, atUtc);
        _cache.Set(live);
        _publishValue(live);
        lock (_dirtyCurrentValues) _dirtyCurrentValues[tag.Id] = (value, quality, atUtc);
    }

    private void MarkAllTags(TagQuality quality)
    {
        var now = DateTime.UtcNow;
        foreach (var t in _plcTags)
        {
            var prev = _cache.Get(_device.Id, t.Id);
            PublishLive(t, prev?.Value, quality, now);
        }
    }

    private async Task FlushCurrentValuesAsync(bool force, CancellationToken ct)
    {
        if (!force && (DateTime.UtcNow - _lastCurrentValueFlush).TotalSeconds < _options.CurrentValueFlushSeconds) return;

        List<KeyValuePair<int, (string? Value, TagQuality Quality, DateTime AtUtc)>> snapshot;
        lock (_dirtyCurrentValues)
        {
            if (_dirtyCurrentValues.Count == 0) { _lastCurrentValueFlush = DateTime.UtcNow; return; }
            snapshot = _dirtyCurrentValues.ToList();
            _dirtyCurrentValues.Clear();
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var kv in snapshot)
            {
                var id = kv.Key; var (value, quality, at) = kv.Value;
                await db.DeviceTags.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.CurrentValue, value)
                    .SetProperty(t => t.CurrentQuality, quality)
                    .SetProperty(t => t.CurrentValueAtUtc, at), ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Device}: failed to persist current values ({Count} tags).", _device.Name, snapshot.Count);
        }
        _lastCurrentValueFlush = DateTime.UtcNow;
    }
}
