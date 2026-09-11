using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui;
using WTT.Application.Interfaces;
using WTT.Application.Security;
using WTT.Domain.Entities;
using WTT.Domain.ValueObjects;
using WTT.UI.Services;

namespace WTT.UI.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly IDeviceService _devices;
    private readonly IDataCaptureController _capture;
    private readonly IDataTransferController _transfer;
    private readonly ICurrentUserService _currentUser;
    private readonly INavigationService _navigation;
    private readonly NavigationContext _navContext;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Queue<(long Readings, long Buffer)> _trend = new();

    public DashboardViewModel(IDeviceService devices, IDataCaptureController capture, IDataTransferController transfer, ICurrentUserService currentUser,
        INavigationService navigation, NavigationContext navContext, IDialogService dialogs, ILogger<DashboardViewModel> logger) : base(dialogs, logger)
    {
        _devices = devices;
        _capture = capture;
        _transfer = transfer;
        _currentUser = currentUser;
        _navigation = navigation;
        _navContext = navContext;
        _timer.Tick += async (_, _) => await RefreshStatsAsync();
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = new();

    [ObservableProperty] private int _devicesTotal;
    [ObservableProperty] private int _devicesRunning;
    [ObservableProperty] private int _devicesConnected;
    [ObservableProperty] private long _readingsToday;
    [ObservableProperty] private long _errorsToday;
    [ObservableProperty] private long _bufferedReadings;
    [ObservableProperty] private long _transferredToday;
    [ObservableProperty] private long _failedBatchesToday;
    [ObservableProperty] private DateTime? _lastTransferUtc;
    [ObservableProperty] private bool _capturePaused;
    [ObservableProperty] private bool _transferPaused;
    [ObservableProperty] private PointCollection _readingTrend = new();
    [ObservableProperty] private PointCollection _bufferTrend = new();
    [ObservableProperty] private string _trendSummary = "Collecting trend data...";

    public bool CanControl => _currentUser.HasPermission(Permission.ControlCapture);
    public bool CanManageDevices => _currentUser.HasPermission(Permission.ManageDevices);

    public override async Task OnNavigatedToAsync()
    {
        _capture.DeviceStatusChanged += OnStatusChanged;
        await LoadDevicesAsync();
        await RefreshStatsAsync();
        _timer.Start();
    }

    public override Task OnNavigatedFromAsync()
    {
        _timer.Stop();
        _capture.DeviceStatusChanged -= OnStatusChanged;
        return Task.CompletedTask;
    }

    private async Task LoadDevicesAsync()
    {
        await RunAsync(async () =>
        {
            var list = await _devices.GetAllAsync();
            Devices.Clear();
            foreach (var d in list)
                Devices.Add(new DeviceCardViewModel(d, _capture.GetStatus(d.Id)));
        });
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var c = await _capture.GetStatisticsAsync();
            var t = await _transfer.GetStatisticsAsync();
            DevicesTotal = c.DevicesTotal; DevicesRunning = c.DevicesRunning; DevicesConnected = c.DevicesConnected;
            ReadingsToday = c.ReadingsToday; ErrorsToday = c.ErrorsToday; BufferedReadings = c.BufferedReadings;
            TransferredToday = t.TransferredToday; FailedBatchesToday = t.FailedBatchesToday; LastTransferUtc = t.LastSuccessAtUtc;
            CapturePaused = _capture.IsPaused; TransferPaused = _transfer.IsPaused;
            AddTrendPoint(ReadingsToday, BufferedReadings);
            foreach (var card in Devices) card.Update(_capture.GetStatus(card.Id));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dashboard refresh failed.");
        }
    }

    private void AddTrendPoint(long readings, long buffer)
    {
        _trend.Enqueue((readings, buffer));
        while (_trend.Count > 60) _trend.Dequeue();
        ReadingTrend = Normalize(_trend.Select(x => x.Readings).ToList());
        BufferTrend = Normalize(_trend.Select(x => x.Buffer).ToList());
        TrendSummary = $"Last {_trend.Count * 3} seconds · readings {readings:N0} · buffered {buffer:N0}";
    }

    private static PointCollection Normalize(IReadOnlyList<long> values)
    {
        var points = new PointCollection();
        if (values.Count == 0) return points;
        var min = values.Min(); var max = values.Max(); var range = Math.Max(1, max - min);
        for (var i = 0; i < values.Count; i++)
            points.Add(new Point(values.Count == 1 ? 0 : i * 300.0 / (values.Count - 1), 80 - (values[i] - min) * 76.0 / range));
        return points;
    }

    private void OnStatusChanged(object? sender, DeviceCaptureStatus status) =>
        OnUi(() => Devices.FirstOrDefault(d => d.Id == status.DeviceId)?.Update(status));

    [RelayCommand]
    private async Task ToggleCaptureAsync(DeviceCardViewModel card)
    {
        if (card is null) return;
        await RunAsync(async () =>
        {
            await _devices.SetRunStateAsync(card.Id, captureEnabled: !card.IsCaptureEnabled, monitorEnabled: null);
            card.IsCaptureEnabled = !card.IsCaptureEnabled;
            Dialogs.Toast("Capture", $"{card.Name}: capture {(card.IsCaptureEnabled ? "started" : "stopped")}.", ToastKind.Success);
        });
    }

    [RelayCommand]
    private async Task ToggleMonitorAsync(DeviceCardViewModel card)
    {
        if (card is null) return;
        await RunAsync(async () =>
        {
            await _devices.SetRunStateAsync(card.Id, captureEnabled: null, monitorEnabled: !card.IsMonitorEnabled);
            card.IsMonitorEnabled = !card.IsMonitorEnabled;
        });
    }

    [RelayCommand]
    private void OpenDevice(DeviceCardViewModel card)
    {
        if (card is null) return;
        _navContext.SelectedDeviceId = card.Id;
        _navigation.Navigate(typeof(Views.Pages.DeviceConfigPage));
    }

    [RelayCommand]
    private void OpenLiveData(DeviceCardViewModel card)
    {
        if (card is null) return;
        _navContext.SelectedDeviceId = card.Id;
        _navigation.Navigate(typeof(Views.Pages.LiveDataPage));
    }

    [RelayCommand]
    private void ToggleCapturePause()
    {
        if (_capture.IsPaused) _capture.Resume(); else _capture.Pause();
        CapturePaused = _capture.IsPaused;
    }

    [RelayCommand]
    private void ToggleTransferPause()
    {
        if (_transfer.IsPaused) _transfer.Resume(); else _transfer.Pause();
        TransferPaused = _transfer.IsPaused;
    }

    [RelayCommand]
    private async Task TransferNowAsync()
    {
        await RunAsync(async () =>
        {
            var n = await _transfer.TransferNowAsync();
            Dialogs.Toast("Data transfer", n == 0 ? "Nothing to transfer." : $"{n:N0} readings transferred.", ToastKind.Success);
            await RefreshStatsAsync();
        }, "Transfer failed");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDevicesAsync();
        await RefreshStatsAsync();
    }
}

/// <summary>One device tile on the dashboard.</summary>
public partial class DeviceCardViewModel : ObservableObject
{
    public DeviceCardViewModel(Device device, DeviceCaptureStatus? status)
    {
        Id = device.Id;
        Name = device.Name;
        Summary = $"{device.Model} · {device.Host}:{device.Port} · {device.PollIntervalMs} ms";
        Mode = device.CaptureMode.ToString();
        IsCaptureEnabled = device.IsCaptureEnabled;
        IsMonitorEnabled = device.IsMonitorEnabled;
        IsEnabled = device.IsEnabled;
        HasServer = device.Server is not null;
        TransferEnabled = device.Server?.IsEnabled ?? false;
        Update(status);
    }

    public int Id { get; }
    public string Name { get; }
    public string Summary { get; }
    public string Mode { get; }
    public bool IsEnabled { get; }
    public bool HasServer { get; }
    public bool TransferEnabled { get; }

    [ObservableProperty] private bool _isCaptureEnabled;
    [ObservableProperty] private bool _isMonitorEnabled;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private string? _triggerText;
    [ObservableProperty] private DateTime? _lastScanUtc;
    [ObservableProperty] private long _scanCount;
    [ObservableProperty] private long _readingsWritten;
    [ObservableProperty] private long _errorCount;
    [ObservableProperty] private string? _lastError;

    public void Update(DeviceCaptureStatus? s)
    {
        if (s is null)
        {
            IsRunning = false; IsConnected = false; StatusText = IsEnabled ? "Stopped" : "Disabled"; TriggerText = null;
            return;
        }
        IsRunning = s.IsRunning;
        IsConnected = s.IsConnected;
        LastScanUtc = s.LastScanUtc;
        ScanCount = s.ScanCount;
        ReadingsWritten = s.ReadingsWritten;
        ErrorCount = s.ErrorCount;
        LastError = s.LastError;
        TriggerText = s.LastTriggerState is null ? null : (s.LastTriggerState.Value ? "Trigger ON" : "Trigger OFF");
        StatusText = !s.IsRunning ? "Stopped"
            : !s.IsConnected ? (s.ReconnectAttempts > 0 ? $"Reconnecting (attempt {s.ReconnectAttempts})" : "Connecting…")
            : s.IsCapturing ? "Capturing" : "Monitoring";
    }
}
