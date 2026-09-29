using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Enums;
using WTT.Infrastructure.Persistence;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _settings;
    private readonly IRuntimeSettings _runtime;
    private readonly ThemeManager _theme;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ShellNavigator _shell;
    private readonly Func<ChangePasswordViewModel> _changePasswordFactory;

    public SettingsViewModel(IAppSettingsService settings, IRuntimeSettings runtime, ThemeManager theme, ICurrentUserService currentUser, IAuditService audit,
        IDbContextFactory<AppDbContext> dbFactory, ShellNavigator shell, Func<ChangePasswordViewModel> changePasswordFactory, IDialogService dialogs, ILogger<SettingsViewModel> logger) : base(dialogs, logger)
    {
        _settings = settings; _runtime = runtime; _theme = theme; _currentUser = currentUser; _audit = audit; _dbFactory = dbFactory;
        _shell = shell; _changePasswordFactory = changePasswordFactory;
        _currentUser.Changed += (_, _) => OnUi(RefreshUser);
        RefreshUser();
    }

    [ObservableProperty] private bool _isDarkMode = true;
    [ObservableProperty] private bool _useMockPlc;
    [ObservableProperty] private int _sessionTimeoutMinutes = 30;
    [ObservableProperty] private int _transferredRetentionDays = 7;
    [ObservableProperty] private int _transferIntervalSeconds = 5;
    [ObservableProperty] private string _databaseLocation = string.Empty;
    [ObservableProperty] private string _databaseSize = string.Empty;
    [ObservableProperty] private string _databaseEdition = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _roleName = string.Empty;

    public bool CanManage => _currentUser.HasPermission(Permission.ManageSettings);

    public event EventHandler<ChangePasswordViewModel>? ChangePasswordRequested;

    private void RefreshUser()
    {
        var u = _currentUser.User;
        DisplayName = u is null ? string.Empty : (string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName);
        RoleName = u?.Role.ToString() ?? string.Empty;
        OnPropertyChanged(nameof(CanManage));
    }

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

    public override async Task OnNavigatedToAsync()
    {
        RefreshUser();
        await RunAsync(async () =>
        {
            IsDarkMode = _theme.Current == AppTheme.Dark;
            // Non-admin users can open Settings for My Account; runtime values load for everyone
            // but Save is still gated by CanManage in UI + service layer.
            try
            {
                UseMockPlc = _runtime.UseMockPlc;
                SessionTimeoutMinutes = _runtime.SessionTimeoutMinutes;
                TransferredRetentionDays = _runtime.TransferredRetentionDays;
                TransferIntervalSeconds = _runtime.TransferIntervalSeconds;
            }
            catch { }
            try { await LoadDatabaseInfoAsync(); } catch { }
            Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        });
    }

    partial void OnIsDarkModeChanged(bool value) => _ = _theme.SetAsync(value ? AppTheme.Dark : AppTheme.Light);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await RunAsync(async () =>
        {
            _currentUser.Demand(Permission.ManageSettings);
            if (UseMockPlc != _runtime.UseMockPlc && UseMockPlc &&
                !await Dialogs.ConfirmAsync("Enable simulator?", "ALL devices will read simulated values instead of real controllers while the simulator is on. Data written to the historian during this time is fake.\n\nEnable anyway?", "Enable simulator", destructive: true))
            {
                UseMockPlc = false;
                return;
            }
            await _settings.SetAsync(AppSettingKeys.UseMockPlc, UseMockPlc.ToString());
            await _settings.SetAsync(AppSettingKeys.SessionTimeoutMinutes, Math.Clamp(SessionTimeoutMinutes, 1, 720).ToString());
            await _settings.SetAsync(AppSettingKeys.TransferredRetentionDays, Math.Clamp(TransferredRetentionDays, 1, 3650).ToString());
            await _settings.SetAsync(AppSettingKeys.TransferIntervalSeconds, Math.Clamp(TransferIntervalSeconds, 1, 3600).ToString());
            await _runtime.ReloadAsync();
            await _audit.RecordAsync("SaveSettings", null, $"Mock={UseMockPlc}; Session={SessionTimeoutMinutes}m; Retention={TransferredRetentionDays}d; TransferInterval={TransferIntervalSeconds}s");
            Dialogs.Toast("Settings", "Settings saved and applied.", ToastKind.Success);
        }, inlineErrors: true);
        _ = ok;
    }

    private async Task LoadDatabaseInfoAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(128)), DB_NAME(), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), (SELECT CAST(SUM(size) * 8.0 / 1024 AS decimal(18,1)) FROM sys.database_files)";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return;
        DatabaseLocation = $"{reader.GetString(0)} / {reader.GetString(1)}";
        DatabaseEdition = reader.GetString(2);
        DatabaseSize = $"{reader.GetDecimal(3):F1} MB allocated (10 GB Express limit)";
    }
}
