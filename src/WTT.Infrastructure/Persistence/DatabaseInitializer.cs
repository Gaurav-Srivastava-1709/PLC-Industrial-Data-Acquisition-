using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.Enums;

namespace WTT.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations, enables WAL and seeds the initial administrator.
/// The seed admin is created ONLY when no user exists and must change the password at first login.
/// </summary>
public sealed class DatabaseInitializer
{
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "Admin@123";

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IDbContextFactory<AppDbContext> factory, IPasswordHasher hasher, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _hasher = hasher;
        _logger = logger;
    }

    /// <returns>True when a default admin account was created during this run.</returns>
    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            _logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
        }

        // Write-ahead logging: readers never block the capture writer.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        if (await db.Users.AnyAsync(ct))
            return false;

        db.Users.Add(new User
        {
            Username = DefaultAdminUsername,
            NormalizedUsername = DefaultAdminUsername.ToUpperInvariant(),
            FullName = "System Administrator",
            PasswordHash = _hasher.Hash(DefaultAdminPassword),
            Role = UserRole.Admin,
            IsActive = true,
            MustChangePassword = true,
        });
        await db.SaveChangesAsync(ct);
        _logger.LogWarning("No users found. Default administrator '{User}' created with a temporary password; it must be changed at first login.", DefaultAdminUsername);
        return true;
    }
}
