using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ICurrentUserService _currentUser;
    private readonly ShellNavigator _shell;
    private readonly Func<ChangePasswordViewModel> _changePasswordFactory;

    public MainViewModel(ICurrentUserService currentUser, ShellNavigator shell, Func<ChangePasswordViewModel> changePasswordFactory,
        IDialogService dialogs, ILogger<MainViewModel> logger) : base(dialogs, logger)
    {
        _currentUser = currentUser;
        _shell = shell;
        _changePasswordFactory = changePasswordFactory;
        _currentUser.Changed += (_, _) => OnUi(RefreshUser);
        RefreshUser();
    }

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _roleName = string.Empty;

    public bool CanViewDevices => _currentUser.HasPermission(Permission.ViewDevices);
    public bool CanViewLiveData => _currentUser.HasPermission(Permission.ViewLiveData);
    public bool CanViewTransfer => _currentUser.HasPermission(Permission.ViewTransfer);
    public bool CanManageUsers => _currentUser.HasPermission(Permission.ManageUsers);
    public bool CanViewLogs => _currentUser.HasPermission(Permission.ViewLogs);
    public bool CanManageSettings => _currentUser.HasPermission(Permission.ManageSettings);

    public event EventHandler<ChangePasswordViewModel>? ChangePasswordRequested;

    private void RefreshUser()
    {
        var u = _currentUser.User;
        DisplayName = u is null ? string.Empty : (string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName);
        RoleName = u?.Role.ToString() ?? string.Empty;
        OnPropertyChanged(nameof(CanViewDevices));
        OnPropertyChanged(nameof(CanViewLiveData));
        OnPropertyChanged(nameof(CanViewTransfer));
        OnPropertyChanged(nameof(CanManageUsers));
        OnPropertyChanged(nameof(CanViewLogs));
        OnPropertyChanged(nameof(CanManageSettings));
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (await Dialogs.ConfirmAsync("Sign out", "Do you want to sign out?", "Sign out"))
            await _shell.LogoutAsync();
    }

    [RelayCommand]
    private void ChangePassword()
    {
        var vm = _changePasswordFactory();
        vm.Initialize(_currentUser.User!.Id, forced: false);
        ChangePasswordRequested?.Invoke(this, vm);
    }
}
