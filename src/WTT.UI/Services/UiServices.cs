using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using WTT.Application.Interfaces;
using WTT.Domain.Enums;

namespace WTT.UI.Services;

/// <summary>User-facing dialogs. ViewModels never touch MessageBox directly.</summary>
public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);
    Task ShowInfoAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", bool destructive = false);
    void Toast(string title, string message, ToastKind kind = ToastKind.Info);
}

public enum ToastKind { Info, Success, Warning, Error }

public sealed class DialogService : IDialogService
{
    private readonly IContentDialogService _dialogs;
    private readonly ISnackbarService _snackbar;

    public DialogService(IContentDialogService dialogs, ISnackbarService snackbar)
    {
        _dialogs = dialogs;
        _snackbar = snackbar;
    }

    public async Task ShowErrorAsync(string title, string message) => await ShowAsync(title, message, "OK", null, ControlAppearance.Danger);
    public async Task ShowInfoAsync(string title, string message) => await ShowAsync(title, message, "OK", null, ControlAppearance.Primary);

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", bool destructive = false)
    {
        var result = await ShowAsync(title, message, confirmText, "Cancel", destructive ? ControlAppearance.Danger : ControlAppearance.Primary);
        return result == ContentDialogResult.Primary;
    }

    public void Toast(string title, string message, ToastKind kind = ToastKind.Info)
    {
        var (appearance, icon) = kind switch
        {
            ToastKind.Success => (ControlAppearance.Success, SymbolRegular.CheckmarkCircle24),
            ToastKind.Warning => (ControlAppearance.Caution, SymbolRegular.Warning24),
            ToastKind.Error => (ControlAppearance.Danger, SymbolRegular.ErrorCircle24),
            _ => (ControlAppearance.Secondary, SymbolRegular.Info24),
        };
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try { _snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(kind == ToastKind.Error ? 8 : 4)); }
            catch (Exception) { /* no presenter yet (e.g. login screen) */ }
        });
    }

    private async Task<ContentDialogResult> ShowAsync(string title, string message, string primary, string? close, ControlAppearance appearance)
    {
        try
        {
            return await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primary,
                CloseButtonText = close ?? string.Empty,
            });
        }
        catch (Exception)
        {
            // Dialog host not available (e.g. before MainWindow) — fall back to a classic box.
            var buttons = close is null ? System.Windows.MessageBoxButton.OK : System.Windows.MessageBoxButton.OKCancel;
            var image = appearance == ControlAppearance.Danger ? System.Windows.MessageBoxImage.Error : System.Windows.MessageBoxImage.Information;
            var r = System.Windows.MessageBox.Show(message, title, buttons, image);
            return r == System.Windows.MessageBoxResult.OK ? ContentDialogResult.Primary : ContentDialogResult.None;
        }
    }
}

/// <summary>Cross-page navigation state (which device is being configured).</summary>
public sealed class NavigationContext
{
    public int? SelectedDeviceId { get; set; }
}

/// <summary>Persists and applies the theme.</summary>
public sealed class ThemeManager
{
    private readonly IAppSettingsService _settings;
    public ThemeManager(IAppSettingsService settings) => _settings = settings;

    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public async Task LoadAsync()
    {
        var saved = await _settings.GetAsync(AppSettingKeys.Theme);
        Current = Enum.TryParse<AppTheme>(saved, out var t) ? t : AppTheme.Dark;
        Apply(Current);
    }

    public async Task SetAsync(AppTheme theme)
    {
        Current = theme;
        Apply(theme);
        await _settings.SetAsync(AppSettingKeys.Theme, theme.ToString());
    }

    // WindowBackdropType.None kyunki saari windows explicitly None use karti hain.
    // Startup sequence ensure karta hai ki pehli baar ye call hone par koi FluentWindow
    // open nahi hoti — sirf plain StartupWindow hota hai jo theme change se affect nahi hota.
    private static void Apply(AppTheme theme) =>
        ApplicationThemeManager.Apply(theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, true);
}

/// <summary>Auto-logout after inactivity. Timeout comes from runtime settings (Admin configurable).</summary>
public sealed class SessionManager : IDisposable
{
    private readonly IRuntimeSettings _runtime;
    private readonly ILogger<SessionManager> _logger;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _armed;

    public event EventHandler? TimedOut;

    public SessionManager(IRuntimeSettings runtime, ILogger<SessionManager> logger)
    {
        _runtime = runtime;
        _logger = logger;
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        _lastActivity = DateTime.UtcNow;
        if (!_armed)
        {
            System.Windows.Input.InputManager.Current.PreProcessInput += OnInput;
            _armed = true;
        }
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_armed)
        {
            System.Windows.Input.InputManager.Current.PreProcessInput -= OnInput;
            _armed = false;
        }
    }

    private void OnInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyboardEventArgs)
            _lastActivity = DateTime.UtcNow;
    }

    private void Check()
    {
        var idle = DateTime.UtcNow - _lastActivity;
        if (idle.TotalMinutes >= _runtime.SessionTimeoutMinutes)
        {
            _logger.LogInformation("Session idle for {Minutes:F0} min — auto logout.", idle.TotalMinutes);
            Stop();
            TimedOut?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>Switches between the login window and the main shell.</summary>
public sealed class ShellNavigator
{
    private readonly IServiceProvider _services;
    private readonly IAuthService _auth;
    private readonly SessionManager _session;
    private readonly ILogger<ShellNavigator> _logger;

    public ShellNavigator(IServiceProvider services, IAuthService auth, SessionManager session, ILogger<ShellNavigator> logger)
    {
        _services = services;
        _auth = auth;
        _session = session;
        _logger = logger;
        _session.TimedOut += async (_, _) => await LogoutAsync("Session timed out due to inactivity.");
    }

    public Views.Windows.LoginWindow ShowLogin(string? message = null)
    {
        var login = _services.GetRequiredService<Views.Windows.LoginWindow>();
        if (message is not null) login.ViewModel.InfoMessage = message;
        login.Show();
        CloseAllExcept(login);
        return login;
    }

    public void ShowMain()
    {
        var main = _services.GetRequiredService<Views.Windows.MainWindow>();
        main.Show();
        CloseAllExcept(main);
        _session.Start();
    }

    public async Task LogoutAsync(string? message = null)
    {
        _session.Stop();
        await _auth.LogoutAsync();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ShowLogin(message));
    }

    private static void CloseAllExcept(Window keep)
    {
        foreach (var w in System.Windows.Application.Current.Windows.OfType<Window>().Where(w => !ReferenceEquals(w, keep)).ToList())
        {
            try { w.Close(); } catch (Exception) { /* ignore */ }
        }
    }
}
