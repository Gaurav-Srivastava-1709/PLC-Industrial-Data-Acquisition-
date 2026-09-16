using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WTT.Infrastructure.Persistence;

/// <summary>
/// Applies pending SQL Server migrations before any application service starts.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IDbContextFactory<AppDbContext> factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            _logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
        }

    }

    public async Task<bool> VerifySchemaCurrentAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (!await db.Database.CanConnectAsync(ct)) throw new InvalidOperationException("WTT database is unavailable.");
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0) throw new InvalidOperationException($"Database upgrade required: {string.Join(", ", pending)}");
        return await db.Users.AnyAsync(ct);
    }
}
