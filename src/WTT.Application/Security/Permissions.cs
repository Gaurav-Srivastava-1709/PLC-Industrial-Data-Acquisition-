using WTT.Domain.Enums;

namespace WTT.Application.Security;

/// <summary>Fine-grained permissions. Roles are mapped to permission sets in <see cref="RolePermissions"/>.</summary>
public enum Permission
{
    ViewDashboard,
    ViewLiveData,
    ViewDevices,
    ManageDevices,
    ManageTags,
    ControlCapture,
    WriteToPlc,
    ViewTransfer,
    ManageTransfer,
    ManageUsers,
    ManageSettings,
    ViewLogs,
}

public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<UserRole, IReadOnlySet<Permission>> Map =
        new Dictionary<UserRole, IReadOnlySet<Permission>>
        {
            [UserRole.Viewer] = new HashSet<Permission>
            {
                Permission.ViewDashboard,
                Permission.ViewLiveData,
                Permission.ViewDevices,
                Permission.ViewTransfer,
            },
            [UserRole.Operator] = new HashSet<Permission>
            {
                Permission.ViewDashboard,
                Permission.ViewLiveData,
                Permission.ViewDevices,
                Permission.ViewTransfer,
                Permission.ControlCapture,
                Permission.ViewLogs,
            },
            [UserRole.Admin] = new HashSet<Permission>(Enum.GetValues<Permission>()),
        };

    public static bool Has(UserRole role, Permission permission) =>
        Map.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlySet<Permission> For(UserRole role) =>
        Map.TryGetValue(role, out var set) ? set : new HashSet<Permission>();
}
