using WTT.Application.DTOs;
using WTT.Application.Security;
using WTT.Domain.Enums;

namespace WTT.Application.Interfaces;

public interface IAuthService
{
    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
}

public interface IInitialAdminEnrollmentService
{
    Task<bool> IsRequiredAsync(CancellationToken ct = default);
    Task<UserDto> EnrollAsync(InitialAdminRequest request, CancellationToken ct = default);
}

/// <summary>Holds the currently signed-in user for the process. Singleton.</summary>
public interface ICurrentUserService
{
    UserDto? User { get; }
    bool IsAuthenticated { get; }
    string Username { get; }
    bool HasPermission(Permission permission);
    bool IsInRole(UserRole role);

    /// <summary>Throws <see cref="Domain.Exceptions.AuthorizationException"/> if the permission is missing.</summary>
    void Demand(Permission permission);

    event EventHandler? Changed;

    void SetUser(UserDto? user);
}

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct = default);
    Task<UserDto?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserDto> UpdateAsync(UpdateUserRequest request, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task UnlockAsync(int id, CancellationToken ct = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

/// <summary>Encrypts secrets at rest (DPAPI on Windows).</summary>
public interface ISecureStorage
{
    string? Protect(string? plainText);
    string? Unprotect(string? protectedText);
}

public interface IAuditService
{
    Task RecordAsync(string action, string? target = null, string? details = null, bool success = true, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Entities.AuditLog>> GetRecentAsync(int take = 200, CancellationToken ct = default);
}
