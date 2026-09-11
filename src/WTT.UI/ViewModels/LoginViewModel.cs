using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private readonly ShellNavigator _shell;
    private readonly Func<ChangePasswordViewModel> _changePasswordFactory;

    public LoginViewModel(IAuthService auth, ShellNavigator shell, Func<ChangePasswordViewModel> changePasswordFactory, IDialogService dialogs, ILogger<LoginViewModel> logger)
        : base(dialogs, logger)
    {
        _auth = auth;
        _shell = shell;
        _changePasswordFactory = changePasswordFactory;
    }

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string? _infoMessage;
    [ObservableProperty] private bool _canSignIn;

    /// <summary>Set by the view when a forced password change must be shown.</summary>
    public event EventHandler<ChangePasswordViewModel>? PasswordChangeRequired;

    [RelayCommand]
    private async Task LoginAsync()
    {
        await RunAsync(async () =>
        {
            var result = await _auth.LoginAsync(new LoginRequest(Username, Password));
            Password = string.Empty;
            if (!result.Succeeded)
            {
                ErrorMessage = result.Error;
                return;
            }

            if (result.MustChangePassword)
            {
                var vm = _changePasswordFactory();
                vm.Initialize(result.User!.Id, forced: true);
                PasswordChangeRequired?.Invoke(this, vm);
                return; // the view continues once the dialog closes
            }

            _shell.ShowMain();
        }, inlineErrors: true);
    }

    /// <summary>Called by the view after a forced password change dialog closes.</summary>
    public async Task CompleteForcedChangeAsync(bool changed)
    {
        if (changed)
        {
            _shell.ShowMain();
            return;
        }
        await _auth.LogoutAsync();
        ErrorMessage = "You must change the temporary password before using the application.";
    }
}

public partial class ChangePasswordViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private int _userId;

    public ChangePasswordViewModel(IAuthService auth, IDialogService dialogs, ILogger<ChangePasswordViewModel> logger) : base(dialogs, logger)
    {
        _auth = auth;
    }

    [ObservableProperty] private string _currentPassword = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _isForced;
    [ObservableProperty] private bool _succeeded;

    public string Title => IsForced ? "Set a new password" : "Change password";
    public string Explanation => IsForced
        ? "Your password is temporary. Choose a new one to continue. Minimum 8 characters, upper and lower case letters and a digit."
        : "Enter your current password and choose a new one. Minimum 8 characters, upper and lower case letters and a digit.";

    public event EventHandler? Completed;

    public void Initialize(int userId, bool forced)
    {
        _userId = userId;
        IsForced = forced;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Explanation));
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The new passwords do not match.";
            return;
        }
        var ok = await RunAsync(() => _auth.ChangePasswordAsync(new ChangePasswordRequest(_userId, CurrentPassword, NewPassword)), inlineErrors: true);
        if (ok)
        {
            Succeeded = true;
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Succeeded = false;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
