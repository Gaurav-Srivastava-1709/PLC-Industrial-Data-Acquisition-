using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ICurrentUserService _currentUser;
    private readonly ShellNavigator _shell;
    private readonly Func<ChangePasswordViewModel> _changePasswordFactory;
    private readonly INavigationService _navigation;
    private readonly IAlarmService _alarmService;
    private readonly ISnackbarService _snackbarService;
    private readonly System.Windows.Threading.DispatcherTimer _syncTimer;

    public MainViewModel(ICurrentUserService currentUser, ShellNavigator shell, Func<ChangePasswordViewModel> changePasswordFactory, INavigationService navigation,
        IAlarmService alarmService, ISnackbarService snackbarService,
        IDialogService dialogs, ILogger<MainViewModel> logger) : base(dialogs, logger)
    {
        _currentUser = currentUser;
        _shell = shell;
        _changePasswordFactory = changePasswordFactory;
        _navigation = navigation;
        _alarmService = alarmService;
        _snackbarService = snackbarService;
        
        _currentUser.Changed += (_, _) => OnUi(RefreshUser);
        
        _alarmService.AlarmTriggered += OnAlarmTriggered;
        _alarmService.AlarmCleared += OnAlarmCleared;
        _alarmService.AlarmCountChanged += (_, _) => _ = RefreshAlarmCountAsync();
        
        RefreshUser();
        _ = RefreshAlarmCountAsync();

        // UI Cross-Process Sync (Polls DB for new alarms created by the background service)
        _syncTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _syncTimer.Tick += (_, _) => _ = _alarmService.SyncCrossProcessAlarmsAsync(CancellationToken.None);
        _syncTimer.Start();
    }

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _roleName = string.Empty;

    public bool CanViewDevices => _currentUser.HasPermission(Permission.ViewDevices);
    public bool CanViewLiveData => _currentUser.HasPermission(Permission.ViewLiveData);
    public bool CanViewTransfer => _currentUser.HasPermission(Permission.ViewTransfer);
    public bool CanManageUsers => _currentUser.HasPermission(Permission.ManageUsers);
    public bool CanViewLogs => _currentUser.HasPermission(Permission.ViewLogs);
    public bool CanManageSettings => _currentUser.HasPermission(Permission.ManageSettings);

    public event EventHandler<ChangePasswordViewModel>? ChangePasswordRequested;

    private void RefreshUser()
    {
        var u = _currentUser.User;
        DisplayName = u is null ? string.Empty : (string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName);
        RoleName = u?.Role.ToString() ?? string.Empty;
        OnPropertyChanged(nameof(CanViewDevices));
        OnPropertyChanged(nameof(CanViewLiveData));
        OnPropertyChanged(nameof(CanViewTransfer));
        OnPropertyChanged(nameof(CanManageUsers));
        OnPropertyChanged(nameof(CanViewLogs));
        OnPropertyChanged(nameof(CanManageSettings));
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (await Dialogs.ConfirmAsync("Sign out", "Do you want to sign out?", "Sign out"))
            await _shell.LogoutAsync();
    }

    [RelayCommand]
    private void OpenDashboard() => _navigation.Navigate(typeof(Views.Pages.DashboardPage));

    [RelayCommand]
    private void ChangePassword()
    {
        var vm = _changePasswordFactory();
        vm.Initialize(_currentUser.User!.Id, forced: false);
        ChangePasswordRequested?.Invoke(this, vm);
    }

    [ObservableProperty] private int _activeAlarmCount;
    [ObservableProperty] private bool _isBeepEnabled = true;
    public bool HasActiveAlarms => ActiveAlarmCount > 0;

    private async Task RefreshAlarmCountAsync()
    {
        var count = await _alarmService.GetActiveAlarmCountAsync(CancellationToken.None);
        OnUi(() => { ActiveAlarmCount = count; OnPropertyChanged(nameof(HasActiveAlarms)); });
        try { var beep = await _alarmService.GetBeepEnabledAsync(CancellationToken.None); OnUi(() => IsBeepEnabled = beep); } catch { }
    }

    partial void OnIsBeepEnabledChanged(bool value)
    {
        _ = _alarmService.SetBeepEnabledAsync(value, CancellationToken.None);
        Dialogs.Toast("Alarms", value ? "Beep ON" : "Beep Silent", ToastKind.Info);
    }

    [RelayCommand]
    private void ToggleBeep() => IsBeepEnabled = !IsBeepEnabled;

    [RelayCommand]
    private void OpenAlarms() => _navigation.Navigate(typeof(Views.Pages.AlarmsPage));

    private void OnAlarmTriggered(object? sender, WTT.Domain.ValueObjects.AlarmTriggeredMessage msg)
    {
        _ = RefreshAlarmCountAsync();
        // Beep is handled by InAppAlarmNotifier (respects IsBeepEnabled).
        OnUi(() =>
        {
            _snackbarService.Show(
                "Alarm Triggered",
                $"[{msg.Priority}] {msg.DeviceName}/{msg.TagName} = {msg.TriggerValue:0.###} {msg.EngineeringUnit}",
                Wpf.Ui.Controls.ControlAppearance.Danger,
                new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Warning24),
                TimeSpan.FromSeconds(5)
            );
        });
    }

    private void OnAlarmCleared(object? sender, WTT.Domain.ValueObjects.AlarmClearedMessage msg)
    {
        _ = RefreshAlarmCountAsync();
    }
}
