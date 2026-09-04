using System.Diagnostics;
using FluentValidation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Application.Validators;
using WTT.Domain.Entities;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class DeviceServerService : IDeviceServerService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly ISecureStorage _secure;
    private readonly SqlConnectionStringFactory _connStrings;
    private readonly ILogger<DeviceServerService> _logger;

    public DeviceServerService(IDbContextFactory<AppDbContext> factory, ICurrentUserService currentUser, IAuditService audit,
        ISecureStorage secure, SqlConnectionStringFactory connStrings, ILogger<DeviceServerService> logger)
    {
        _factory = factory;
        _currentUser = currentUser;
        _audit = audit;
        _secure = secure;
        _connStrings = connStrings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeviceServer>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceServers.AsNoTracking().Include(s => s.Device).OrderBy(s => s.Device!.Name).ToListAsync(ct);
    }

    public async Task<DeviceServer?> GetForDeviceAsync(int deviceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceServers.AsNoTracking().Include(s => s.Device).FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct);
    }

    public async Task<DeviceServer> SaveAsync(DeviceServer server, string? plainPassword, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTransfer);
        server.ServerName = server.ServerName.Trim();
        server.DatabaseName = server.DatabaseName.Trim();
        server.TableName = string.IsNullOrWhiteSpace(server.TableName) ? "TagDetail" : server.TableName.Trim();
        server.UserId = string.IsNullOrWhiteSpace(server.UserId) ? null : server.UserId.Trim();

        var validation = new DeviceServerValidator().Validate(server);
        if (!validation.IsValid) throw new BusinessRuleException(string.Join(Environment.NewLine, validation.Errors.Select(e => e.ErrorMessage)));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == server.DeviceId, ct) ?? throw new NotFoundException("Device", server.DeviceId);

        var entity = await db.DeviceServers.FirstOrDefaultAsync(s => s.DeviceId == server.DeviceId, ct);
        if (entity is null)
        {
            entity = new DeviceServer { DeviceId = server.DeviceId };
            db.DeviceServers.Add(entity);
        }

        entity.ServerName = server.ServerName;
        entity.DatabaseName = server.DatabaseName;
        entity.TableName = server.TableName;
        entity.UseIntegratedSecurity = server.UseIntegratedSecurity;
        entity.UserId = server.UseIntegratedSecurity ? null : server.UserId;
        entity.Encrypt = server.Encrypt;
        entity.TrustServerCertificate = server.TrustServerCertificate;
        entity.IsEnabled = server.IsEnabled;
        entity.LastError = null;

        if (server.UseIntegratedSecurity) entity.ProtectedPassword = null;
        else if (plainPassword is not null) entity.ProtectedPassword = _secure.Protect(plainPassword);
        else if (entity.Id == 0) throw new BusinessRuleException("Enter the SQL password.");

        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("SaveTransferServer", device.Name, $"{entity.ServerName}/{entity.DatabaseName}.{entity.TableName}", true, ct);
        return entity;
    }

    public async Task DeleteAsync(int deviceId, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageTransfer);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceServers.Include(s => s.Device).FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct) ?? throw new NotFoundException("Transfer server", deviceId);
        db.DeviceServers.Remove(entity);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("DeleteTransferServer", entity.Device?.Name, null, true, ct);
    }

    public async Task SetEnabledAsync(int deviceId, bool enabled, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ControlCapture);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceServers.Include(s => s.Device).FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct) ?? throw new NotFoundException("Transfer server", deviceId);
        entity.IsEnabled = enabled;
        entity.LastError = null;
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("SetTransferEnabled", entity.Device?.Name, enabled.ToString(), true, ct);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(DeviceServer server, string? plainPassword, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var cs = _connStrings.ForServer(server, plainPassword, connectTimeoutSeconds: 10);
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT @@VERSION", conn);
            var version = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? string.Empty;
            var firstLine = version.Split('\n')[0].Trim();
            return ConnectionTestResult.Ok($"Connected: {firstLine}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL connection test failed for {Server}/{Db}.", server.ServerName, server.DatabaseName);
            return ConnectionTestResult.Fail(ex.Message, sw.Elapsed);
        }
    }
}
