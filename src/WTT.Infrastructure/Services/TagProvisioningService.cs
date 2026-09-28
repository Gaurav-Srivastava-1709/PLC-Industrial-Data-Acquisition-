using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Catalog;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Common;
using WTT.Domain.Entities;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

/// <summary>
/// Seeds default tags for a newly created device based on protocol/model.
///
/// Strategy:
///   1. Browse-capable devices (ControlLogix, CompactLogix): imports tags via live PLC browse.
///      Falls back to the template if browse fails or the device is offline.
///   2. All others (S7, Modbus, PLC-5, etc.): inserts starter tags from TagTemplateLibrary.
///
/// Transaction Safety:
///   This service is called AFTER DeviceService.CreateAsync() — the device is
///   already saved and its Id is available. Tags are saved in a separate
///   DbContext factory call. Partial failure (tags fail) logs a
///   warning — the device remains usable but without tags.
///
/// DI Lifetime: Singleton (injects IDbContextFactory, not DbContext directly).
/// </summary>
public sealed class TagProvisioningService : ITagProvisioningService
{
    // Maximum wait time for browse — the UI must not hang
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IPlcOperationsService _plcOps;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly ILogger<TagProvisioningService> _logger;

    public TagProvisioningService(
        IDbContextFactory<AppDbContext> factory,
        IPlcOperationsService plcOps,
        ICurrentUserService currentUser,
        IAuditService audit,
        ILogger<TagProvisioningService> logger)
    {
        _factory = factory;
        _plcOps = plcOps;
        _currentUser = currentUser;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Provisions tags for a device.
    /// Does nothing when seedTags=false (admin unchecked the checkbox).
    /// </summary>
    public async Task<int> ProvisionAsync(Device device, bool seedTags, CancellationToken ct = default)
    {
        // Admin turned "Auto-create tags" OFF — do nothing
        if (!seedTags) return 0;

        // Only Admin/Operator roles holding the ManageTags permission
        if (!_currentUser.HasPermission(Permission.ManageTags))
        {
            _logger.LogWarning("Tag provisioning skipped — user lacks ManageTags permission.");
            return 0;
        }

        _logger.LogInformation(
            "Starting tag provisioning for device {Device} (Model={Model}, Protocol={Protocol}).",
            device.Name, device.Model, device.Protocol);

        try
        {
            // Check driver capabilities — CanBrowseTags?
            // Note: creates a fresh driver via PlcDriverFactory for capability check only
            // (does not actually connect during provisioning)
            var canBrowse = CanBrowse(device);

            int count;
            if (canBrowse)
            {
                // Strategy 1: Live Browse → fallback to Template on failure
                count = await ProvisionViaBrowseAsync(device, ct);
            }
            else
            {
                // Strategy 2: Template Seeding
                count = await ProvisionViaTemplateAsync(device, ct);
            }

            if (count > 0)
            {
                await _audit.RecordAsync(
                    "AutoProvisionTags",
                    device.Name,
                    $"{count} tag(s) auto-provisioned for {device.Model} ({(canBrowse ? "browse" : "template")})",
                    true, ct);
                _logger.LogInformation("Provisioned {Count} tags for device {Device}.", count, device.Name);
            }

            return count;
        }
        catch (Exception ex)
        {
            // Never delete the device when provisioning fails — log a warning
            _logger.LogWarning(ex,
                "Tag provisioning failed for device {Device}. Device is saved but has no tags.",
                device.Name);
            return 0;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Strategy 1: Browse (ControlLogix + CompactLogix only)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<int> ProvisionViaBrowseAsync(Device device, CancellationToken ct)
    {
        _logger.LogInformation("Attempting live browse for {Device}...", device.Name);

        try
        {
            // 15-second timeout — the UI must not hang
            using var browseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            browseCts.CancelAfter(BrowseTimeout);

            var discovered = await _plcOps.BrowseTagsAsync(device, browseCts.Token);

            if (discovered.Count == 0)
            {
                _logger.LogWarning("Browse returned 0 tags for {Device}. Falling back to template.", device.Name);
                return await ProvisionViaTemplateAsync(device, ct);
            }

            // Reuses ImportDiscoveredAsync semantics — duplicate check + ByteCount
            // but inserts directly into the DB here (same Singleton pattern)
            return await SaveDiscoveredTagsAsync(device, discovered, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Browse timed out for {Device} (>{Sec}s). Falling back to template.",
                device.Name, BrowseTimeout.TotalSeconds);
            return await ProvisionViaTemplateAsync(device, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browse failed for {Device}. Falling back to template.", device.Name);
            return await ProvisionViaTemplateAsync(device, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Strategy 2: Template Seeding (S7, Modbus, PLC-5, MicroLogix, etc.)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<int> ProvisionViaTemplateAsync(Device device, CancellationToken ct)
    {
        var templateTags = TagTemplateLibrary.GetTemplateFor(device.Model);
        if (templateTags.Count == 0) return 0;

        await using var db = await _factory.CreateDbContextAsync(ct);

        // Check existing tags — idempotent (skip when tags already exist)
        var existingNames = await db.DeviceTags
            .Where(t => t.DeviceId == device.Id)
            .Select(t => t.Name)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        var added = 0;
        foreach (var template in templateTags)
        {
            // Duplicate name → skip (don't overwrite existing tags)
            if (existingNames.Contains(template.Name)) continue;

            db.DeviceTags.Add(new DeviceTag
            {
                DeviceId     = device.Id,
                Name         = template.Name,
                Alias        = template.Alias,
                Kind         = template.Kind,
                DataType     = template.DataType,
                PlcAddress   = template.PlcAddress,
                RegisterType = template.RegisterType,
                ElementCount = template.ElementCount,
                // ByteCount: calculated via TagValueFormatter.DefaultByteSize
                ByteCount    = TagValueFormatter.DefaultByteSize(template.DataType, template.ElementCount),
                IsSelected   = true,
                DecimalPlaces    = template.DecimalPlaces,
                EngineeringUnit  = template.EngineeringUnit,
            });
            existingNames.Add(template.Name);
            added++;
        }

        if (added > 0)
            await db.SaveChangesAsync(ct);

        return added;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Persist browse-discovered tags to the DB
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<int> SaveDiscoveredTagsAsync(
        Device device,
        IReadOnlyList<WTT.Domain.ValueObjects.DiscoveredTag> discovered,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var existing = await db.DeviceTags
            .Where(t => t.DeviceId == device.Id)
            .Select(t => t.Name)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        var added = 0;
        foreach (var d in discovered.Where(d => !d.IsStructure))
        {
            if (string.IsNullOrWhiteSpace(d.Name) || existing.Contains(d.Name)) continue;

            var dataType = d.DataType ?? WTT.Domain.Enums.TagDataType.Int32;
            db.DeviceTags.Add(new DeviceTag
            {
                DeviceId     = device.Id,
                Name         = d.Name,
                PlcAddress   = d.Name,
                Kind         = WTT.Domain.Enums.TagKind.Plc,
                DataType     = dataType,
                ElementCount = Math.Max(1, d.ElementCount),
                ByteCount    = d.ByteCount > 0
                    ? d.ByteCount
                    : TagValueFormatter.DefaultByteSize(dataType, d.ElementCount),
                IsSelected   = true,
            });
            existing.Add(d.Name);
            added++;
        }

        if (added > 0)
            await db.SaveChangesAsync(ct);

        return added;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks browse support for a device model.
    /// Per AllenBradleyDriver.cs:31-32: only ControlLogix + CompactLogix return true.
    /// </summary>
    private static bool CanBrowse(Device device) =>
        device.Model is WTT.Domain.Enums.DeviceModel.ControlLogix
                     or WTT.Domain.Enums.DeviceModel.CompactLogix;
}
