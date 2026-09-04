using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Security;
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

    public async Task WriteTagAsync(int tagId, string value, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.WriteToPlc);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tag = await db.DeviceTags.AsNoTracking().Include(t => t.Device).Include(t => t.Address).FirstOrDefaultAsync(t => t.Id == tagId, ct)
                  ?? throw new NotFoundException("Tag", tagId);
        if (tag.Kind != TagKind.Plc) throw new BusinessRuleException("Only PLC tags can be written to the controller.");

        try
        {
            await using var driver = _factory.Create(tag.Device!);
            await driver.ConnectAsync(ct);
            await driver.WriteAsync(new TagWriteRequest(tag, value), ct);
            await _audit.RecordAsync("WriteToPlc", $"{tag.Device!.Name}/{tag.Name}", value, true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync("WriteToPlc", $"{tag.Device!.Name}/{tag.Name}", $"{value} → {ex.Message}", false, ct);
            throw;
        }
    }

    public async Task<TagReadResult> ReadTagOnceAsync(int tagId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tag = await db.DeviceTags.AsNoTracking().Include(t => t.Device).Include(t => t.Address).FirstOrDefaultAsync(t => t.Id == tagId, ct)
                  ?? throw new NotFoundException("Tag", tagId);
        await using var driver = _factory.Create(tag.Device!);
        await driver.ConnectAsync(ct);
        return (await driver.ReadAsync([tag], ct))[0];
    }
}
