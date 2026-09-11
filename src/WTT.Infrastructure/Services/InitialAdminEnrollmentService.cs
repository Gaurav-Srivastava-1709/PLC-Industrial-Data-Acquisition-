using System.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Validators;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Persistence;

namespace WTT.Infrastructure.Services;

public sealed class InitialAdminEnrollmentService : IInitialAdminEnrollmentService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IPasswordHasher _hasher;
    private readonly SecurityOptions _options;

    public InitialAdminEnrollmentService(IDbContextFactory<AppDbContext> factory, IPasswordHasher hasher, IOptions<SecurityOptions> options)
    {
        _factory = factory;
        _hasher = hasher;
        _options = options.Value;
    }

    public async Task<bool> IsRequiredAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return !await db.Users.AnyAsync(ct);
    }

    public async Task<UserDto> EnrollAsync(InitialAdminRequest request, CancellationToken ct = default)
    {
        var create = new CreateUserRequest(request.Username.Trim(), request.FullName.Trim(), request.Password, UserRole.Admin);
        var validation = new CreateUserRequestValidator(_options).Validate(create);
        if (!validation.IsValid) throw new BusinessRuleException(validation.Errors[0].ErrorMessage);

        await using var strategyContext = await _factory.CreateDbContextAsync(ct);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await db.Users.AnyAsync(ct)) throw new BusinessRuleException("Initial administrator enrollment is already complete.");

            var user = new User
            {
                Username = create.Username,
                NormalizedUsername = create.Username.ToUpperInvariant(),
                FullName = create.FullName,
                PasswordHash = _hasher.Hash(create.Password),
                Role = UserRole.Admin,
                IsActive = true,
                MustChangePassword = false,
                PasswordChangedAtUtc = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AuthService.ToDto(user);
        });
    }
}
