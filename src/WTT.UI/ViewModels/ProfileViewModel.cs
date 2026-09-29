using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Enums;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

/// <summary>My own profile. Every role can open it (no permission demand).</summary>
public partial class ProfileViewModel : ViewModelBase
{
    private readonly ICurrentUserService _currentUser;
    private readonly ShellNavigator _shell;
    private readonly Func<ChangePasswordViewModel> _changePasswordFactory;

    public ProfileViewModel(ICurrentUserService currentUser, ShellNavigator shell,
        Func<ChangePasswordViewModel> changePasswordFactory,
        IDialogService dialogs, ILogger<ProfileViewModel> logger) : base(dialogs, logger)
    {
        _currentUser = currentUser;
        _shell = shell;
        _changePasswordFactory = changePasswordFactory;
        _currentUser.Changed += (_, _) => OnUi(RefreshUser);
        RefreshUser();
    }

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _roleName = string.Empty;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _mustChangePassword;
    [ObservableProperty] private bool _isLockedOut;
    [ObservableProperty] private DateTime? _lastLoginAtUtc;
    [ObservableProperty] private DateTime? _lockoutEndUtc;
    [ObservableProperty] private DateTime _createdAtUtc;
    [ObservableProperty] private List<string> _myPermissions = new();

    public event EventHandler<ChangePasswordViewModel>? ChangePasswordRequested;

    private void RefreshUser()
    {
        var u = _currentUser.User;
        if (u is null)
        {
            DisplayName = string.Empty;
            Username = string.Empty;
            RoleName = string.Empty;
            MyPermissions = new();
            return;
        }
        DisplayName = string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName;
        Username = u.Username;
        RoleName = u.Role.ToString();
        IsActive = u.IsActive;
        MustChangePassword = u.MustChangePassword;
        LastLoginAtUtc = u.LastLoginAtUtc;
        LockoutEndUtc = u.LockoutEndUtc;
        IsLockedOut = u.LockoutEndUtc.HasValue && u.LockoutEndUtc.Value > DateTime.UtcNow;
        CreatedAtUtc = u.CreatedAtUtc;
        MyPermissions = RolePermissions.For(u.Role).Select(FriendlyName).OrderBy(x => x).ToList();
    }

    private static string FriendlyName(Permission p) => p switch
    {
        Permission.ViewDashboard => "Dashboard",
        Permission.ViewLiveData => "Live Data & Trends",
        Permission.ViewDevices => "View Devices",
        Permission.ManageDevices => "Manage Devices",
        Permission.ManageTags => "Manage Tags",
        Permission.ControlCapture => "Start / Stop Capture & Transfer",
        Permission.WriteToPlc => "Write Values to PLC",
        Permission.ViewTransfer => "View Data Transfer",
        Permission.ManageTransfer => "Manage Transfer Servers",
        Permission.ManageUsers => "Manage Users",
        Permission.ManageSettings => "Manage Settings",
        Permission.ViewLogs => "View Logs",
        Permission.AcknowledgeAlarm => "Acknowledge Alarms",
        _ => p.ToString(),
    };

    [RelayCommand]
    private void ChangePassword()
    {
        if (_currentUser.User is null) return;
        var vm = _changePasswordFactory();
        vm.Initialize(_currentUser.User.Id, forced: false);
        ChangePasswordRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (await Dialogs.ConfirmAsync("Sign out", "Do you want to sign out?", "Sign out"))
            await _shell.LogoutAsync();
    }

    public override Task OnNavigatedToAsync()
    {
        RefreshUser();
        return Task.CompletedTask;
    }
}
