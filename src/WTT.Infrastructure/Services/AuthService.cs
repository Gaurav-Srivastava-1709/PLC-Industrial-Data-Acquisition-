using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Validators;
using WTT.Domain.Entities;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    private const string GenericFailure = "Invalid username or password.";

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly SecurityOptions _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IDbContextFactory<AppDbContext> factory,
        IPasswordHasher hasher,
        ICurrentUserService currentUser,
        IAuditService audit,
        IOptions<SecurityOptions> options,
        ILogger<AuthService> logger)
    {
        _factory = factory;
        _hasher = hasher;
        _currentUser = currentUser;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var validation = new LoginRequestValidator().Validate(request);
        if (!validation.IsValid)
            return LoginResult.Fail(validation.Errors[0].ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var normalized = request.Username.Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        var now = DateTime.UtcNow;

        if (user is null)
        {
            // Constant-time-ish: still run a hash verification so timing does not leak user existence.
            _hasher.Verify(request.Password, "$2a$12$C6UzMDM.H6dfI/f/IKcEeO5bZ0sQvYq0gJ2OaI6IhZ9Yz3X0f9G2u");
            await _audit.RecordAsync("Login", request.Username, "Unknown user", success: false, ct);
            return LoginResult.Fail(GenericFailure);
        }

        if (!user.IsActive)
        {
            await _audit.RecordAsync("Login", user.Username, "Account disabled", success: false, ct);
            return LoginResult.Fail("This account is disabled. Contact an administrator.");
        }

        if (user.IsLockedOut(now))
        {
            var minutes = Math.Ceiling((user.LockoutEndUtc!.Value - now).TotalMinutes);
            await _audit.RecordAsync("Login", user.Username, "Account locked", success: false, ct);
            return LoginResult.Fail($"Account is locked. Try again in {minutes} minute(s).");
        }

        if (!_hasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _options.MaxFailedLogins)
            {
                user.LockoutEndUtc = now.AddMinutes(_options.LockoutMinutes);
                user.FailedLoginCount = 0;
                _logger.LogWarning("User {User} locked out after too many failed logins.", user.Username);
            }
            await db.SaveChangesAsync(ct);
            await _audit.RecordAsync("Login", user.Username, "Wrong password", success: false, ct);
            return LoginResult.Fail(GenericFailure);
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginAtUtc = now;
        await db.SaveChangesAsync(ct);

        var dto = ToDto(user);
        _currentUser.SetUser(dto);
        await _audit.RecordAsync("Login", user.Username, "Success", success: true, ct);
        _logger.LogInformation("User {User} logged in with role {Role}.", user.Username, user.Role);
        return LoginResult.Ok(dto);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (_currentUser.IsAuthenticated)
        {
            await _audit.RecordAsync("Logout", _currentUser.Username, null, true, ct);
            _logger.LogInformation("User {User} logged out.", _currentUser.Username);
        }
        _currentUser.SetUser(null);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        var policy = new PasswordPolicyValidator(_options).Validate(request.NewPassword);
        if (!policy.IsValid)
            throw new BusinessRuleException(policy.Errors[0].ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
                   ?? throw new NotFoundException("User", request.UserId);

        if (!_hasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            await _audit.RecordAsync("ChangePassword", user.Username, "Current password mismatch", false, ct);
            throw new AuthenticationException("The current password is incorrect.");
        }

        if (_hasher.Verify(request.NewPassword, user.PasswordHash))
            throw new BusinessRuleException("The new password must be different from the current password.");

        user.PasswordHash = _hasher.Hash(request.NewPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (_currentUser.User?.Id == user.Id)
            _currentUser.SetUser(ToDto(user));

        await _audit.RecordAsync("ChangePassword", user.Username, null, true, ct);
    }

    internal static UserDto ToDto(User u) => new(
        u.Id, u.Username, u.FullName, u.Role, u.IsActive, u.MustChangePassword,
        u.LastLoginAtUtc, u.LockoutEndUtc, u.CreatedAtUtc);
}
