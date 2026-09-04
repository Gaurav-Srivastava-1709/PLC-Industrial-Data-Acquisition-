using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Domain.Enums;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class UsersViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private readonly ICurrentUserService _currentUser;
    private readonly Func<UserEditViewModel> _editFactory;

    public UsersViewModel(IUserService users, ICurrentUserService currentUser, Func<UserEditViewModel> editFactory, IDialogService dialogs, ILogger<UsersViewModel> logger) : base(dialogs, logger)
    {
        _users = users; _currentUser = currentUser; _editFactory = editFactory;
    }

    public ObservableCollection<UserDto> Users { get; } = new();
    [ObservableProperty] private UserDto? _selected;

    public event EventHandler<UserEditViewModel>? EditRequested;

    public override Task OnNavigatedToAsync() => LoadAsync();

    private async Task LoadAsync() => await RunAsync(async () =>
    {
        var list = await _users.GetAllAsync();
        Users.Clear(); foreach (var u in list) Users.Add(u);
    });

    [RelayCommand]
    private void Add()
    {
        var vm = _editFactory();
        vm.Initialize(null);
        vm.Saved += async (_, _) => await LoadAsync();
        EditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void Edit(UserDto? user)
    {
        user ??= Selected;
        if (user is null) return;
        var vm = _editFactory();
        vm.Initialize(user);
        vm.Saved += async (_, _) => await LoadAsync();
        EditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private void ResetPassword(UserDto? user)
    {
        user ??= Selected;
        if (user is null) return;
        var vm = _editFactory();
        vm.Initialize(user, resetPasswordMode: true);
        vm.Saved += async (_, _) => await LoadAsync();
        EditRequested?.Invoke(this, vm);
    }

    [RelayCommand]
    private async Task UnlockAsync(UserDto? user)
    {
        user ??= Selected;
        if (user is null) return;
        if (await RunAsync(() => _users.UnlockAsync(user.Id))) { Dialogs.Toast("Users", $"'{user.Username}' unlocked.", ToastKind.Success); await LoadAsync(); }
    }

    [RelayCommand]
    private async Task DeleteAsync(UserDto? user)
    {
        user ??= Selected;
        if (user is null) return;
        if (!await Dialogs.ConfirmAsync("Delete user", $"Delete user '{user.Username}'?", "Delete", destructive: true)) return;
        if (await RunAsync(() => _users.DeleteAsync(user.Id), "Cannot delete user")) await LoadAsync();
    }

    [RelayCommand] private Task RefreshAsync() => LoadAsync();
}

public partial class UserEditViewModel : ViewModelBase
{
    private readonly IUserService _users;
    private UserDto? _original;

    public UserEditViewModel(IUserService users, IDialogService dialogs, ILogger<UserEditViewModel> logger) : base(dialogs, logger) => _users = users;

    public event EventHandler? Saved;
    public event EventHandler? Closed;

    public IReadOnlyList<UserRole> Roles { get; } = Enum.GetValues<UserRole>();

    [ObservableProperty] private string _title = "Add user";
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private UserRole _role = UserRole.Viewer;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _isNew = true;
    [ObservableProperty] private bool _isResetMode;

    public bool ShowPassword => IsNew || IsResetMode;
    public bool ShowProfile => !IsResetMode;

    public void Initialize(UserDto? user, bool resetPasswordMode = false)
    {
        _original = user;
        IsNew = user is null;
        IsResetMode = resetPasswordMode;
        if (user is not null)
        {
            Title = resetPasswordMode ? $"Reset password — {user.Username}" : $"Edit user — {user.Username}";
            Username = user.Username; FullName = user.FullName; Role = user.Role; IsActive = user.IsActive;
        }
        OnPropertyChanged(nameof(ShowPassword)); OnPropertyChanged(nameof(ShowProfile));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (ShowPassword && Password != ConfirmPassword) { ErrorMessage = "Passwords do not match."; return; }
        var ok = await RunAsync(async () =>
        {
            if (IsNew) await _users.CreateAsync(new CreateUserRequest(Username, FullName, Password, Role));
            else if (IsResetMode) await _users.ResetPasswordAsync(new ResetPasswordRequest(_original!.Id, Password));
            else await _users.UpdateAsync(new UpdateUserRequest(_original!.Id, FullName, Role, IsActive));
            Dialogs.Toast("Users", IsResetMode ? "Password reset. The user must change it at next login." : $"'{Username}' saved.", ToastKind.Success);
        }, inlineErrors: true);
        if (ok) { Saved?.Invoke(this, EventArgs.Empty); Closed?.Invoke(this, EventArgs.Empty); }
    }

    [RelayCommand] private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}
