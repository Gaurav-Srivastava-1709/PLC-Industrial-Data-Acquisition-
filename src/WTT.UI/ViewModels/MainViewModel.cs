using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ICurrentUserService _currentUser;
    private readonly ShellNavigator _shell;
    private readonly Func<ChangePasswordViewModel> _changePasswordFactory;
    private readonly INavigationService _navigation;
    private readonly IAlarmService _alarmService;
    private readonly ISnackbarService _snackbarService;
    private readonly ICaptureRuntimeStatusReader _runtimeStatus;
    private readonly IDataTransferController _transfer;
    private readonly System.Windows.Threading.DispatcherTimer _syncTimer;
    // App-wide monitors (shell lives for the whole session, unlike pages): dedup maps stop toast spam.
    private readonly Dictionary<int, (bool DownNotified, bool WasConnected)> _connNotify = new();
    private long _prevFailedBatches;
    private bool _transferBaselineSet;
    private int _tickCount;
    private int _monitorBusy;
    private bool _disposed;

    public MainViewModel(ICurrentUserService currentUser, ShellNavigator shell, Func<ChangePasswordViewModel> changePasswordFactory, INavigationService navigation,
        IAlarmService alarmService, ISnackbarService snackbarService, ICaptureRuntimeStatusReader runtimeStatus, IDataTransferController transfer,
        IDialogService dialogs, ILogger<MainViewModel> logger) : base(dialogs, logger)
    {
        _currentUser = currentUser;
        _shell = shell;
        _changePasswordFactory = changePasswordFactory;
        _navigation = navigation;
        _alarmService = alarmService;
        _snackbarService = snackbarService;
        _runtimeStatus = runtimeStatus;
        _transfer = transfer;
        
        _currentUser.Changed += (_, _) => OnUi(RefreshUser);
        
        _alarmService.AlarmTriggered += OnAlarmTriggered;
        _alarmService.AlarmCleared += OnAlarmCleared;
        _alarmService.AlarmCountChanged += (_, _) => _ = RefreshAlarmCountAsync();
        
        RefreshUser();
        _ = RefreshAlarmCountAsync();

        // UI Cross-Process Sync (Polls DB for new alarms created by the background service)
        // + app-wide device/transfer health monitor: fires on every page, not just Dashboard.
        _syncTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _syncTimer.Tick += (_, _) => _ = OnSyncTickAsync();
        _syncTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _syncTimer.Stop();
        _syncTimer.Tick -= (_, _) => _ = OnSyncTickAsync();

        _currentUser.Changed -= (_, _) => OnUi(RefreshUser);
        _alarmService.AlarmTriggered -= OnAlarmTriggered;
        _alarmService.AlarmCleared -= OnAlarmCleared;
        _alarmService.AlarmCountChanged -= (_, _) => _ = RefreshAlarmCountAsync();

        GC.SuppressFinalize(this);
    }

    // Single guarded tick: alarm sync every 2s, health monitors staggered so a slow DB never piles up.
    private async Task OnSyncTickAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _monitorBusy, 1) == 1) return;
        try
        {
            try { await _alarmService.SyncCrossProcessAlarmsAsync(CancellationToken.None); } catch (Exception ex) { Logger.LogDebug(ex, "Alarm sync tick failed."); }
            try { await CheckDeviceConnectionsAsync(); } catch (Exception ex) { Logger.LogDebug(ex, "Device health tick failed."); }
            if (++_tickCount % 5 == 0)
            {
                try { await CheckTransferHealthAsync(); } catch (Exception ex) { Logger.LogDebug(ex, "Transfer health tick failed."); }
            }
        }
        finally { System.Threading.Interlocked.Exchange(ref _monitorBusy, 0); }
    }

    // Industry: connection loss must toast on ANY page, once per down/up transition — never every tick.
    private async Task CheckDeviceConnectionsAsync()
    {
        var snapshot = await _runtimeStatus.GetSnapshotAsync();
        var seen = new HashSet<int>();
        foreach (var s in snapshot.Devices)
        {
            seen.Add(s.DeviceId);
            _connNotify.TryGetValue(s.DeviceId, out var prev);
            if (!s.IsRunning)
            {
                _connNotify[s.DeviceId] = (false, false);
                continue;
            }
            if (!s.IsConnected)
            {
                if (!prev.DownNotified)
                {
                    var err = string.IsNullOrWhiteSpace(s.LastError) ? "connection failed" : s.LastError;
                    if (err.Length > 140) err = err[..140] + "…";
                    var attempt = s.ReconnectAttempts > 0 ? $" (retry {s.ReconnectAttempts})" : string.Empty;
                    Dialogs.Toast("Device disconnected", $"{s.DeviceName}: {err}{attempt}", ToastKind.Warning);
                    _connNotify[s.DeviceId] = (true, false);
                }
            }
            else if (prev.DownNotified)
            {
                Dialogs.Toast("Device reconnected", $"{s.DeviceName}: connected.", ToastKind.Success);
                _connNotify[s.DeviceId] = (false, true);
            }
            else
            {
                _connNotify[s.DeviceId] = (false, true);
            }
        }
        // Workers that vanished (device disabled) must not hold stale down-flags.
        foreach (var id in _connNotify.Keys.Where(k => !seen.Contains(k)).ToList())
            _connNotify[id] = (false, false);
    }

    // Industry: a dead central server must shout, not just sit red on one page.
    // Toasts once when the daily failure count rises; recovers silently to avoid noise.
    private async Task CheckTransferHealthAsync()
    {
        var stats = await _transfer.GetStatisticsAsync();
        if (!_transferBaselineSet)
        {
            _prevFailedBatches = stats.FailedBatchesToday;
            _transferBaselineSet = true;
            return;
        }
        if (stats.FailedBatchesToday > _prevFailedBatches)
        {
            var fresh = stats.FailedBatchesToday - _prevFailedBatches;
            Dialogs.Toast("Data transfer failing",
                $"{fresh} batch(es) just failed ({stats.FailedBatchesToday} today) · {stats.PendingRecords:N0} records waiting. Check Data Transfer.",
                ToastKind.Warning);
        }
        _prevFailedBatches = stats.FailedBatchesToday;
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
    private void OpenSettings() => _navigation.Navigate(typeof(Views.Pages.SettingsPage));

    [RelayCommand]
    private void OpenProfile() => _navigation.Navigate(typeof(Views.Pages.ProfilePage));

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
