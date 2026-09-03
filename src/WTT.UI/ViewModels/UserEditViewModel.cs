using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using Wpf.Ui.Controls;

namespace WTT.UI.ViewModels;

public partial class UserEditViewModel : ObservableObject
{
    private readonly IUserService _userService;
    private readonly WTT.Application.Validators.UserValidator _validator;

    [ObservableProperty]
    private string _windowTitle = "Add New User";

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _fullName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private UserRole _selectedRole = UserRole.Viewer;

    [ObservableProperty]
    private bool _isActive = true;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasErrors;

    private User? _editingUser;
    
    // Dropdown items
    public ObservableCollection<UserRole> AvailableRoles { get; } = new(Enum.GetValues<UserRole>());

    public Action? CloseAction { get; set; }
    public bool DialogResult { get; private set; }

    public UserEditViewModel(IUserService userService)
    {
        _userService = userService;
        _validator = new WTT.Application.Validators.UserValidator();
    }

    public void SetUserForEdit(User user)
    {
        _editingUser = user;
        IsEditMode = true;
        WindowTitle = $"Edit User: {user.Username}";
        
        Username = user.Username;
        FullName = user.FullName;
        SelectedRole = user.Role;
        IsActive = user.IsActive;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        HasErrors = false;

        // Basic password validation for new users
        if (!IsEditMode && (string.IsNullOrWhiteSpace(Password) || Password.Length < 6))
        {
            ErrorMessage = "Password must be at least 6 characters long.";
            HasErrors = true;
            return;
        }

        var userToSave = _editingUser ?? new User();
        userToSave.Username = Username;
        userToSave.FullName = FullName;
        userToSave.Role = SelectedRole;
        userToSave.IsActive = IsActive;

        // FluentValidation Check
        var validationResult = await _validator.ValidateAsync(userToSave);
        if (!validationResult.IsValid)
        {
            ErrorMessage = string.Join("\n", validationResult.Errors.Select(e => e.ErrorMessage));
            HasErrors = true;
            return;
        }

        try
        {
            if (IsEditMode)
            {
                await _userService.UpdateUserAsync(userToSave);
                
                // If password is provided during edit, reset it
                if (!string.IsNullOrWhiteSpace(Password))
                {
                    await _userService.AdminResetPasswordAsync(userToSave.Id, Password);
                }
            }
            else
            {
                await _userService.CreateUserAsync(userToSave, Password);
            }

            DialogResult = true;
            CloseAction?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasErrors = true;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseAction?.Invoke();
    }
}
