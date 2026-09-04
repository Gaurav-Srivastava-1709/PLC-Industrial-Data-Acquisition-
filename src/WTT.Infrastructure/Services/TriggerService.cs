using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Application.Validators;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class TriggerService : ITriggerService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly IDataCaptureController _capture;

    public TriggerService(IDbContextFactory<AppDbContext> factory, ICurrentUserService currentUser, IAuditService audit, IDataCaptureController capture)
    {
        _factory = factory;
        _currentUser = currentUser;
        _audit = audit;
        _capture = capture;
    }

    public async Task<IReadOnlyList<DeviceTriggerCondition>> GetForDeviceAsync(int deviceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceTriggerConditions.AsNoTracking().Where(c => c.DeviceId == deviceId).OrderBy(c => c.Order).ToListAsync(ct);
    }

    public async Task<DeviceTriggerCondition> AddAsync(DeviceTriggerCondition condition, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        condition.TagName = condition.TagName.Trim();
        condition.Value = condition.Operator == TriggerOperator.OnChange ? null : condition.Value?.Trim();
        var validation = new TriggerConditionValidator().Validate(condition);
        if (!validation.IsValid) throw new BusinessRuleException(validation.Errors[0].ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == condition.DeviceId, ct) ?? throw new NotFoundException("Device", condition.DeviceId);
        if (!await db.DeviceTags.AnyAsync(t => t.DeviceId == condition.DeviceId && t.Name == condition.TagName, ct))
            throw new BusinessRuleException($"Tag '{condition.TagName}' does not exist on device '{device.Name}'.");

        var maxOrder = await db.DeviceTriggerConditions.Where(c => c.DeviceId == condition.DeviceId).MaxAsync(c => (int?)c.Order, ct) ?? 0;
        var entity = new DeviceTriggerCondition
        {
            DeviceId = condition.DeviceId,
            Order = maxOrder + 1,
            Conjunction = maxOrder == 0 ? LogicalOperator.None : (condition.Conjunction == LogicalOperator.None ? LogicalOperator.And : condition.Conjunction),
            TagName = condition.TagName,
            Operator = condition.Operator,
            Value = condition.Value,
        };
        db.DeviceTriggerConditions.Add(entity);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("AddTriggerCondition", device.Name, $"{entity.Conjunction} {entity.TagName} {entity.Operator} {entity.Value}", true, ct);
        _capture.RequestReconcile();
        return entity;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceTriggerConditions.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Condition", id);
        db.DeviceTriggerConditions.Remove(entity);
        await db.SaveChangesAsync(ct);

        // Renumber and make sure the first clause has no conjunction.
        var rest = await db.DeviceTriggerConditions.Where(c => c.DeviceId == entity.DeviceId).OrderBy(c => c.Order).ToListAsync(ct);
        for (var i = 0; i < rest.Count; i++)
        {
            rest[i].Order = i + 1;
            if (i == 0) rest[i].Conjunction = LogicalOperator.None;
            else if (rest[i].Conjunction == LogicalOperator.None) rest[i].Conjunction = LogicalOperator.And;
        }
        await db.SaveChangesAsync(ct);
        _capture.RequestReconcile();
    }

    public async Task ReorderAsync(int deviceId, IReadOnlyList<int> orderedIds, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var items = await db.DeviceTriggerConditions.Where(c => c.DeviceId == deviceId).ToListAsync(ct);
        var order = 1;
        foreach (var id in orderedIds)
        {
            var item = items.FirstOrDefault(c => c.Id == id);
            if (item is null) continue;
            item.Order = order++;
        }
        await db.SaveChangesAsync(ct);
        _capture.RequestReconcile();
    }
}
