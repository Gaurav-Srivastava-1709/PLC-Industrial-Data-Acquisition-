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
    private readonly System.Windows.Threading.DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _autoRefreshing;

    public AlarmsViewModel(IAlarmService alarmService, ICurrentUserService currentUser, IDialogService dialogs, ILogger<AlarmsViewModel> logger) : base(dialogs, logger)
    {
        _alarmService = alarmService;
        _currentUser = currentUser;

        _alarmService.AlarmTriggered += OnAlarmTriggered;
        _alarmService.AlarmCleared += OnAlarmCleared;
        // Industry: badge/count change must refresh the list too — was a no-op before, so the page stayed empty until manual Refresh.
        _alarmService.AlarmCountChanged += (_, _) => _ = RefreshActiveFromDbAsync();
        _autoRefreshTimer.Tick += (_, _) => _ = AutoRefreshAsync();
    }

    [ObservableProperty] private int _selectedTabIndex;

    // Load fresh data immediately on tab switch (Active or History)
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1)
        {
            // Alarm History tab opened — load the latest audit history in the background
            _ = RefreshHistoryQuietAsync();
        }
        else if (value == 0)
        {
            // Active Alarms tab opened — sync the live active alarms
            _ = RefreshActiveFromDbAsync();
        }
    }
    
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
        // Industry SCADA: alarm list auto-refreshes every 2s while visible — no manual Refresh needed.
        _autoRefreshTimer.Start();
    }

    private async Task LoadRetentionAsync()
    {
        try { var days = await _alarmService.GetRetentionDaysAsync(CancellationToken.None); OnUi(() => { SelectedRetentionDays = RetentionOptions.Contains(days) ? days : 90; CustomRetentionDays = days; }); } catch { }
    }
    private async Task LoadBeepAsync()
    {
        try { var beep = await _alarmService.GetBeepEnabledAsync(CancellationToken.None); OnUi(() => IsBeepEnabled = beep); } catch { }
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
        _autoRefreshTimer.Stop();
        return Task.CompletedTask;
    }

    // Industrial SCADA: background sync runs every 2 seconds
    private async Task AutoRefreshAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _autoRefreshing, 1) == 1) return;
        try
        {
            // Sync cross-process DB changes first
            try { await _alarmService.SyncCrossProcessAlarmsAsync(CancellationToken.None); } catch { }
            
            // Auto-refresh data according to the currently selected tab
            if (SelectedTabIndex == 0)
            {
                // Active Alarms tab is open, so refresh the active list
                await RefreshActiveFromDbAsync();
            }
            else if (SelectedTabIndex == 1)
            {
                // Alarm History tab is open, so keep the history audit log updated in the background
                await RefreshHistoryQuietAsync();
            }
        }
        finally { System.Threading.Interlocked.Exchange(ref _autoRefreshing, 0); }
    }

    // Update Active Alarms via diff without screen flicker
    private async Task RefreshActiveFromDbAsync()
    {
        try
        {
            var active = await _alarmService.GetActiveAlarmsAsync(CancellationToken.None);
            OnUi(() =>
            {
                var activeMap = active.ToDictionary(a => a.Id);
                
                // 1. Remove alarms that are no longer active (already cleared) from the list
                for (int i = ActiveAlarms.Count - 1; i >= 0; i--)
                {
                    if (!activeMap.ContainsKey(ActiveAlarms[i].Id))
                    {
                        ActiveAlarms.RemoveAt(i);
                    }
                }

                // 2. For alarms already in the list, sync their State (Active vs Acknowledged) and AckBy
                for (int i = 0; i < ActiveAlarms.Count; i++)
                {
                    var existing = ActiveAlarms[i];
                    if (activeMap.TryGetValue(existing.Id, out var fresh))
                    {
                        if (existing.State != fresh.State || existing.AcknowledgedBy != fresh.AcknowledgedBy)
                        {
                            // Replacing the item makes the DataGrid row render the updated status immediately
                            ActiveAlarms[i] = fresh;
                        }
                    }
                }

                // 3. Add newly triggered alarms to the list
                var existingIds = ActiveAlarms.Select(a => a.Id).ToHashSet();
                foreach (var a in active)
                {
                    if (!existingIds.Contains(a.Id))
                    {
                        ActiveAlarms.Add(a);
                    }
                }
            });
        }
        catch { }
    }

    // Generate a reusable object for the history filter
    private AlarmHistoryFilter CreateCurrentHistoryFilter()
    {
        return new AlarmHistoryFilter(
            FromUtc: FilterFromDate?.ToUniversalTime(),
            ToUtc: FilterToDate?.ToUniversalTime(),
            Priority: FilterPriority,
            AlarmType: FilterType,
            State: FilterState,
            DeviceName: string.IsNullOrWhiteSpace(FilterDeviceName) ? null : FilterDeviceName,
            TagName: string.IsNullOrWhiteSpace(FilterTagName) ? null : FilterTagName
        );
    }

    // Keep Alarm History fully in sync with the DB in the background without a full-screen spinner
    private async Task RefreshHistoryQuietAsync()
    {
        try
        {
            var filter = CreateCurrentHistoryFilter();
            var (items, totalCount) = await _alarmService.GetAlarmHistoryAsync(filter, CurrentPage, PageSize, CancellationToken.None);
            
            OnUi(() =>
            {
                // If the item count differs or the list was empty, do a fresh reload
                if (HistoryAlarms.Count != items.Count)
                {
                    HistoryAlarms.Clear();
                    foreach (var item in items) HistoryAlarms.Add(item);
                }
                else
                {
                    // Check item-by-item so the scroll position or selection does not jump
                    for (int i = 0; i < items.Count; i++)
                    {
                        var cur = HistoryAlarms[i];
                        var fresh = items[i];

                        if (cur.Id != fresh.Id || 
                            cur.State != fresh.State || 
                            cur.AcknowledgedBy != fresh.AcknowledgedBy || 
                            cur.ClearedAtUtc != fresh.ClearedAtUtc)
                        {
                            // Status change (e.g. Active -> Acknowledged) reflects immediately in the UI
                            HistoryAlarms[i] = fresh;
                        }
                    }
                }
                TotalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            });
        }
        catch { }
    }

    [RelayCommand]
    private async Task LoadActiveAlarmsAsync()
    {
        await RunAsync(async () =>
        {
            var active = await _alarmService.GetActiveAlarmsAsync(CancellationToken.None);
            OnUi(() =>
            {
                ActiveAlarms.Clear();
                foreach (var a in active) ActiveAlarms.Add(a);
            });
            // While active loads, also prepare history in the background
            await RefreshHistoryQuietAsync();
        });
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        await RunAsync(async () =>
        {
            var filter = CreateCurrentHistoryFilter();
            var (items, totalCount) = await _alarmService.GetAlarmHistoryAsync(filter, CurrentPage, PageSize, CancellationToken.None);
            
            OnUi(() =>
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
            // Save acknowledgment to DB (with operator username)
            await _alarmService.AcknowledgeAsync(alarm.Id, _currentUser.Username ?? "Unknown", CancellationToken.None);
            
            // 1. Update active alarms (button disable and opacity will update)
            await RefreshActiveFromDbAsync();

            // 2. ISA-18.2 Standard: Update history log immediately so 'Acknowledged' and 'admin' appear in the audit trail without delay
            await RefreshHistoryQuietAsync();
        });
    }

    [RelayCommand]
    private async Task AcknowledgeAllAsync()
    {
        await RunAsync(async () =>
        {
            // Acknowledge all active alarms at once
            await _alarmService.AcknowledgeAllAsync(_currentUser.Username ?? "Unknown", CancellationToken.None);
            
            // Immediately update both Active and History grids
            await RefreshActiveFromDbAsync();
            await RefreshHistoryQuietAsync();
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
        OnUi(() =>
        {
            // Insert incrementally at the top, without clearing the whole list
            var alarm = new AlarmEvent
            {
                Id = msg.AlarmEventId,
                DeviceName = msg.DeviceName,
                TagName = msg.TagName,
                AlarmType = msg.AlarmType,
                Priority = msg.Priority,
                TriggerValue = msg.TriggerValue,
                ThresholdValue = msg.ThresholdValue,
                EngineeringUnit = msg.EngineeringUnit,
                TriggeredAtUtc = msg.TriggeredAtUtc,
                State = AlarmState.Active
            };
            ActiveAlarms.Insert(0, alarm);
        });
    }

    private void OnAlarmCleared(object? sender, AlarmClearedMessage msg)
    {
        OnUi(() =>
        {
            // Find and remove the cleared alarm from Active list
            var existing = ActiveAlarms.FirstOrDefault(a => a.Id == msg.AlarmEventId);
            if (existing != null)
            {
                ActiveAlarms.Remove(existing);
            }
        });
    }
}
