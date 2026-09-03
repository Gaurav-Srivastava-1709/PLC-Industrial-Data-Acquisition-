using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WTT.Application.Interfaces;

namespace WTT.UI.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _authService;
    private readonly IServiceProvider _serviceProvider;
    private readonly Services.SessionManager _sessionManager;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    [ObservableProperty]
    private bool _isBusy;

    public LoginViewModel(IAuthService authService, IServiceProvider serviceProvider, Services.SessionManager sessionManager)
    {
        _authService = authService;
        _serviceProvider = serviceProvider;
        _sessionManager = sessionManager;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Username and Password are required.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            var user = await _authService.AuthenticateAsync(Username, Password);
            if (user != null)
            {
                _sessionManager.StartSession();

                // Navigate to MainWindow on successful login
                var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                mainWindow?.Show();

                // Close current window (Login Window)
                foreach (Window window in App.Current.Windows)
                {
                    if (window is not MainWindow)
                    {
                        window.Close();
                    }
                }
            }
            else
            {
                ErrorMessage = "Invalid username or password.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
