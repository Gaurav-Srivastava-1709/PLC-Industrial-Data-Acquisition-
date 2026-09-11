using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Application.Validators;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Domain.ValueObjects;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class TagService : ITagService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly IDataCaptureController _capture;
    private readonly ILogger<TagService> _logger;

    public TagService(IDbContextFactory<AppDbContext> factory, ICurrentUserService currentUser, IAuditService audit, IDataCaptureController capture, ILogger<TagService> logger)
    {
        _factory = factory;
        _currentUser = currentUser;
        _audit = audit;
        _capture = capture;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeviceTag>> GetForDeviceAsync(int deviceId, TagKind? kind = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.DeviceTags.AsNoTracking().Include(t => t.Address).Include(t => t.Query).Include(t => t.QueryColumn).Where(t => t.DeviceId == deviceId);
        if (kind.HasValue) q = q.Where(t => t.Kind == kind.Value);
        return await q.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<DeviceTag?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceTags.AsNoTracking().Include(t => t.Device).Include(t => t.Address).FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<DeviceTag> SaveAsync(DeviceTag tag, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        tag.Name = tag.Name.Trim();
        tag.PlcAddress = tag.PlcAddress?.Trim() ?? string.Empty;
        tag.Alias = string.IsNullOrWhiteSpace(tag.Alias) ? null : tag.Alias.Trim();
        if (tag.Kind != TagKind.Plc) tag.PlcAddress = string.Empty;
        if (tag.Kind != TagKind.Plc) { tag.IsWriteEnabled = false; tag.WriteMinimum = null; tag.WriteMaximum = null; }
        if (tag.Kind != TagKind.External) { tag.QueryId = null; tag.QueryColumnId = null; }
        if (tag.ByteCount <= 0) tag.ByteCount = TagValueFormatter.DefaultByteSize(tag.DataType, tag.ElementCount);

        var validation = new DeviceTagValidator().Validate(tag);
        if (!validation.IsValid) throw new BusinessRuleException(string.Join(Environment.NewLine, validation.Errors.Select(e => e.ErrorMessage)));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == tag.DeviceId, ct) ?? throw new NotFoundException("Device", tag.DeviceId);

        if (await db.DeviceTags.AnyAsync(t => t.DeviceId == tag.DeviceId && t.Id != tag.Id && t.Name == tag.Name, ct))
            throw new BusinessRuleException($"Tag '{tag.Name}' already exists on device '{device.Name}'.");

        if (tag.Kind == TagKind.External && tag.QueryColumnId.HasValue)
        {
            var col = await db.DeviceQueryColumns.Include(c => c.Query).FirstOrDefaultAsync(c => c.Id == tag.QueryColumnId, ct);
            if (col is null || col.QueryId != tag.QueryId || col.Query!.DeviceId != tag.DeviceId)
                throw new BusinessRuleException("Selected query column does not belong to the chosen query of this device.");
        }

        DeviceTag entity;
        if (tag.Id == 0)
        {
            entity = new DeviceTag { DeviceId = tag.DeviceId };
            db.DeviceTags.Add(entity);
        }
        else
        {
            entity = await db.DeviceTags.FirstOrDefaultAsync(t => t.Id == tag.Id, ct) ?? throw new NotFoundException("Tag", tag.Id);
        }

        entity.AddressId = tag.AddressId;
        entity.Name = tag.Name;
        entity.Alias = tag.Alias;
        entity.Kind = tag.Kind;
        entity.DataType = tag.DataType;
        entity.PlcAddress = tag.PlcAddress;
        entity.RegisterType = tag.RegisterType;
        entity.ElementCount = tag.ElementCount;
        entity.ByteCount = tag.ByteCount;
        entity.IsSelected = tag.IsSelected;
        entity.SetValue = tag.SetValue;
        entity.ScaleFactor = tag.ScaleFactor;
        entity.ScaleOffset = tag.ScaleOffset;
        entity.DecimalPlaces = tag.DecimalPlaces;
        entity.EngineeringUnit = tag.EngineeringUnit;
        entity.IsWriteEnabled = tag.IsWriteEnabled;
        entity.WriteMinimum = tag.WriteMinimum;
        entity.WriteMaximum = tag.WriteMaximum;
        entity.MinimumWriteIntervalMs = Math.Clamp(tag.MinimumWriteIntervalMs, 250, 60000);
        entity.QueryId = tag.QueryId;
        entity.QueryColumnId = tag.QueryColumnId;
        await db.SaveChangesAsync(ct);

        await _audit.RecordAsync("SaveTag", $"{device.Name}/{entity.Name}", $"{entity.Kind} {entity.DataType} {entity.PlcAddress}", true, ct);
        _capture.RequestReconcile();
        return entity;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceTags.Include(t => t.Device).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tag", id);

        if (entity.Device!.CaptureMode == CaptureMode.PlcTrigger && entity.Device.TriggerTagName == entity.Name)
            throw new BusinessRuleException($"Tag '{entity.Name}' is the PLC trigger tag of this device. Change the capture mode first.");
        if (await db.DeviceTriggerConditions.AnyAsync(c => c.DeviceId == entity.DeviceId && c.TagName == entity.Name, ct))
            throw new BusinessRuleException($"Tag '{entity.Name}' is used in a logical trigger condition. Remove the condition first.");

        db.DeviceTags.Remove(entity);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("DeleteTag", $"{entity.Device.Name}/{entity.Name}", null, true, ct);
        _capture.RequestReconcile();
    }

    public async Task SetSelectedAsync(IEnumerable<int> tagIds, bool selected, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        var ids = tagIds.Distinct().ToList();
        if (ids.Count == 0) return;
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.DeviceTags.Where(t => ids.Contains(t.Id)).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsSelected, selected), ct);
        _capture.RequestReconcile();
    }

    public async Task SetCustomValueAsync(int tagId, string value, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.WriteToPlc);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceTags.Include(t => t.Device).FirstOrDefaultAsync(t => t.Id == tagId, ct) ?? throw new NotFoundException("Tag", tagId);
        if (entity.Kind != TagKind.Custom)
            throw new BusinessRuleException("Only Custom tags accept a manual value. Use 'Write to PLC' for PLC tags.");

        entity.SetValue = value.Trim();
        entity.CurrentValue = entity.SetValue;
        entity.CurrentQuality = TagQuality.Good;
        entity.CurrentValueAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("SetCustomValue", $"{entity.Device!.Name}/{entity.Name}", value, true, ct);
        _capture.RequestReconcile();
    }

    public async Task<int> ImportDiscoveredAsync(int deviceId, IEnumerable<DiscoveredTag> discovered, bool selectNew, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device", deviceId);
        var existing = (await db.DeviceTags.Where(t => t.DeviceId == deviceId).Select(t => t.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var d in discovered.Where(d => !d.IsStructure))
        {
            if (string.IsNullOrWhiteSpace(d.Name) || existing.Contains(d.Name)) continue;
            var dataType = d.DataType ?? TagDataType.Int32;
            db.DeviceTags.Add(new DeviceTag
            {
                DeviceId = deviceId,
                Name = d.Name,
                PlcAddress = d.Name,
                Kind = TagKind.Plc,
                DataType = dataType,
                ElementCount = Math.Max(1, d.ElementCount),
                ByteCount = d.ByteCount > 0 ? d.ByteCount : TagValueFormatter.DefaultByteSize(dataType, d.ElementCount),
                IsSelected = selectNew,
            });
            existing.Add(d.Name);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            await _audit.RecordAsync("ImportTags", device.Name, $"{added} tag(s) imported from controller", true, ct);
            _logger.LogInformation("Imported {Count} browsed tags into device {Device}.", added, device.Name);
            _capture.RequestReconcile();
        }
        return added;
    }
}
