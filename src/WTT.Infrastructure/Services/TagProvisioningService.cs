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
/// Jab naya device create ho, protocol/model ke hisab se default tags seed karta hai.
///
/// Strategy:
///   1. Browse-capable devices (ControlLogix, CompactLogix): PLC se live browse karke
///      tags import karta hai. Agar browse fail ho ya offline ho, Template pe fallback.
///   2. Baaki sabhi (S7, Modbus, PLC-5, etc.): TagTemplateLibrary se starter tags daalta hai.
///
/// Transaction Safety:
///   Is service ko DeviceService.CreateAsync() ke BAAD call kiya jata hai — device
///   already saved hoti hai aur uska Id available hota hai. Tags ek alag
///   DbContext factory call mein save hote hain. Partial failure (tags fail) ek
///   warning log karega — device usable rahegi par bina tags ke.
///
/// DI Lifetime: Singleton (IDbContextFactory inject karta hai, not DbContext directly).
/// </summary>
public sealed class TagProvisioningService : ITagProvisioningService
{
    // Browse ke liye maximum wait time — UI hang nahi hona chahiye
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
    /// Device ke liye tags provision karta hai.
    /// seedTags=false hone pe kuch nahi karta (admin ne checkbox uncheck kiya).
    /// </summary>
    public async Task<int> ProvisionAsync(Device device, bool seedTags, CancellationToken ct = default)
    {
        // Admin ne "Auto-create tags" OFF kiya — kuch mat karo
        if (!seedTags) return 0;

        // Sirf Admin/Operator jo ManageTags permission rakhte hain
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
            // Driver ki Capabilities check karo — CanBrowseTags?
            // Note: PlcDriverFactory se naya driver banate hain sirf capability check ke liye
            // (actual connect nahi karte provision mein)
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
            // Provisioning fail hone pe device delete nahi karte — warning log karo
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
            // 15 second timeout — UI hang nahi hona chahiye
            using var browseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            browseCts.CancelAfter(BrowseTimeout);

            var discovered = await _plcOps.BrowseTagsAsync(device, browseCts.Token);

            if (discovered.Count == 0)
            {
                _logger.LogWarning("Browse returned 0 tags for {Device}. Falling back to template.", device.Name);
                return await ProvisionViaTemplateAsync(device, ct);
            }

            // ImportDiscoveredAsync existing code use karta hai — duplicate check + ByteCount
            // Lekin yahan hum seedha DB mein daalenge (same Singleton pattern)
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

        // Existing tags check karo — idempotent (agar already tags hain toh skip)
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
                // ByteCount: TagValueFormatter.DefaultByteSize se calculate karo
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
    // Browse se mile tags DB mein save karo
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
    /// Device model ke liye browse support check karo.
    /// AllenBradleyDriver.cs:31-32 se: sirf ControlLogix + CompactLogix = true.
    /// </summary>
    private static bool CanBrowse(Device device) =>
        device.Model is WTT.Domain.Enums.DeviceModel.ControlLogix
                     or WTT.Domain.Enums.DeviceModel.CompactLogix;
}
