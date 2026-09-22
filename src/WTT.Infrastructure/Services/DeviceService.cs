using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Catalog;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Application.Validators;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class DeviceService : IDeviceService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly IDataCaptureController _capture;
    // Auto tag provisioning service — device create ke baad tags seed karta hai
    private readonly ITagProvisioningService _provisioning;
    private readonly ILogger<DeviceService> _logger;

    public DeviceService(
        IDbContextFactory<AppDbContext> factory,
        ICurrentUserService currentUser,
        IAuditService audit,
        IDataCaptureController capture,
        ITagProvisioningService provisioning,
        ILogger<DeviceService> logger)
    {
        _factory = factory;
        _currentUser = currentUser;
        _audit = audit;
        _capture = capture;
        _provisioning = provisioning;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Devices.AsNoTracking().Include(d => d.Server).OrderBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<Device?> GetByIdAsync(int id, bool includeChildren = false, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        IQueryable<Device> q = db.Devices.AsNoTracking();
        if (includeChildren)
            q = q.Include(d => d.Addresses)
                 .Include(d => d.Tags)
                 .Include(d => d.Queries).ThenInclude(x => x.Columns)
                 .Include(d => d.TriggerConditions)
                 .Include(d => d.Server);
        return await q.FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<Device> CreateAsync(Device device, CancellationToken ct = default)
    {
        return await CreateAsync(device, seedTags: false, ct);
    }

    /// <summary>
    /// Device create karo aur optional auto tag provisioning karo.
    /// <paramref name="seedTags"/> = true → protocol/model ke hisab se default tags create honge.
    /// </summary>
    public async Task<Device> CreateAsync(Device device, bool seedTags, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        Normalize(device);
        Validate(device);

        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.Devices.AnyAsync(d => d.Name == device.Name, ct))
            throw new BusinessRuleException($"A device named '{device.Name}' already exists.");

        // Device save karo — Id generate hoga jo tags ke liye chahiye
        var entity = new Device();
        CopyEditable(device, entity);
        db.Devices.Add(entity);
        await db.SaveChangesAsync(ct);

        await _audit.RecordAsync("CreateDevice", entity.Name, $"{entity.Protocol}/{entity.Model} @ {entity.Host}:{entity.Port}", true, ct);

        // Auto Tag Provisioning — Device save hone ke BAAD call karo
        // Agar provisioning fail bhi ho, device saved rahegi (non-destructive)
        var tagCount = await _provisioning.ProvisionAsync(entity, seedTags, ct);
        if (tagCount > 0)
            _logger.LogInformation("Auto-provisioned {Count} default tags for device {Device} ({Model}).",
                tagCount, entity.Name, entity.Model);

        _capture.RequestReconcile();
        return entity;
    }

    public async Task<Device> UpdateAsync(Device device, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        Normalize(device);
        Validate(device);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == device.Id, ct) ?? throw new NotFoundException("Device", device.Id);
        if (await db.Devices.AnyAsync(d => d.Id != device.Id && d.Name == device.Name, ct))
            throw new BusinessRuleException($"A device named '{device.Name}' already exists.");

        var renamed = entity.Name != device.Name;
        CopyEditable(device, entity);
        entity.ExternalContextLoaded = false; // config change → refresh context on next trigger
        await db.SaveChangesAsync(ct);

        if (renamed)
        {
            // Keep untransferred buffered rows attached to the new name so downstream keys stay consistent.
            await db.TagReadings.Where(r => r.DeviceId == entity.Id && !r.IsTransferred)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeviceName, entity.Name), ct);
        }

        await _audit.RecordAsync("UpdateDevice", entity.Name, null, true, ct);
        _capture.RequestReconcile();
        return entity;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Device", id);

        var pending = await db.TagReadings.CountAsync(r => r.DeviceId == id && !r.IsTransferred, ct);
        if (pending > 0)
            throw new BusinessRuleException($"Device '{entity.Name}' still has {pending:N0} untransferred readings. Transfer or purge them first.");

        db.Devices.Remove(entity);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("DeleteDevice", entity.Name, null, true, ct);
        _logger.LogWarning("Device {Device} deleted by {User}.", entity.Name, _currentUser.Username);
        _capture.RequestReconcile();
    }

    public async Task SetRunStateAsync(int id, bool? captureEnabled, bool? monitorEnabled, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ControlCapture);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Device", id);
        if (captureEnabled.HasValue) entity.IsCaptureEnabled = captureEnabled.Value;
        if (monitorEnabled.HasValue) entity.IsMonitorEnabled = monitorEnabled.Value;
        entity.ExternalContextLoaded = false;
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("SetRunState", entity.Name, $"Capture={entity.IsCaptureEnabled}; Monitor={entity.IsMonitorEnabled}", true, ct);
        _capture.RequestReconcile();
    }

    public async Task SetCaptureModeAsync(int id, CaptureMode mode, string? triggerTagName, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageDevices);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Device", id);

        if (mode == CaptureMode.PlcTrigger)
        {
            if (string.IsNullOrWhiteSpace(triggerTagName))
                throw new BusinessRuleException("Select a trigger tag for PLC Trigger mode.");
            var exists = await db.DeviceTags.AnyAsync(t => t.DeviceId == id && t.Name == triggerTagName && t.Kind == TagKind.Plc, ct);
            if (!exists) throw new BusinessRuleException($"Trigger tag '{triggerTagName}' is not a PLC tag of this device.");
        }
        if (mode == CaptureMode.Logical && !await db.DeviceTriggerConditions.AnyAsync(c => c.DeviceId == id, ct))
            throw new BusinessRuleException("Add at least one condition before selecting Logical mode.");

        entity.CaptureMode = mode;
        entity.TriggerTagName = mode == CaptureMode.PlcTrigger ? triggerTagName!.Trim() : null;
        entity.ExternalContextLoaded = false;
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("SetCaptureMode", entity.Name, $"{mode} {triggerTagName}", true, ct);
        _capture.RequestReconcile();
    }

    private static void Normalize(Device d)
    {
        d.Name = d.Name.Trim();
        d.Host = d.Host.Trim();
        d.Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim();
        d.CipPath = string.IsNullOrWhiteSpace(d.CipPath) ? null : d.CipPath.Trim();
        var info = PlcModelCatalog.Models.FirstOrDefault(m => m.Model == d.Model);
        if (info is not null)
        {
            d.Protocol = info.Protocol;
            d.Manufacturer = info.Manufacturer;
            if (d.Port <= 0) d.Port = info.DefaultPort;
        }
        if (d.ConnectionSize is 4000 or 4064) d.ConnectionSize = 4002;
    }

    private static void Validate(Device d)
    {
        var result = new DeviceValidator().Validate(d);
        if (!result.IsValid) throw new BusinessRuleException(string.Join(Environment.NewLine, result.Errors.Select(e => e.ErrorMessage)));
    }

    private static void CopyEditable(Device src, Device dst)
    {
        dst.Name = src.Name;
        dst.Description = src.Description;
        dst.Protocol = src.Protocol;
        dst.Manufacturer = src.Manufacturer;
        dst.Model = src.Model;
        dst.Host = src.Host;
        dst.Port = src.Port;
        dst.PollIntervalMs = src.PollIntervalMs;
        dst.TimeoutMs = src.TimeoutMs;
        dst.Slot = src.Slot;
        dst.Rack = src.Rack;
        dst.ConnectionSize = src.ConnectionSize;
        dst.CipPath = src.CipPath;
        dst.UnitId = src.UnitId;
        dst.WordOrder = src.WordOrder;
        dst.OneBasedAddressing = src.OneBasedAddressing;
        dst.IsEnabled = src.IsEnabled;
        if (dst.Id == 0)
        {
            dst.CaptureMode = src.CaptureMode;
            dst.TriggerTagName = src.TriggerTagName;
            dst.IsCaptureEnabled = src.IsCaptureEnabled;
            dst.IsMonitorEnabled = src.IsMonitorEnabled;
        }
    }
}
