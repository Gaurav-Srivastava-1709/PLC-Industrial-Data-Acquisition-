using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.UI.ViewModels;

public partial class DataTransferViewModel : ObservableObject
{
    private readonly IDataTransferService _dataTransferService;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private int _pendingRecordsCount;

    [ObservableProperty]
    private long _totalTransferredCount;

    [ObservableProperty]
    private int _transferErrorsCount;

    [ObservableProperty]
    private string _lastSuccessfulTransferAt = "Never";

    [ObservableProperty]
    private string _lastErrorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    // Progress bar bindable properties
    [ObservableProperty]
    private int _currentProgressPercent;
    
    [ObservableProperty]
    private string _progressMessage = "Idle";

    public DataTransferViewModel(IDataTransferService dataTransferService)
    {
        _dataTransferService = dataTransferService;
        
        IsRunning = _dataTransferService.IsRunning;

        // Hook up to service events
        _dataTransferService.ServiceStatusChanged += (s, isRunning) => 
        {
            App.Current.Dispatcher.Invoke(() => IsRunning = isRunning);
        };

        _dataTransferService.TransferProgress += (s, e) => 
        {
            App.Current.Dispatcher.Invoke(() => 
            {
                ProgressMessage = e.Message;
                // Calculate percentage based on current batch vs total pending
                if (e.TotalPending + e.CurrentBatchTransferred > 0)
                {
                    CurrentProgressPercent = (int)((e.CurrentBatchTransferred / (double)(e.TotalPending + e.CurrentBatchTransferred)) * 100);
                }
                UpdateStats();
            });
        };

        // UI Refresh Timer for stats (every 5 seconds)
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (s, e) => UpdateStats();
        _refreshTimer.Start();

        UpdateStats();
    }

    private void UpdateStats()
    {
        var stats = _dataTransferService.GetStatistics();
        
        PendingRecordsCount = stats.PendingRecordsCount;
        TotalTransferredCount = stats.TotalTransferredCount;
        TransferErrorsCount = stats.TransferErrorsCount;
        
        LastSuccessfulTransferAt = stats.LastSuccessfulTransferAt.HasValue 
            ? stats.LastSuccessfulTransferAt.Value.ToLocalTime().ToString("g") 
            : "Never";

        if (!string.IsNullOrEmpty(stats.LastErrorMessage))
        {
            LastErrorMessage = stats.LastErrorMessage;
            HasError = true;
        }
        else
        {
            HasError = false;
        }
    }

    [RelayCommand]
    private async Task ToggleServiceAsync()
    {
        if (IsRunning)
            await _dataTransferService.StopAsync();
        else
            await _dataTransferService.StartAsync();
    }

    [RelayCommand]
    private async Task TransferNowAsync()
    {
        ProgressMessage = "Manual transfer triggered...";
        await _dataTransferService.TransferNowAsync();
    }
}
