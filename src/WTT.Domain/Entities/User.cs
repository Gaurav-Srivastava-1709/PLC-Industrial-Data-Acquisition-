using WTT.Domain.Common;
using WTT.Domain.Enums;

namespace WTT.Domain.Entities;

public class User : AuditableEntity
{
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;

    /// <summary>BCrypt hash. Never expose outside the auth service.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Viewer;
    public bool IsActive { get; set; } = true;

    /// <summary>Forces a password change at next login (new accounts, admin resets).</summary>
    public bool MustChangePassword { get; set; } = true;

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? PasswordChangedAtUtc { get; set; }

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc.HasValue && LockoutEndUtc.Value > nowUtc;
}
