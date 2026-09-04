using System.Globalization;
using FluentValidation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Application.Validators;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

/// <summary>
/// External SQL queries that supply context values (batch id, shift, recipe...) for External tags.
/// </summary>
public sealed class ExternalQueryService : IExternalQueryService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly ISecureStorage _secure;
    private readonly SqlConnectionStringFactory _connStrings;
    private readonly ILogger<ExternalQueryService> _logger;

    public ExternalQueryService(IDbContextFactory<AppDbContext> factory, ICurrentUserService currentUser, IAuditService audit,
        ISecureStorage secure, SqlConnectionStringFactory connStrings, ILogger<ExternalQueryService> logger)
    {
        _factory = factory;
        _currentUser = currentUser;
        _audit = audit;
        _secure = secure;
        _connStrings = connStrings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeviceQuery>> GetForDeviceAsync(int deviceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceQueries.AsNoTracking().Include(q => q.Columns.OrderBy(c => c.Ordinal)).Where(q => q.DeviceId == deviceId).OrderBy(q => q.Name).ToListAsync(ct);
    }

    public async Task<DeviceQuery> SaveAsync(DeviceQuery query, string? plainPassword, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        query.Name = query.Name.Trim();
        query.ServerName = query.ServerName.Trim();
        query.DatabaseName = query.DatabaseName.Trim();
        query.UserId = string.IsNullOrWhiteSpace(query.UserId) ? null : query.UserId.Trim();
        query.QueryText = query.QueryText.Trim();

        var validation = new DeviceQueryValidator().Validate(query);
        if (!validation.IsValid) throw new BusinessRuleException(string.Join(Environment.NewLine, validation.Errors.Select(e => e.ErrorMessage)));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == query.DeviceId, ct) ?? throw new NotFoundException("Device", query.DeviceId);
        if (await db.DeviceQueries.AnyAsync(q => q.DeviceId == query.DeviceId && q.Id != query.Id && q.Name == query.Name, ct))
            throw new BusinessRuleException($"Query '{query.Name}' already exists on this device.");

        DeviceQuery entity;
        if (query.Id == 0)
        {
            entity = new DeviceQuery { DeviceId = query.DeviceId };
            db.DeviceQueries.Add(entity);
        }
        else
        {
            entity = await db.DeviceQueries.FirstOrDefaultAsync(q => q.Id == query.Id, ct) ?? throw new NotFoundException("Query", query.Id);
        }

        var queryChanged = entity.QueryText != query.QueryText || entity.ServerName != query.ServerName || entity.DatabaseName != query.DatabaseName;
        entity.Name = query.Name;
        entity.ServerName = query.ServerName;
        entity.DatabaseName = query.DatabaseName;
        entity.UseIntegratedSecurity = query.UseIntegratedSecurity;
        entity.UserId = query.UseIntegratedSecurity ? null : query.UserId;
        entity.Encrypt = query.Encrypt;
        entity.TrustServerCertificate = query.TrustServerCertificate;
        entity.QueryText = query.QueryText;
        entity.QueryType = query.QueryType;
        if (query.UseIntegratedSecurity) entity.ProtectedPassword = null;
        else if (plainPassword is not null) entity.ProtectedPassword = _secure.Protect(plainPassword);
        else if (entity.Id == 0) throw new BusinessRuleException("Enter the SQL password.");

        if (queryChanged || entity.Id == 0)
        {
            entity.IsVerified = false;
            entity.VerifiedAtUtc = null;
        }
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("SaveExternalQuery", $"{device.Name}/{entity.Name}", null, true, ct);
        return entity;
    }

    public async Task DeleteAsync(int queryId, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceQueries.Include(q => q.Device).FirstOrDefaultAsync(q => q.Id == queryId, ct) ?? throw new NotFoundException("Query", queryId);
        var inUse = await db.DeviceTags.CountAsync(t => t.QueryId == queryId, ct);
        if (inUse > 0) throw new BusinessRuleException($"Query '{entity.Name}' is used by {inUse} external tag(s). Delete those tags first.");
        db.DeviceQueries.Remove(entity);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("DeleteExternalQuery", $"{entity.Device?.Name}/{entity.Name}", null, true, ct);
    }

    public async Task<ExternalQueryVerifyResult> VerifyAsync(int queryId, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTags);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceQueries.Include(q => q.Columns).FirstOrDefaultAsync(q => q.Id == queryId, ct) ?? throw new NotFoundException("Query", queryId);

        try
        {
            var (columns, row) = await ExecuteFirstRowAsync(entity, ct);

            // Sync column list (keep ids of unchanged columns so tag bindings survive).
            var keep = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            foreach (var old in entity.Columns.Where(c => !keep.Contains(c.ColumnName)).ToList())
                db.DeviceQueryColumns.Remove(old);
            for (var i = 0; i < columns.Count; i++)
            {
                var existing = entity.Columns.FirstOrDefault(c => c.ColumnName.Equals(columns[i], StringComparison.OrdinalIgnoreCase));
                if (existing is null) entity.Columns.Add(new DeviceQueryColumn { ColumnName = columns[i], Ordinal = i });
                else existing.Ordinal = i;
            }

            entity.IsVerified = true;
            entity.VerifiedAtUtc = DateTime.UtcNow;
            entity.LastError = null;
            await db.SaveChangesAsync(ct);
            await _audit.RecordAsync("VerifyExternalQuery", entity.Name, $"{columns.Count} column(s)", true, ct);
            return new ExternalQueryVerifyResult(true, columns, row, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entity.IsVerified = false;
            entity.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "External query {Query} verification failed.", entity.Name);
            return new ExternalQueryVerifyResult(false, Array.Empty<string>(), new Dictionary<string, string?>(), ex.Message);
        }
    }

    public async Task<bool> RefreshExternalTagValuesAsync(int deviceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var queries = await db.DeviceQueries.Include(q => q.Columns).Where(q => q.DeviceId == deviceId && q.IsVerified).ToListAsync(ct);
        if (queries.Count == 0) return true;

        var externalTags = await db.DeviceTags.Where(t => t.DeviceId == deviceId && t.Kind == TagKind.External && t.QueryColumnId != null).ToListAsync(ct);
        if (externalTags.Count == 0) return true;

        var allOk = true;
        var now = DateTime.UtcNow;
        foreach (var q in queries)
        {
            var tagsForQuery = externalTags.Where(t => t.QueryId == q.Id).ToList();
            if (tagsForQuery.Count == 0) continue;
            try
            {
                var (_, row) = await ExecuteFirstRowAsync(q, ct);
                foreach (var tag in tagsForQuery)
                {
                    var col = q.Columns.FirstOrDefault(c => c.Id == tag.QueryColumnId);
                    if (col is null || !row.TryGetValue(col.ColumnName, out var value))
                    {
                        tag.CurrentQuality = TagQuality.ConfigError;
                        continue;
                    }
                    tag.SetValue = value ?? string.Empty;
                    tag.CurrentValue = tag.SetValue;
                    tag.CurrentQuality = TagQuality.Good;
                    tag.CurrentValueAtUtc = now;
                }
                q.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                allOk = false;
                q.LastError = ex.Message;
                foreach (var tag in tagsForQuery) tag.CurrentQuality = TagQuality.Bad;
                _logger.LogWarning(ex, "External query {Query} failed while refreshing context for device {DeviceId}.", q.Name, deviceId);
            }
        }
        await db.SaveChangesAsync(ct);
        return allOk;
    }

    private async Task<(List<string> Columns, Dictionary<string, string?> Row)> ExecuteFirstRowAsync(DeviceQuery q, CancellationToken ct)
    {
        var cs = _connStrings.ForQuery(q, connectTimeoutSeconds: 10);
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(q.QueryText, conn) { CommandTimeout = 30 };
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, ct);

        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (await reader.ReadAsync(ct))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var v = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[columns[i]] = v switch
                {
                    null => null,
                    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => v.ToString(),
                };
            }
        }
        else
        {
            foreach (var c in columns) row[c] = null;
        }
        return (columns, row);
    }
}
