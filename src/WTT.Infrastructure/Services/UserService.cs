using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Security;
using WTT.Application.Validators;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class UserService : IUserService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly SecurityOptions _options;
    private readonly ILogger<UserService> _logger;

    public UserService(
        IDbContextFactory<AppDbContext> factory,
        IPasswordHasher hasher,
        ICurrentUserService currentUser,
        IAuditService audit,
        IOptions<SecurityOptions> options,
        ILogger<UserService> logger)
    {
        _factory = factory;
        _hasher = hasher;
        _currentUser = currentUser;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageUsers);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
        return users.Select(AuthService.ToDto).ToList();
    }

    public async Task<UserDto?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return u is null ? null : AuthService.ToDto(u);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageUsers);
        var validation = new CreateUserRequestValidator(_options).Validate(request);
        if (!validation.IsValid) throw new BusinessRuleException(validation.Errors[0].ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var normalized = request.Username.Trim().ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct))
            throw new BusinessRuleException($"Username '{request.Username}' already exists.");

        var user = new User
        {
            Username = request.Username.Trim(),
            NormalizedUsername = normalized,
            FullName = request.FullName.Trim(),
            PasswordHash = _hasher.Hash(request.Password),
            Role = request.Role,
            IsActive = true,
            MustChangePassword = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("CreateUser", user.Username, $"Role={user.Role}", true, ct);
        _logger.LogInformation("User {User} created with role {Role} by {Actor}.", user.Username, user.Role, _currentUser.Username);
        return AuthService.ToDto(user);
    }

    public async Task<UserDto> UpdateAsync(UpdateUserRequest request, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageUsers);
        if (string.IsNullOrWhiteSpace(request.FullName)) throw new BusinessRuleException("Full name is required.");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.Id, ct) ?? throw new NotFoundException("User", request.Id);

        await EnsureNotLastActiveAdminAsync(db, user, request.Role, request.IsActive, ct);

        user.FullName = request.FullName.Trim();
        user.Role = request.Role;
        user.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("UpdateUser", user.Username, $"Role={user.Role}; Active={user.IsActive}", true, ct);

        if (_currentUser.User?.Id == user.Id) _currentUser.SetUser(AuthService.ToDto(user));
        return AuthService.ToDto(user);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageUsers);
        var policy = new PasswordPolicyValidator(_options).Validate(request.NewPassword);
        if (!policy.IsValid) throw new BusinessRuleException(policy.Errors[0].ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct) ?? throw new NotFoundException("User", request.UserId);
        user.PasswordHash = _hasher.Hash(request.NewPassword);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.PasswordChangedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("ResetPassword", user.Username, null, true, ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageUsers);
        if (_currentUser.User?.Id == id) throw new BusinessRuleException("You cannot delete your own account.");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
        await EnsureNotLastActiveAdminAsync(db, user, UserRole.Viewer, false, ct);
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("DeleteUser", user.Username, null, true, ct);
    }

    public async Task UnlockAsync(int id, CancellationToken ct = default)
    {
        _currentUser.Demand(Permission.ManageUsers);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
        user.LockoutEndUtc = null;
        user.FailedLoginCount = 0;
        await db.SaveChangesAsync(ct);
        await _audit.RecordAsync("UnlockUser", user.Username, null, true, ct);
    }

    private static async Task EnsureNotLastActiveAdminAsync(AppDbContext db, User user, UserRole newRole, bool newActive, CancellationToken ct)
    {
        var losesAdmin = user.Role == UserRole.Admin && user.IsActive && (newRole != UserRole.Admin || !newActive);
        if (!losesAdmin) return;
        var otherAdmins = await db.Users.CountAsync(u => u.Id != user.Id && u.Role == UserRole.Admin && u.IsActive, ct);
        if (otherAdmins == 0)
            throw new BusinessRuleException("At least one active administrator must remain.");
    }
}

public sealed class AuditService : IAuditService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IDbContextFactory<AppDbContext> factory, ICurrentUserService currentUser, ILogger<AuditService> logger)
    {
        _factory = factory;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task RecordAsync(string action, string? target = null, string? details = null, bool success = true, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            db.AuditLogs.Add(new AuditLog
            {
                Username = _currentUser.Username,
                Action = action,
                Target = target,
                Details = details,
                Success = success,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Auditing must never break the primary operation.
            _logger.LogError(ex, "Failed to write audit record {Action}/{Target}.", action, target);
        }
    }

    public async Task<IReadOnlyList<AuditLog>> GetRecentAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.TimestampUtc).Take(take).ToListAsync(ct);
    }
}
