using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Domain.Interfaces;
using WTT.Domain.ValueObjects;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

/// <summary>Ad-hoc controller operations from the UI over a short-lived connection.</summary>
public sealed class PlcOperationsService : IPlcOperationsService
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _writeLocks = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastWrites = new();
    private readonly IPlcDriverFactory _factory;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly ILogger<PlcOperationsService> _logger;

    public PlcOperationsService(IPlcDriverFactory factory, IDbContextFactory<AppDbContext> dbFactory, ICurrentUserService currentUser, IAuditService audit, ILogger<PlcOperationsService> logger)
    {
        _factory = factory;
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(Device device, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var driver = _factory.Create(device);
            await driver.ConnectAsync(ct);

            // If the device already has a selected PLC tag, prove the protocol works by reading one.
            var probe = device.Tags.FirstOrDefault(t => t.Kind == TagKind.Plc && t.IsSelected && !string.IsNullOrWhiteSpace(t.PlcAddress));
            if (probe is null && device.Id != 0)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                probe = await db.DeviceTags.AsNoTracking().Include(t => t.Address).FirstOrDefaultAsync(t => t.DeviceId == device.Id && t.Kind == TagKind.Plc && t.IsSelected, ct);
            }
            if (probe is not null)
            {
                var r = (await driver.ReadAsync([probe], ct))[0];
                return r.IsGood
                    ? ConnectionTestResult.Ok($"Connected. Read '{probe.Name}' = {r.RawValue}.", sw.Elapsed)
                    : ConnectionTestResult.Ok($"Connected, but reading '{probe.Name}' failed: {r.Error}", sw.Elapsed);
            }
            return ConnectionTestResult.Ok("Connected successfully.", sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Connection test for {Device} failed: {Message}", device.Name, ex.Message);
            return ConnectionTestResult.Fail(ex.Message, sw.Elapsed);
        }
    }

    public async Task<IReadOnlyList<DiscoveredTag>> BrowseTagsAsync(Device device, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        await using var driver = _factory.Create(device);
        if (!driver.Capabilities.CanBrowseTags)
            throw new BusinessRuleException($"{device.Model} does not support tag browsing. Add tags manually.");
        await driver.ConnectAsync(ct);
        return await driver.BrowseTagsAsync(ct);
    }

    public async Task<PlcWriteResult> WriteTagAsync(int tagId, string value, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.WriteToPlc);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tag = await db.DeviceTags.AsNoTracking().Include(t => t.Device).Include(t => t.Address).FirstOrDefaultAsync(t => t.Id == tagId, ct)
                  ?? throw new NotFoundException("Tag", tagId);
        if (tag.Kind != TagKind.Plc) throw new BusinessRuleException("Only PLC tags can be written to the controller.");
        if (!tag.IsWriteEnabled) throw new AuthorizationException($"Writing is not enabled for tag '{tag.Name}'.");
        if (tag.ElementCount != 1) throw new BusinessRuleException("Array writes are not supported by the safe write workflow.");

        object parsed;
        try { parsed = TagValueFormatter.ParseForWrite(tag.DataType, value); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new BusinessRuleException($"Value is invalid for {tag.DataType}."); }
        if (parsed is string text && text.Length > Math.Max(1, tag.ElementCount)) throw new BusinessRuleException($"Value exceeds the configured string length of {tag.ElementCount}.");
        if (TagValueFormatter.TryToDouble(parsed, out var numeric) &&
            (tag.WriteMinimum.HasValue && numeric < tag.WriteMinimum.Value || tag.WriteMaximum.HasValue && numeric > tag.WriteMaximum.Value))
            throw new BusinessRuleException($"Value must be between {tag.WriteMinimum?.ToString() ?? "unbounded"} and {tag.WriteMaximum?.ToString() ?? "unbounded"}.");

        var gate = _writeLocks.GetOrAdd(tagId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            if (_lastWrites.TryGetValue(tagId, out var last) && (DateTime.UtcNow - last).TotalMilliseconds < tag.MinimumWriteIntervalMs)
                throw new BusinessRuleException($"Wait at least {tag.MinimumWriteIntervalMs} ms between writes to this tag.");

            await using var driver = _factory.Create(tag.Device!);
            await driver.ConnectAsync(ct);
            var before = (await driver.ReadAsync([tag], ct))[0];
            await driver.WriteAsync(new TagWriteRequest(tag, value), ct);
            var after = (await driver.ReadAsync([tag], ct))[0];
            if (!after.IsGood) throw new PlcWriteException($"Write completed but readback failed: {after.Error}");

            var requested = TagValueFormatter.Format(tag, parsed);
            var readback = TagValueFormatter.Format(tag, after.RawValue);
            if (!ValuesMatch(tag.DataType, requested, readback)) throw new PlcWriteException($"Write verification failed. Requested '{requested}', controller returned '{readback}'.");

            _lastWrites[tagId] = DateTime.UtcNow;
            var previous = before.IsGood ? TagValueFormatter.Format(tag, before.RawValue) : null;
            await _audit.RecordAsync("WriteToPlc", $"{tag.Device!.Name}/{tag.Name}", $"Old={previous}; Requested={requested}; Readback={readback}", true, ct);
            return new PlcWriteResult(requested, previous, readback, _lastWrites[tagId]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync("WriteToPlc", $"{tag.Device!.Name}/{tag.Name}", $"{value} → {ex.Message}", false, ct);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<TagReadResult> ReadTagOnceAsync(int tagId, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ViewLiveData);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tag = await db.DeviceTags.AsNoTracking().Include(t => t.Device).Include(t => t.Address).FirstOrDefaultAsync(t => t.Id == tagId, ct)
                  ?? throw new NotFoundException("Tag", tagId);
        await using var driver = _factory.Create(tag.Device!);
        await driver.ConnectAsync(ct);
        return (await driver.ReadAsync([tag], ct))[0];
    }

    private static bool ValuesMatch(TagDataType type, string expected, string actual)
    {
        if (type is not (TagDataType.Float32 or TagDataType.Float64)) return string.Equals(expected, actual, StringComparison.Ordinal);
        if (!double.TryParse(expected, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var e) ||
            !double.TryParse(actual, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)) return false;
        return Math.Abs(e - a) <= Math.Max(1e-6, Math.Abs(e) * 1e-5);
    }
}
