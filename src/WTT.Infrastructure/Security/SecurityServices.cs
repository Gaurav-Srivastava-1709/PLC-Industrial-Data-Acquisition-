using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Application.Security;
using WTT.Domain.Enums;
using WTT.Domain.Exceptions;

namespace WTT.Infrastructure.Security;

/// <summary>BCrypt password hashing with configurable work factor.</summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private readonly int _workFactor;

    public BcryptPasswordHasher(IOptions<SecurityOptions> options)
    {
        _workFactor = Math.Clamp(options.Value.BcryptWorkFactor, 10, 15);
    }

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, _workFactor);

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// Windows DPAPI protection (LocalMachine scope so that the same value can be read by the
/// interactive user and by a service account on this machine). Output is base64 with a prefix
/// so we can detect already-protected values.
/// </summary>
public sealed class DpapiSecureStorage : ISecureStorage
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WTT.SecureStorage.v1");
    private readonly ILogger<DpapiSecureStorage> _logger;

    public DpapiSecureStorage(ILogger<DpapiSecureStorage> logger) => _logger = logger;

    public string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        if (plainText.StartsWith(Prefix, StringComparison.Ordinal)) return plainText;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedText; // legacy / unprotected value
        try
        {
            var bytes = Convert.FromBase64String(protectedText[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine));
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt a stored secret. It was probably created on another machine.");
            throw new BusinessRuleException("A stored password could not be decrypted on this machine. Please re-enter it.");
        }
    }
}

/// <summary>Process-wide current user holder.</summary>
public sealed class CurrentUserService : ICurrentUserService
{
    private UserDto? _user;

    public UserDto? User => _user;
    public bool IsAuthenticated => _user is not null;
    public string Username => _user?.Username ?? "system";

    public event EventHandler? Changed;

    public bool HasPermission(Permission permission) =>
        _user is not null && _user.IsActive && RolePermissions.Has(_user.Role, permission);

    public bool IsInRole(UserRole role) => _user is not null && _user.Role == role;

    public void Demand(Permission permission)
    {
        if (!HasPermission(permission))
            throw new AuthorizationException($"You do not have permission to perform this action ({permission}).");
    }

    public void SetUser(UserDto? user)
    {
        _user = user;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
