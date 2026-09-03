using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WTT.Application.Interfaces;

namespace WTT.UI.ViewModels;

/// <summary>
/// MainViewModel: Navigation shell ka brain — sidebar navigation, user info, aur logout ka kaam.
/// </summary>
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

    public MainViewModel(IAuthService authService, IServiceProvider serviceProvider, Services.SessionManager sessionManager)
    {
        _authService = authService;
        _serviceProvider = serviceProvider;
        _sessionManager = sessionManager;

        // Set current user info from auth service
        if (_authService.CurrentUser != null)
        {
            CurrentUsername = _authService.CurrentUser.Username;
            CurrentUserRole = _authService.CurrentUser.Role.ToString();
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
        var loginWindow = _serviceProvider.GetRequiredService<Views.LoginWindow>();
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
