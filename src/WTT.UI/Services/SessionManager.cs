using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WTT.Application.Interfaces;

namespace WTT.UI.Services;

public class SessionManager
{
    private readonly DispatcherTimer _inactivityTimer;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _timeoutPeriod = TimeSpan.FromMinutes(15);
    private bool _isActiveSession;

    public SessionManager(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        _inactivityTimer = new DispatcherTimer
        {
            Interval = _timeoutPeriod
        };
        _inactivityTimer.Tick += OnInactivityTimeout;

        // Monitor application-wide input events
        InputManager.Current.PreProcessInput += OnActivity;
    }

    public void StartSession()
    {
        _isActiveSession = true;
        _inactivityTimer.Start();
    }

    public void StopSession()
    {
        _isActiveSession = false;
        _inactivityTimer.Stop();
    }

    private void OnActivity(object sender, PreProcessInputEventArgs e)
    {
        if (!_isActiveSession) return;

        // Reset the timer on any input (mouse move, key press, etc.)
        if (e.StagingItem.Input is MouseEventArgs || e.StagingItem.Input is KeyboardEventArgs || e.StagingItem.Input is StylusEventArgs)
        {
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }
    }

    private async void OnInactivityTimeout(object? sender, EventArgs e)
    {
        StopSession();

        // Perform logout
        var authService = _serviceProvider.GetRequiredService<IAuthService>();
        await authService.LogoutAsync();

        // Show login window and close main window
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var loginWindow = _serviceProvider.GetRequiredService<Views.LoginWindow>();
            loginWindow.Show();

            // Find and close the main window if open
            foreach (Window window in System.Windows.Application.Current.Windows)
            {
                if (window is MainWindow)
                {
                    window.Close();
                    break;
                }
            }
        });
    }
}
