using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Catalog;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Application.Validators;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

/// <summary>Modbus address blocks and sequential tag generation.</summary>
public sealed class DeviceAddressService : IDeviceAddressService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly IDataCaptureController _capture;
    private readonly ILogger<DeviceAddressService> _logger;

    public DeviceAddressService(IDbContextFactory<AppDbContext> factory, ICurrentUserService currentUser, IAuditService audit, IDataCaptureController capture, ILogger<DeviceAddressService> logger)
    {
        _factory = factory;
        _currentUser = currentUser;
        _audit = audit;
        _capture = capture;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeviceAddress>> GetForDeviceAsync(int deviceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceAddresses.AsNoTracking().Where(a => a.DeviceId == deviceId).OrderBy(a => a.StartAddress).ToListAsync(ct);
    }

    public async Task<DeviceAddress> SaveAsync(DeviceAddress address, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        address.TagPrefix = address.TagPrefix.Trim();
        var validation = new DeviceAddressValidator().Validate(address);
        if (!validation.IsValid) throw new BusinessRuleException(string.Join(Environment.NewLine, validation.Errors.Select(e => e.ErrorMessage)));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == address.DeviceId, ct) ?? throw new NotFoundException("Device", address.DeviceId);
        if (device.Protocol != ProtocolType.ModbusTcp)
            throw new BusinessRuleException("Address blocks are only used by Modbus devices.");

        DeviceAddress entity;
        if (address.Id == 0)
        {
            entity = new DeviceAddress { DeviceId = address.DeviceId };
            db.DeviceAddresses.Add(entity);
        }
        else
        {
            entity = await db.DeviceAddresses.FirstOrDefaultAsync(a => a.Id == address.Id, ct) ?? throw new NotFoundException("Address block", address.Id);
        }

        entity.AddressType = address.AddressType;
        entity.RegisterType = address.RegisterType;
        entity.StartAddress = address.StartAddress;
        entity.DataType = address.DataType;
        entity.PointCount = address.PointCount;
        entity.TagPrefix = address.TagPrefix;
        entity.ZeroBasedTagIndex = address.ZeroBasedTagIndex;
        entity.ModuleOffset = address.ModuleOffset;
        entity.DecimalPlaces = address.DecimalPlaces;
        entity.Description = address.Description;
        await db.SaveChangesAsync(ct);

        if (entity.AddressType == ModbusAddressType.Sequential)
            await GenerateTagsInternalAsync(db, device, entity, ct);

        await _audit.RecordAsync("SaveAddressBlock", device.Name, $"{entity.AddressType} {entity.RegisterType} @ {entity.StartAddress} x{entity.PointCount}", true, ct);
        _capture.RequestReconcile();
        return entity;
    }

    public async Task DeleteAsync(int addressId, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceAddresses.Include(a => a.Device).FirstOrDefaultAsync(a => a.Id == addressId, ct) ?? throw new NotFoundException("Address block", addressId);
        var generatedTags = await db.DeviceTags.Where(t => t.AddressId == addressId).ToListAsync(ct);
        db.DeviceTags.RemoveRange(generatedTags);
        db.DeviceAddresses.Remove(entity);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("DeleteAddressBlock", entity.Device?.Name, $"Id={addressId}", true, ct);
        _capture.RequestReconcile();
    }

    public async Task<int> GenerateTagsAsync(int addressId, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceAddresses.Include(a => a.Device).FirstOrDefaultAsync(a => a.Id == addressId, ct) ?? throw new NotFoundException("Address block", addressId);
        if (entity.AddressType != ModbusAddressType.Sequential)
            throw new BusinessRuleException("Only Sequential blocks support tag generation.");
        var count = await GenerateTagsInternalAsync(db, entity.Device!, entity, ct);
        _capture.RequestReconcile();
        return count;
    }

    /// <summary>
    /// Replaces the block's tags with PointCount generated tags.
    /// Naming: "{Prefix}{index:000}" (legacy compatible: TagPrefix + "00" + n).
    /// Addressing: contiguous points, each occupying the register count of the data type;
    /// GM10 uses its channel bank layout instead.
    /// </summary>
    private async Task<int> GenerateTagsInternalAsync(AppDbContext db, Device device, DeviceAddress block, CancellationToken ct)
    {
        var existing = await db.DeviceTags.Where(t => t.AddressId == block.Id).ToListAsync(ct);
        db.DeviceTags.RemoveRange(existing);

        var regsPerPoint = TagValueFormatter.ModbusRegisterCount(block.DataType);
        var tags = new List<DeviceTag>(block.PointCount);

        for (var i = 0; i < block.PointCount; i++)
        {
            var index = block.ZeroBasedTagIndex ? i : i + 1;
            var offset = device.Model == DeviceModel.Gm10
                ? PlcModelCatalog.Gm10ChannelRegisterOffset(i + 1)
                : i * regsPerPoint;
            var address = block.StartAddress + block.ModuleOffset + offset;

            tags.Add(new DeviceTag
            {
                DeviceId = device.Id,
                AddressId = block.Id,
                Name = $"{block.TagPrefix}{index:000}",
                Kind = TagKind.Plc,
                DataType = block.DataType,
                RegisterType = block.RegisterType,
                PlcAddress = address.ToString(),
                ElementCount = 1,
                ByteCount = TagValueFormatter.DefaultByteSize(block.DataType),
                IsSelected = true,
                DecimalPlaces = block.DecimalPlaces,
            });
        }

        // Avoid duplicate names with tags in other blocks of the same device.
        var otherNames = await db.DeviceTags.Where(t => t.DeviceId == device.Id && t.AddressId != block.Id).Select(t => t.Name).ToListAsync(ct);
        var clash = tags.Select(t => t.Name).Intersect(otherNames, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (clash is not null)
            throw new BusinessRuleException($"Generated tag name '{clash}' already exists on this device. Use a different prefix.");

        db.DeviceTags.AddRange(tags);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Generated {Count} tags for block {Block} on device {Device}.", tags.Count, block.Id, device.Name);
        return tags.Count;
    }
}
