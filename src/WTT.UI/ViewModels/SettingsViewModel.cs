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

    public SettingsViewModel(IAppSettingsService settings, IRuntimeSettings runtime, ThemeManager theme, ICurrentUserService currentUser, IAuditService audit,
        IDbContextFactory<AppDbContext> dbFactory, IDialogService dialogs, ILogger<SettingsViewModel> logger) : base(dialogs, logger)
    {
        _settings = settings; _runtime = runtime; _theme = theme; _currentUser = currentUser; _audit = audit; _dbFactory = dbFactory;
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

    public bool CanManage => _currentUser.HasPermission(Permission.ManageSettings);

    public override async Task OnNavigatedToAsync()
    {
        _currentUser.Demand(Permission.ManageSettings);
        await RunAsync(async () =>
        {
            IsDarkMode = _theme.Current == AppTheme.Dark;
            UseMockPlc = _runtime.UseMockPlc;
            SessionTimeoutMinutes = _runtime.SessionTimeoutMinutes;
            TransferredRetentionDays = _runtime.TransferredRetentionDays;
            TransferIntervalSeconds = _runtime.TransferIntervalSeconds;
            await LoadDatabaseInfoAsync();
            Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        });
    }

    partial void OnIsDarkModeChanged(bool value) => _ = _theme.SetAsync(value ? AppTheme.Dark : AppTheme.Light);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await RunAsync(async () =>
        {
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
