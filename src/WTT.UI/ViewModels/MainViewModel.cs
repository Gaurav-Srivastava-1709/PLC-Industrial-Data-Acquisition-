using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using System.Windows;
using WTT.Application.Interfaces;
using WTT.Infrastructure.Services;
using Wpf.Ui;

namespace WTT.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAuthService _authService;
    private readonly IServiceProvider _serviceProvider;
    private readonly Services.SessionManager _sessionManager;

    // Current logged-in user ki info dikhane ke liye
    [ObservableProperty]
    private string _currentUsername = "admin";

    [ObservableProperty]
    private string _currentUserRole = "Administrator";

    // Dark Mode toggle state
    [ObservableProperty]
    private bool _isDarkMode = true;

    // RBAC: Role-based Visibility Properties
    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private bool _isOperatorOrAbove;

    public MainViewModel(IAuthService authService, IServiceProvider serviceProvider, Services.SessionManager sessionManager)
    {
        _authService = authService;
        _serviceProvider = serviceProvider;
        _sessionManager = sessionManager;

        UpdateUserContext();
    }

    private void UpdateUserContext()
    {
        if (_authService.CurrentUser != null)
        {
            CurrentUsername = _authService.CurrentUser.Username;
            CurrentUserRole = _authService.CurrentUser.Role.ToString();
            
            // Set visibility flags based on Role
            IsAdmin = _authService.CurrentUser.Role == WTT.Domain.Entities.UserRole.Admin;
            IsOperatorOrAbove = _authService.CurrentUser.Role == WTT.Domain.Entities.UserRole.Admin || 
                                _authService.CurrentUser.Role == WTT.Domain.Entities.UserRole.Operator;
        }
    }

    /// <summary>
    /// Dark/Light mode toggle karne ka command
    /// </summary>
    partial void OnIsDarkModeChanged(bool value)
    {
        if (value)
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        else
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
    }

    /// <summary>
    /// Logout button ka command — session band karo, login screen dikhao
    /// </summary>
    [RelayCommand]
    private async Task LogoutAsync()
    {
        _sessionManager.StopSession();
        await _authService.LogoutAsync();

        // Login window kholo
        var loginWindow = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Views.LoginWindow>(_serviceProvider);
        loginWindow.Show();

        // Saari MainWindows band karo
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (window is MainWindow)
            {
                window.Close();
                break;
            }
        }
    }
}
