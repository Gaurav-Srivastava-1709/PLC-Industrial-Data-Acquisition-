using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WTT.Application.DTOs;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;
using WTT.Domain.Enums;
using WTT.Domain.ValueObjects;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class AlarmsViewModel : ViewModelBase
{
    private readonly IAlarmService _alarmService;
    private readonly ICurrentUserService _currentUser;

    public AlarmsViewModel(IAlarmService alarmService, ICurrentUserService currentUser, IDialogService dialogs, ILogger<AlarmsViewModel> logger) : base(dialogs, logger)
    {
        _alarmService = alarmService;
        _currentUser = currentUser;

        _alarmService.AlarmTriggered += OnAlarmTriggered;
        _alarmService.AlarmCleared += OnAlarmCleared;
    }

    [ObservableProperty] private int _selectedTabIndex;
    
    // --- Active Alarms ---
    public ObservableCollection<AlarmEvent> ActiveAlarms { get; } = new();

    // --- Alarm History ---
    public ObservableCollection<AlarmEvent> HistoryAlarms { get; } = new();
    
    [ObservableProperty] private DateTime? _filterFromDate;
    [ObservableProperty] private DateTime? _filterToDate;
    [ObservableProperty] private AlarmPriority? _filterPriority;
    [ObservableProperty] private AlarmType? _filterType;
    [ObservableProperty] private AlarmState? _filterState;
    [ObservableProperty] private string _filterDeviceName = string.Empty;
    [ObservableProperty] private string _filterTagName = string.Empty;
    
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private int _pageSize = 50;

    public IReadOnlyList<AlarmPriority> Priorities { get; } = Enum.GetValues<AlarmPriority>();
    public IReadOnlyList<AlarmType> Types { get; } = Enum.GetValues<AlarmType>();
    public IReadOnlyList<AlarmState> States { get; } = Enum.GetValues<AlarmState>();

    // ---- Retention In-Out + Beep In-Out ----
    public IReadOnlyList<int> RetentionOptions { get; } = new[] { 7, 30, 90, 180, 365 };
    [ObservableProperty] private int _selectedRetentionDays = 90;
    [ObservableProperty] private int _customRetentionDays;
    [ObservableProperty] private bool _isBeepEnabled = true;

    partial void OnIsBeepEnabledChanged(bool value)
    {
        _ = RunAsync(async () => { await _alarmService.SetBeepEnabledAsync(value, CancellationToken.None); Dialogs.Toast("Alarms", value ? "Beep ON" : "Beep Silent", ToastKind.Info); });
    }

    public override async Task OnNavigatedToAsync()
    {
        await LoadActiveAlarmsAsync();
        await LoadHistoryAsync();
        await LoadRetentionAsync();
        await LoadBeepAsync();
    }

    private async Task LoadRetentionAsync()
    {
        try { var days = await _alarmService.GetRetentionDaysAsync(CancellationToken.None); System.Windows.Application.Current.Dispatcher.Invoke(() => { SelectedRetentionDays = RetentionOptions.Contains(days) ? days : 90; CustomRetentionDays = days; }); } catch { }
    }
    private async Task LoadBeepAsync()
    {
        try { var beep = await _alarmService.GetBeepEnabledAsync(CancellationToken.None); System.Windows.Application.Current.Dispatcher.Invoke(() => IsBeepEnabled = beep); } catch { }
    }

    [RelayCommand]
    private async Task UpdateRetentionAsync()
    {
        int days = CustomRetentionDays > 0 ? CustomRetentionDays : SelectedRetentionDays;
        if (await Dialogs.ConfirmAsync("Retention Update", $"Set alarm history retention to {days} days? Older alarms will be purged daily.", "Save"))
        {
            await RunAsync(async () => { await _alarmService.UpdateRetentionAsync(days, CancellationToken.None); Dialogs.Toast("Alarms", $"Retention set to {days} days", ToastKind.Success); });
        }
    }

    [RelayCommand]
    private async Task PurgeNowAsync()
    {
        if (!await Dialogs.ConfirmAsync("Purge Now", $"Delete alarms older than {(CustomRetentionDays > 0 ? CustomRetentionDays : SelectedRetentionDays)} days immediately? This cannot be undone.", "Delete", destructive: true)) return;
        await RunAsync(async () =>
        {
            int days = CustomRetentionDays > 0 ? CustomRetentionDays : SelectedRetentionDays;
            var deleted = await _alarmService.PurgeNowAsync(days, CancellationToken.None);
            Dialogs.Toast("Alarms", $"{deleted} old alarms deleted", ToastKind.Info);
            await LoadHistoryAsync();
        });
    }

    [RelayCommand]
    private void ToggleBeep()
    {
        IsBeepEnabled = !IsBeepEnabled;
    }

    public override Task OnNavigatedFromAsync()
    {
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LoadActiveAlarmsAsync()
    {
        await RunAsync(async () =>
        {
            var active = await _alarmService.GetActiveAlarmsAsync(CancellationToken.None);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ActiveAlarms.Clear();
                foreach (var a in active) ActiveAlarms.Add(a);
            });
        });
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        await RunAsync(async () =>
        {
            var filter = new AlarmHistoryFilter(
                FromUtc: FilterFromDate?.ToUniversalTime(),
                ToUtc: FilterToDate?.ToUniversalTime(),
                Priority: FilterPriority,
                AlarmType: FilterType,
                State: FilterState,
                DeviceName: string.IsNullOrWhiteSpace(FilterDeviceName) ? null : FilterDeviceName,
                TagName: string.IsNullOrWhiteSpace(FilterTagName) ? null : FilterTagName
            );

            var (items, totalCount) = await _alarmService.GetAlarmHistoryAsync(filter, CurrentPage, PageSize, CancellationToken.None);
            
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                HistoryAlarms.Clear();
                foreach (var item in items) HistoryAlarms.Add(item);
                TotalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            });
        });
    }

    [RelayCommand]
    private async Task AcknowledgeAsync(AlarmEvent alarm)
    {
        if (alarm == null) return;
        await RunAsync(async () =>
        {
            await _alarmService.AcknowledgeAsync(alarm.Id, _currentUser.Username ?? "Unknown", CancellationToken.None);
            await LoadActiveAlarmsAsync();
        });
    }

    [RelayCommand]
    private async Task AcknowledgeAllAsync()
    {
        await RunAsync(async () =>
        {
            await _alarmService.AcknowledgeAllAsync(_currentUser.Username ?? "Unknown", CancellationToken.None);
            await LoadActiveAlarmsAsync();
        });
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            _ = LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private void PrevPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            _ = LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterFromDate = null;
        FilterToDate = null;
        FilterPriority = null;
        FilterType = null;
        FilterState = null;
        FilterDeviceName = string.Empty;
        FilterTagName = string.Empty;
        CurrentPage = 1;
        _ = LoadHistoryAsync();
    }

    private void OnAlarmTriggered(object? sender, AlarmTriggeredMessage msg)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(LoadActiveAlarmsAsync);
    }

    private void OnAlarmCleared(object? sender, AlarmClearedMessage msg)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(LoadActiveAlarmsAsync);
    }
}
